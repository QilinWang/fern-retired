using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// The ten operations the one-store schema exposes, and nothing else.
///
/// Owner's cut of 2026-09-11: a registered Study, its cells and their attempts
/// are the whole execution model. This type is the complete write and read
/// surface over the six tables; there is no second door into them.
///
/// Two properties are enforced HERE rather than by a convention a caller could
/// forget:
///
/// FENCING LIVES IN THE WRITE. Every update a worker makes to its own attempt
/// carries the epoch it was claimed with: <c>UPDATE attempts ... WHERE id = ?
/// AND fencing_epoch = ?</c>. An update that touches ZERO rows is the refusal —
/// the attempt was displaced by a successor, and the displaced worker's late
/// write matches nothing. There is no read-then-act anywhere in this file,
/// because a read-then-act has a window and a conditional update does not.
///
/// ONE TRANSACTION PER DECISION. <see cref="TryStartAttempt"/> checks the study
/// is in state <c>run</c>, the cell has no accepted attempt and no other open
/// attempt, and inserts the attempt, all inside one BEGIN IMMEDIATE. Two
/// schedulers racing the same cell therefore produce one attempt and one typed
/// refusal, not two attempts.
/// </summary>
public static class RegistryOperations
{
    /// <summary>The states a study may be asked to be in.</summary>
    public const string StateRun = "run";
    public const string StatePause = "pause";
    public const string StateCancel = "cancel";

    /// <summary>The states an attempt may be in. `opened` and `running` are open; the rest are terminal.</summary>
    public const string AttemptOpened = "opened";
    public const string AttemptRunning = "running";
    public const string AttemptCompleted = "completed";
    /// <summary>
    /// How often a supervisor renews the attempt it is running, and how long an
    /// attempt may be silent before the store stops believing anyone is behind
    /// it. They live here, with the writes that mean them: `RenewAttempt` stamps
    /// the beat, `TryStartAttempt` counts only fresh ones against a Site's
    /// capacity, and `retry-cell` uses the same bound to decide whether an open
    /// attempt is stranded. One number, three readers.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Ten missed beats: a supervisor that is gone rather than one that is busy.
    /// At a two-minute cadence that is twenty minutes of silence.
    /// </summary>
    public static readonly TimeSpan StaleAfter = HeartbeatInterval * 10;

    /// <summary>What a Site admits at once when its capacity declares nothing: one cell.</summary>
    public const long DefaultSlots = 1;

    /// <summary>The event that records which card an attempt was granted, and under which generation.</summary>
    public const string DeviceGrantEvent = "device_grant";

    /// <summary>
    /// How many attempts one cell gets before a person has to say otherwise.
    ///
    /// Ported from the retired per-stage retry budget (`StageRetrySpecV1`),
    /// which a compiled study declared and the resident scheduler enforced. The
    /// counter is already in the schema: `attempts.ordinal` is the cell's own
    /// count of tries, so nothing had to be invented to count them — only to act
    /// on the count. Three, unless the study declares its own
    /// (`retry.max_total_attempts` in its compiled document) or the operator
    /// names one.
    /// </summary>
    public const long DefaultMaxAttempts = 3;

    public const string AttemptFailed = "failed";
    public const string AttemptLost = "lost";
    public const string AttemptCancelled = "cancelled";

    // CHG 2026-09-11 night (slice 2 of the cell port, design note
    // docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
    // rows 8, 11, 13). The retired StageAttemptAuthority's four retry classes,
    // as the `failure_class` field of the failure record the worker writes;
    // the retired dispatch block's ten-minute window before a failed unit is
    // offered again, here per cell; and the event a person's retry-cell writes,
    // which lifts both.
    public const string FailureDeterministic = "deterministic";
    public const string FailureTransient = "transient";
    public const string FailureSite = "site";
    public const string FailureHuman = "human";
    public const string RetryRequestedEvent = "retry_requested";
    public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(10);

    // CHG 2026-09-11 night (slice 3 of the cell port, rows 4, 5, 30). A card is
    // held by a `device_grants` row (the retired DeviceGrantAuthority, unchanged)
    // owned by the attempt as "attempt:<id>"; one cell per card, which is what
    // 3c285ad3 declared and what a training process needs. A Site disabled by a
    // person is a standing hold over its scope, read by the claim and the loop.
    public const long DeviceCapacity = 1;

    public static string GrantOwner(long attemptId) => "attempt:" + attemptId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Hand back every card this attempt holds, with the reason; nothing to hand back is fine.</summary>
    static void ReleaseDeviceGrantsOf(SqliteConnection connection, long attemptId, string reason)
    {
        var grants = new List<string>();
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT grant_id FROM device_grants WHERE owner_attempt_id = $owner AND released_at_ms IS NULL";
            lookup.Parameters.AddWithValue("$owner", GrantOwner(attemptId));
            using var rows = lookup.ExecuteReader();
            while (rows.Read()) grants.Add(rows.GetString(0));
        }
        foreach (var grant in grants)
        {
            var released = DeviceGrantAuthority.ReleaseInTransaction(connection, grant, GrantOwner(attemptId), reason);
            if (released.TryGetRefusal(out var refusal))
            {
                // The grant was selected by this attempt's own owner id a line
                // above, so a refusal here is a broken invariant, not a case.
                throw new InvalidOperationException($"attempt {attemptId} could not hand back device grant {grant}: {refusal.Detail}");
            }
        }
    }

    // ── the records the operations speak in ──────────────────────────────────

    /// <summary>One compiled cell, as a study registration declares it.</summary>
    public sealed record CompiledCell(
        long Ordinal,
        string Name,
        string Dataset,
        long Seed,
        string ConfigJson,
        string RequirementsJson,
        string RetentionJson);
    // CHG 2026-09-26: ConfigRegionsJson, the compiler's digest per section of the
    // configuration (carried since 2026-09-14), left with cell_config_regions at schema 18.

    /// <summary>A registered study and the cells written with it.</summary>
    public sealed record RegisteredStudy(long StudyId, IReadOnlyList<long> CellIds);

    public sealed record RegisteredSource(long SourceId, bool AlreadyRegistered);

    /// <param name="KeptKeys">On a re-registration, the keys another command recorded on the Site that were kept.</param>
    /// <param name="RemovedKeys">On a re-registration, the owned keys the new declaration no longer carries.</param>
    public sealed record RegisteredSite(
        long SiteId, bool AlreadyRegistered,
        IReadOnlyList<string>? KeptKeys = null, IReadOnlyList<string>? RemovedKeys = null);

    /// <summary>A cell the scheduler may claim: what it is, and what it needs.</summary>
    public sealed record RunnableCell(
        long CellId,
        long StudyId,
        long SourceId,
        long Ordinal,
        string Name,
        string Dataset,
        long Seed,
        string RequirementsJson,
        [property: System.Text.Json.Serialization.JsonRequired] string ConfigJson);

    /// <summary>A registered Site and what it declares.</summary>
    public sealed record Site(long SiteId, string Name, string CapabilitiesJson, string CapacityJson);

    /// <summary>One claimed attempt: its id, its ordinal, and the epoch every later write must carry.</summary>
    /// <param name="Device">
    /// The card this attempt was granted, when its Site declares its cards, and
    /// null when the Site is counted by slots instead. The supervisor puts it in
    /// the environment it launches the trainer with, so two cells on one machine
    /// land on two cards.
    /// </param>
    /// <param name="CodeFrom">
    /// Where the cell's code comes from on its Site (<see cref="CodeFromWorkingTree"/>
    /// or <see cref="CodeFromCommit"/>), read from the Site's declaration at the
    /// claim; <paramref name="SourceRoot"/> is the Site's repository. Null from a
    /// Home older than 2026-09-26, which only knew installed sources.
    /// </param>
    public sealed record StartedAttempt(
        long AttemptId, long Ordinal, long FencingEpoch, string? Device = null, string? SourceRoot = null, string? CodeFrom = null);

    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 2; owner: build both A and B, choose a default later): where a cell's
    // code comes from, declared per Site as `code_from` in its execution profile.
    // working-tree (A): the Site's git repository as it is when the cell starts.
    // commit (B): the source's declared files read from git at its registered
    // commit into a directory made for the attempt and deleted after it.
    // CHG 2026-09-26 (slice 4; owner: "旧的做法全是垃圾", "先把删除全部做好"): the
    // third answer, installed-source (the directory install-study-source wrote,
    // recorded in site_sources), is retired with everything that served it.
    public const string CodeFromWorkingTree = "working-tree";
    public const string CodeFromCommit = "commit";

    // CHG 2026-09-26 (cards-without-hashes, schema 21): how a source was registered, in
    // sources.registered_from, in the words code_from uses; unknown for a source
    // registered under a scheme older than the two prefixes bundle_sha256 carried.
    public const string RegisteredFromCommit = CodeFromCommit;
    public const string RegisteredFromWorkingTree = CodeFromWorkingTree;
    public const string RegisteredFromUnknown = "unknown";

    /// <summary>A Site's declared answer to where a cell's code comes from, and its repository for the two that use one.</summary>
    public sealed record SiteCode(string From, string? Repository);

    /// <summary>
    /// Read <c>code_from</c> and <c>repository</c> from a Site's capabilities. The
    /// owner chooses a default later, so until then a Site says which of the two
    /// it is: absence, installed-source (retired on 2026-09-26), a value this code
    /// does not know, or no absolute repository is refused by name rather than
    /// read as anything. A Site registered before that date carries no
    /// <c>code_from</c> and is refused until it is registered again.
    /// </summary>
    public static RegistryResult<SiteCode> CodeOf(string capabilitiesJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(capabilitiesJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("code_from", out var declared))
            {
                return RegistryResult<SiteCode>.Rejected(Invalid(
                    $"the Site declares no code_from; installed sources were retired on 2026-09-26, so register it again "
                    + $"with code_from {CodeFromWorkingTree} or {CodeFromCommit} and its repository"));
            }
            var from = declared.ValueKind == System.Text.Json.JsonValueKind.String ? declared.GetString() : null;
            if (from is not (CodeFromWorkingTree or CodeFromCommit))
            {
                return RegistryResult<SiteCode>.Rejected(Invalid(
                    $"the Site declares code_from {declared.GetRawText()}; it is {CodeFromWorkingTree} or {CodeFromCommit} "
                    + "(installed sources were retired on 2026-09-26)"));
            }
            var repository = root.TryGetProperty("repository", out var named) && named.ValueKind == System.Text.Json.JsonValueKind.String
                ? named.GetString()
                : null;
            if (repository is not { Length: > 0 } || !Path.IsPathRooted(repository))
            {
                return RegistryResult<SiteCode>.Rejected(Invalid(
                    $"the Site declares code_from {from} and no absolute repository to take the code from"));
            }
            return RegistryResult<SiteCode>.Ok(new SiteCode(from, repository));
        }
        catch (System.Text.Json.JsonException error)
        {
            return RegistryResult<SiteCode>.Rejected(Invalid($"the Site's capabilities are not readable JSON: {error.Message}"));
        }
    }

    /// <summary>A registered study as a reader sees it.</summary>
    public sealed record StudyView(
        long StudyId,
        string Name,
        long Revision,
        string RequestedState,
        long SourceId,
        string CompiledStudyJson,
        string? SupervisorLease,
        long SupervisorEpoch,
        int CellCount,
        int AcceptedCellCount);

    // ── refusals ─────────────────────────────────────────────────────────────

    static RegistryRefusal Invalid(string detail) => new("refused", "invalid_request", detail);

    static RegistryRefusal NotFound(string detail) => new("refused", "not_found", detail);

    /// <summary>
    /// The refusal a displaced worker gets. It carries the epoch it presented,
    /// so a reader of the refusal can see it was fenced rather than guess.
    /// </summary>
    static RegistryRefusal Fenced(long attemptId, long presentedEpoch) =>
        new("refused", "fenced",
            $"attempt {attemptId} is no longer current at fencing epoch {presentedEpoch}; a successor holds it",
            StaleEpoch: presentedEpoch);

    static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    // ── 1. RegisterStudy ──────────────────────────────────────────────────────

    /// <summary>
    /// The frozen source a study will be registered against: the commit it was
    /// taken at, the files it declares, and the digest that names the bundle.
    ///
    /// Replay is by <c>bundle_sha256</c>, which is what the freeze computes
    /// over the declared bytes: freezing an unchanged tree twice is one row,
    /// because it is one source. A changed file changes that digest and mints
    /// a new row, so the study registered against the old id keeps naming the
    /// bytes it was registered with.
    ///
    /// CHG 2026-09-26 (cards-without-hashes, schema 21): no digest. Replay is by the
    /// commit, the declared files as written, and how the source was registered,
    /// compared as text: registering the same commit with the same declaration twice
    /// is one row. A declaration no longer carries each file's digest, so an edit
    /// that keeps a file's path and size at the same commit is the same source; a
    /// cell runs the commit's bytes (CellCode), which is what that row names.
    /// </summary>
    public static RegistryResult<RegisteredSource> RegisterSource(
        RegistryWriter writer,
        string commitSha,
        string declaredFilesJson,
        string registeredFrom)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            return RegistryResult<RegisteredSource>.Rejected(Invalid("a source is registered at a commit"));
        }
        if (registeredFrom is not (RegisteredFromCommit or RegisteredFromWorkingTree))
        {
            return RegistryResult<RegisteredSource>.Rejected(Invalid(
                $"a source is registered from {RegisteredFromCommit} or {RegisteredFromWorkingTree}, not {registeredFrom}"));
        }
        try
        {
            using var declared = System.Text.Json.JsonDocument.Parse(declaredFilesJson);
            if (declared.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array
                || declared.RootElement.GetArrayLength() == 0)
            {
                return RegistryResult<RegisteredSource>.Rejected(
                    Invalid("a source declares a non-empty array of files"));
            }
        }
        catch (System.Text.Json.JsonException invalid)
        {
            return RegistryResult<RegisteredSource>.Rejected(
                Invalid($"a source's declared files must be valid JSON: {invalid.Message}"));
        }

        return writer.WriteImmediate(connection =>
        {
            using (var existing = connection.CreateCommand())
            {
                existing.CommandText =
                    "SELECT id FROM sources WHERE commit_sha = $commit AND declared_files_json = $declared AND registered_from = $from";
                existing.Parameters.AddWithValue("$commit", commitSha);
                existing.Parameters.AddWithValue("$declared", declaredFilesJson);
                existing.Parameters.AddWithValue("$from", registeredFrom);
                if (existing.ExecuteScalar() is { } found)
                {
                    return RegistryResult<RegisteredSource>.Ok(
                        new RegisteredSource(Convert.ToInt64(found), AlreadyRegistered: true));
                }
            }

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ($commit, $declared, $from, $now) RETURNING id
                """;
            insert.Parameters.AddWithValue("$commit", commitSha);
            insert.Parameters.AddWithValue("$declared", declaredFilesJson);
            insert.Parameters.AddWithValue("$from", registeredFrom);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return RegistryResult<RegisteredSource>.Ok(
                new RegisteredSource(Convert.ToInt64(insert.ExecuteScalar()), AlreadyRegistered: false));
        });
    }

    /// <summary>
    /// A Worker Site this store may place cells on: its name, what it can do,
    /// and how much of it there is.
    ///
    /// Replay is by name, because a Site IS its name here: registering the same
    /// machine twice with a new profile updates what it declares rather than
    /// creating a second row that the scheduler would treat as a second machine.
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-25 (docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started,
    /// slice S1): a re-registration no longer rewrites the capabilities column
    /// whole. Other commands record keys on the same row — the installation
    /// receipt, the machine and environment admissions, and models environment
    /// admission added — and a re-registration after an address change erased
    /// them on 2026-09-25. Now the declaration's keys replace, the keys in
    /// <paramref name="ownedKeys"/> that the declaration no longer carries are
    /// removed, and every other key is kept. The capacity column has no other
    /// writer and is still replaced.
    /// </remarks>
    public static RegistryResult<RegisteredSite> RegisterSite(
        RegistryWriter writer,
        string name,
        string capabilitiesJson,
        string capacityJson,
        IReadOnlyCollection<string>? ownedKeys = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return RegistryResult<RegisteredSite>.Rejected(Invalid("a Site is registered under a name"));
        }

        return writer.WriteImmediate(connection =>
        {
            using (var existing = connection.CreateCommand())
            {
                existing.CommandText = "SELECT id FROM sites WHERE name = $name";
                existing.Parameters.AddWithValue("$name", name);
                if (existing.ExecuteScalar() is { } found)
                {
                    var siteId = Convert.ToInt64(found);
                    using var stored = connection.CreateCommand();
                    stored.CommandText = "SELECT capabilities_json FROM sites WHERE id = $id";
                    stored.Parameters.AddWithValue("$id", siteId);
                    if (MergeCapabilities(stored.ExecuteScalar() as string, capabilitiesJson, ownedKeys ?? [])
                        is not { } mergedRow)
                    {
                        // What other commands recorded cannot be read, so it
                        // cannot be kept; overwriting it would lose it silently.
                        return RegistryResult<RegisteredSite>.Rejected(Invalid(
                            $"Site {name}'s stored capabilities are not a JSON object; nothing was replaced, "
                            + "because what other commands recorded there could not be kept"));
                    }
                    var (merged, kept, removed) = mergedRow;
                    using var update = connection.CreateCommand();
                    update.CommandText =
                        "UPDATE sites SET capabilities_json = $capabilities, capacity_json = $capacity WHERE id = $id";
                    update.Parameters.AddWithValue("$capabilities", merged);
                    update.Parameters.AddWithValue("$capacity", capacityJson);
                    update.Parameters.AddWithValue("$id", siteId);
                    update.ExecuteNonQuery();
                    return RegistryResult<RegisteredSite>.Ok(new RegisteredSite(siteId, AlreadyRegistered: true, kept, removed));
                }
            }

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ($name, $capabilities, $capacity, $now) RETURNING id
                """;
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$capabilities", capabilitiesJson);
            insert.Parameters.AddWithValue("$capacity", capacityJson);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return RegistryResult<RegisteredSite>.Ok(
                new RegisteredSite(Convert.ToInt64(insert.ExecuteScalar()), AlreadyRegistered: false));
        });
    }

    /// <summary>
    /// Register one compiled study as a study and its cells, in one transaction.
    ///
    /// A study is identified by its name and revision, never by a digest: the
    /// UNIQUE constraint is what refuses a duplicate, and the refusal says so.
    /// Every cell arrives with the bytes of its payload already hashed by
    /// whoever compiled it; this operation stores the document AND the sha, and
    /// verifies nothing, because verification belongs at the hop that moves the
    /// bytes, not at the one that files them.
    /// </summary>
    // CHG 2026-09-26: WriteConfigRegions, which wrote the compiler's region digests one
    // row per section into cell_config_regions, left with the table at schema 18.

    public static RegistryResult<RegisteredStudy> RegisterStudy(
        RegistryWriter writer,
        string name,
        long revision,
        string compiledStudyJson,
        long sourceId,
        IReadOnlyList<CompiledCell> cells)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return RegistryResult<RegisteredStudy>.Rejected(Invalid("a study is registered under a name"));
        }
        if (cells.Count == 0)
        {
            return RegistryResult<RegisteredStudy>.Rejected(Invalid($"study {name} declares no cells; a study with nothing to run is not a study"));
        }
        var ordinals = new HashSet<long>();
        foreach (var cell in cells)
        {
            if (!ordinals.Add(cell.Ordinal))
            {
                return RegistryResult<RegisteredStudy>.Rejected(Invalid($"study {name} declares cell ordinal {cell.Ordinal} twice"));
            }
        }

        return writer.WriteImmediate(connection =>
        {
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT id FROM studies WHERE name = $name AND revision = $revision";
                exists.Parameters.AddWithValue("$name", name);
                exists.Parameters.AddWithValue("$revision", revision);
                if (exists.ExecuteScalar() is not null)
                {
                    return RegistryResult<RegisteredStudy>.Rejected(
                        Invalid($"study {name} revision {revision} is already registered; a revision is registered once"));
                }
            }
            if (!SourceExists(connection, sourceId))
            {
                return RegistryResult<RegisteredStudy>.Rejected(NotFound($"source {sourceId} is not registered"));
            }
            // CHG 2026-09-26: a study whose declared data inputs are not in the
            // source it runs from is refused here, where one person reads one
            // sentence, rather than by every cell in turn on a machine that was
            // never sent the recording.
            var lacking = DeclaredInputsTheSourceLacks(connection, sourceId, compiledStudyJson);
            if (lacking.Count > 0)
            {
                return RegistryResult<RegisteredStudy>.Rejected(Invalid(
                    $"source {sourceId} does not carry the data this study declares: "
                    + string.Join(", ", lacking)
                    + $". Register the source with the compiled study so its inputs join it "
                    + "(register-study-source --study <compiled.json>); without that flag a source is the code alone."));
            }

            var now = Now();
            long studyId;
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO studies (name, revision, compiled_study_json, source_id, requested_state, created_at)
                    VALUES ($name, $revision, $compiled, $source, 'run', $now)
                    RETURNING id
                    """;
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$revision", revision);
                insert.Parameters.AddWithValue("$compiled", compiledStudyJson);
                insert.Parameters.AddWithValue("$source", sourceId);
                insert.Parameters.AddWithValue("$now", now);
                studyId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var cellIds = new List<long>(cells.Count);
            foreach (var cell in cells)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO cells (
                        study_id, ordinal, name, dataset, seed, config_json,
                        requirements_json, retention_json, created_at
                    ) VALUES (
                        $study, $ordinal, $name, $dataset, $seed, $config,
                        $requirements, $retention, $now
                    ) RETURNING id
                    """;
                insert.Parameters.AddWithValue("$study", studyId);
                insert.Parameters.AddWithValue("$ordinal", cell.Ordinal);
                insert.Parameters.AddWithValue("$name", cell.Name);
                insert.Parameters.AddWithValue("$dataset", cell.Dataset);
                insert.Parameters.AddWithValue("$seed", cell.Seed);
                insert.Parameters.AddWithValue("$config", cell.ConfigJson);
                insert.Parameters.AddWithValue("$requirements", cell.RequirementsJson);
                insert.Parameters.AddWithValue("$retention", cell.RetentionJson);
                insert.Parameters.AddWithValue("$now", now);
                var cellId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                cellIds.Add(cellId);
            }

            return RegistryResult<RegisteredStudy>.Ok(new RegisteredStudy(studyId, cellIds));
        });
    }

    /// <summary>
    /// The stored capabilities with the declared ones laid over them: declared
    /// keys replace, owned keys the declaration dropped are removed, others are
    /// kept. The models an environment admission added stay admitted, because
    /// that admission is kept.
    /// </summary>
    /// <remarks>Null when the stored column holds something other than a JSON object.</remarks>
    static (string Merged, IReadOnlyList<string> Kept, IReadOnlyList<string> Removed)? MergeCapabilities(
        string? storedJson, string declaredJson, IReadOnlyCollection<string> ownedKeys)
    {
        var declared = System.Text.Json.Nodes.JsonNode.Parse(declaredJson) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidOperationException("a Site's declared capabilities are a JSON object");
        System.Text.Json.Nodes.JsonObject stored;
        if (string.IsNullOrWhiteSpace(storedJson))
        {
            stored = new System.Text.Json.Nodes.JsonObject();
        }
        else
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(storedJson) is not System.Text.Json.Nodes.JsonObject read) return null;
                stored = read;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        var kept = new List<string>();
        var removed = new List<string>();
        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in stored)
        {
            if (declared.ContainsKey(key)) continue;
            if (ownedKeys.Contains(key))
            {
                removed.Add(key);
                continue;
            }
            merged[key] = value?.DeepClone();
            kept.Add(key);
        }
        foreach (var (key, value) in declared)
        {
            merged[key] = value?.DeepClone();
        }
        if (merged[SiteAdmissions.EnvironmentKey] is System.Text.Json.Nodes.JsonObject admission
            && admission["models"] is System.Text.Json.Nodes.JsonArray added)
        {
            var admitted = merged["admitted_models"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            var names = admitted.Select(model => model?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var model in added)
            {
                if (model is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var modelName)
                    && modelName.Length > 0 && names.Add(modelName))
                {
                    admitted.Add(modelName);
                }
            }
            merged["admitted_models"] = admitted;
        }
        return (merged.ToJsonString(), kept, removed);
    }

    static bool SourceExists(SqliteConnection connection, long sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// The declared data inputs of a compiled study that the source does not
    /// carry, by path and in order.
    ///
    /// A source is registered either with a study, so that study's declared
    /// data inputs join its files, or without one, so it holds the code alone.
    /// Both are valid sources and nothing distinguishes them by name. A study
    /// that declares data inputs and is registered against the code alone
    /// produces cells that reach a machine, look for a recording that was never
    /// shipped, and fail in about two seconds each — which is what happened on
    /// 2026-09-26, when the whole controller-recording density suite failed this
    /// way and kept being re-offered.
    ///
    /// Empty means the study is satisfied: it declares no data inputs, or the
    /// source carries all of them.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredInputsTheSourceLacks(
        SqliteConnection connection, long sourceId, string compiledStudyJson)
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        using (var compiled = System.Text.Json.JsonDocument.Parse(compiledStudyJson))
        {
            if (!compiled.RootElement.TryGetProperty("dataset", out var dataset)
                || !dataset.TryGetProperty("inputs", out var inputs)
                || inputs.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return [];
            }
            foreach (var entry in inputs.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("path", out var path)
                    && path.ValueKind == System.Text.Json.JsonValueKind.String
                    && path.GetString() is { Length: > 0 } declared)
                {
                    wanted.Add(declared.Replace('\\', '/'));
                }
            }
        }
        if (wanted.Count == 0)
        {
            return [];
        }

        string? declaredFilesJson;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT declared_files_json FROM sources WHERE id = $id";
            command.Parameters.AddWithValue("$id", sourceId);
            declaredFilesJson = command.ExecuteScalar() as string;
        }
        if (declaredFilesJson is null)
        {
            return [];
        }
        var carried = new HashSet<string>(StringComparer.Ordinal);
        using (var files = System.Text.Json.JsonDocument.Parse(declaredFilesJson))
        {
            if (files.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var file in files.RootElement.EnumerateArray())
                {
                    if (file.TryGetProperty("path", out var path)
                        && path.ValueKind == System.Text.Json.JsonValueKind.String
                        && path.GetString() is { Length: > 0 } held)
                    {
                        carried.Add(held.Replace('\\', '/'));
                    }
                }
            }
        }
        wanted.ExceptWith(carried);
        return wanted.ToArray();
    }

    // ── 2. ListRunnableCells ─────────────────────────────────────────────────

    /// <summary>
    /// The cells a scheduler may claim right now: their study is in state
    /// <c>run</c>, they have no accepted attempt, and no attempt of theirs is
    /// still open.
    ///
    /// This is a projection, not a claim. It can be stale the moment it
    /// returns, which is why <see cref="TryStartAttempt"/> re-checks every one
    /// of these conditions inside its own transaction rather than trusting a
    /// caller who read this first.
    /// </summary>
    public static IReadOnlyList<RunnableCell> ListRunnableCells(
        SqliteConnection connection, long? studyId = null, long maxAttempts = DefaultMaxAttempts)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.id, c.study_id, p.source_id, c.ordinal, c.name, c.dataset, c.seed, c.requirements_json, c.config_json
            FROM cells c
            JOIN studies p ON p.id = c.study_id
            WHERE p.requested_state = 'run'
              AND c.accepted_attempt_id IS NULL
              -- CHG 2026-09-11: "no attempt of theirs is still open" now means
              -- no attempt of theirs is still open AND BEING WORKED ON. An
              -- attempt whose supervisor has not beaten within StaleAfter is a
              -- machine that is gone, and the cell it holds was unclaimable
              -- forever: this reader excluded it while `LiveAttempts` — the
              -- sibling that decides whether a Site is full — already ignored it.
              -- One question, asked the same way in both places. The claim
              -- settles it for real: `TryStartAttempt` marks such an attempt
              -- lost inside its own transaction before opening the next one.
              -- CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch):
              -- an attempt that went silent may still be training on a machine
              -- nobody can reach, so silence alone no longer frees its cell;
              -- a person gives it up with `retry-cell`.
              AND NOT EXISTS (
                    SELECT 1 FROM attempts a
                    WHERE a.cell_id = c.id AND a.state IN ('opened', 'running'))
              -- The cell's own count of tries is `attempts.ordinal`; a cell that
              -- has spent its budget waits for a person, who says so with
              -- `retry-cell` and a reason.
              -- CHG 2026-09-25 (slice M1, owner's go): an attempt a Site's
              -- compiler refused is the machine's, and is not counted.
              AND (COUNTED_ATTEMPTS < $attempts
                   OR EXISTS (SELECT 1 FROM events e
                       WHERE e.attempt_id = (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
                         AND e.id = (SELECT MAX(id) FROM events WHERE attempt_id = e.attempt_id)
                         AND e.kind = 'retry_requested'))
              -- A refusal that would repeat unchanged is not retried at all. The
              -- worker classifies it when it writes the failure; retrying a card
              -- the compiler refuses would burn a machine printing one sentence.
              -- CHG 2026-09-11 night (slice 2 of the cell port): the retired
              -- StageAttemptAuthority's four retry classes, read from the LAST
              -- record of the LAST attempt: deterministic and human are never
              -- offered again by the machine (a person's `retry-cell` writes a
              -- later record that lifts them); site is refused at the claim for
              -- the Site it failed on; transient waits out the window below.
              AND NOT EXISTS (
                    SELECT 1 FROM events e
                    WHERE e.attempt_id = (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
                      AND e.id = (SELECT MAX(id) FROM events WHERE attempt_id = e.attempt_id)
                      AND (json_extract(e.detail_json, '$.deterministic') = 1
                           OR json_extract(e.detail_json, '$.failure_class') IN ('deterministic', 'human')))
              -- The retired dispatch block gave a Plan whose supervision was
              -- refused ten minutes before offering it again; here the window is
              -- per cell, so a transient failure is not retried within seconds
              -- and a person's retry-cell lifts it at once.
              AND NOT EXISTS (
                    SELECT 1 FROM attempts a
                    WHERE a.id = (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
                      AND a.state = 'failed' AND a.ended_at >= $windowSince
                      AND (SELECT kind FROM events WHERE attempt_id = a.id ORDER BY id DESC LIMIT 1) != 'retry_requested'
                      AND COALESCE((SELECT json_extract(detail_json, '$.failure_class') FROM events
                                    WHERE attempt_id = a.id ORDER BY id DESC LIMIT 1), 'transient') = 'transient')
              -- A standing pause over every Site or over this study, or a hold on
              -- the study or the cell, keeps it out of the offer. A Site-scoped
              -- pause is the claim's to refuse: the projection names no Site.
              AND NOT EXISTS (
                    SELECT 1 FROM operator_requests r
                    WHERE r.consumed_at IS NULL AND r.released_at IS NULL
                      AND ((r.kind = 'pause' AND r.scope IN ('all', 'study:' || c.study_id))
                        OR (r.kind = 'hold' AND r.scope IN ('study:' || c.study_id, 'cell:' || c.id))))
              AND ($study IS NULL OR c.study_id = $study)
            -- CHG 2026-09-11 night (slice 7, row 12): the order the offer is
            -- made in. A running cell is not in this list, so it is never moved.
            -- CHG 2026-09-25 (slice P2 of the scheduling loop): the rank first,
            -- the cell's own over its study's, normal without either; then the
            -- queue, where a study registered with id n waits at 2n and work
            -- moved last behind study m waits at 2m + 1; two moves behind the
            -- same study in the order they were made, to the second; then the
            -- compiled order. tools/fern-scheduler-gleam/src/fern_scheduler/priority.gleam
            -- orders the same way, and the two are tested against one another.
            ORDER BY CASE COALESCE((SELECT priority FROM dispatch_priorities WHERE scope = 'cell:' || c.id),
                                   (SELECT priority FROM dispatch_priorities WHERE scope = 'study:' || c.study_id),
                                   'normal')
                         WHEN 'immediate' THEN 0 WHEN 'urgent' THEN 1 WHEN 'normal' THEN 2 WHEN 'slow' THEN 3 END,
                     COALESCE((SELECT 2 * behind_study_id + 1 FROM dispatch_last_moves WHERE scope = 'cell:' || c.id),
                              (SELECT 2 * behind_study_id + 1 FROM dispatch_last_moves WHERE scope = 'study:' || c.study_id),
                              2 * c.study_id),
                     COALESCE((SELECT substr(set_at, 1, 19) FROM dispatch_last_moves WHERE scope = 'cell:' || c.id),
                              (SELECT substr(set_at, 1, 19) FROM dispatch_last_moves WHERE scope = 'study:' || c.study_id),
                              ''),
                     c.ordinal, c.id
            """;
        command.Parameters.AddWithValue("$study", studyId is { } id ? id : DBNull.Value);
        command.Parameters.AddWithValue("$windowSince", RetryWindowSince());
        command.CommandText = command.CommandText.Replace("COUNTED_ATTEMPTS", CountedAttempts("c.id"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$attempts", maxAttempts);
        var rows = new List<RunnableCell>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RunnableCell(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8)));
        }
        return rows;
    }

    // ── 3. ListSites ─────────────────────────────────────────────────────────

    /// <summary>Every registered Site and what it declares it can do.</summary>
    public static IReadOnlyList<Site> ListSites(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, capabilities_json, capacity_json FROM sites ORDER BY id";
        var rows = new List<Site>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Site(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    // ── 4. TryStartAttempt ───────────────────────────────────────────────────

    /// <summary>
    /// Claim one cell, in ONE transaction that also proves the claim is allowed.
    ///
    /// Inside the transaction: the study must be in state <c>run</c>; the cell
    /// must have no accepted attempt; no other attempt of the cell may be open.
    /// Only then is the attempt inserted, with the next ordinal and the next
    /// fencing epoch. Two schedulers racing one cell therefore get one attempt
    /// and one refusal — the second finds the first's open attempt, because
    /// BEGIN IMMEDIATE serialised them.
    ///
    /// The epoch is monotone per cell and never reused: it is the previous
    /// maximum plus one, so a displaced worker's epoch can never come round
    /// again and accidentally match.
    /// </summary>
    /// <param name="officeEpoch">
    /// CHG 2026-09-26 (slice W1 of leases-and-watching-a-running-cell): the epoch
    /// of the office the claiming supervisor believes it holds. When given, the
    /// claim is refused unless it is the Site's current office AND the Home still
    /// counts that office's lease as held now: a supervisor displaced between two
    /// renewals, or one whose lease ran out before anyone took over, claims
    /// nothing. Omitted, the claim is as it was.
    /// </param>
    public static RegistryResult<StartedAttempt> TryStartAttempt(
        RegistryWriter writer,
        long cellId,
        long siteId,
        string requestJson,
        long maxAttempts = DefaultMaxAttempts,
        long? officeEpoch = null)
    {
        return writer.WriteImmediate(connection =>
        {
            if (officeEpoch is { } epoch)
            {
                var holder = ReadSupervisor(connection, siteId);
                if (holder is null || holder.Epoch != epoch || !holder.HoldsOfficeAt(Now()))
                {
                    return RegistryResult<StartedAttempt>.Rejected(Invalid(
                        $"the office at epoch {epoch} is not Site {siteId}'s current office"
                        + (holder is null
                            ? " (nobody holds it)"
                            : $" (epoch {holder.Epoch}, held by {holder.Host}/{holder.Pid} until {holder.LeaseUntil}"
                              + (holder.EndedAt is null ? ")" : $", ended {holder.EndedAt})"))
                        + "; this supervisor claims nothing"));
                }
            }
            long studyId;
            long sourceId;
            string requestedState;
            using (var study = connection.CreateCommand())
            {
                study.CommandText =
                    """
                    SELECT p.id, p.requested_state, c.accepted_attempt_id, p.source_id
                    FROM cells c JOIN studies p ON p.id = c.study_id
                    WHERE c.id = $cell
                    """;
                study.Parameters.AddWithValue("$cell", cellId);
                using var reader = study.ExecuteReader();
                if (!reader.Read())
                {
                    return RegistryResult<StartedAttempt>.Rejected(NotFound($"cell {cellId} is not registered"));
                }
                studyId = reader.GetInt64(0);
                requestedState = reader.GetString(1);
                if (!reader.IsDBNull(2))
                {
                    return RegistryResult<StartedAttempt>.Rejected(
                        Invalid($"cell {cellId} already has accepted attempt {reader.GetInt64(2)}"));
                }
                sourceId = reader.GetInt64(3);
            }
            if (requestedState != StateRun)
            {
                return RegistryResult<StartedAttempt>.Rejected(
                    Invalid($"study {studyId} is {requestedState}, not run; no new cell is claimed"));
            }
            if (ReadSite(connection, siteId) is not { } site)
            {
                return RegistryResult<StartedAttempt>.Rejected(NotFound($"site {siteId} is not registered"));
            }
            // CHG 2026-09-11 night (slice 6, row 31): the Site must hold the
            // study's declared source, verified by declared bytes and recorded
            // by install-study-source; the root it recorded is what the worker
            // trains from. The decision refuses the placement first with the
            // same sentence; this is the authority.
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slices 2 and 4):
            // a Site runs every cell from its repository, and the worker makes
            // the directory the cell runs in; no installed source is needed or
            // recorded. A Site that declares no code_from is refused by name.
            var declaredCode = CodeOf(site.CapabilitiesJson);
            if (!declaredCode.TryGetValue(out var code))
            {
                declaredCode.TryGetRefusal(out var unreadable);
                return RegistryResult<StartedAttempt>.Rejected(Invalid($"Site {site.Name}: {unreadable.Detail}"));
            }
            var sourceRoot = code.Repository!;

            long nextOrdinal;
            long nextEpoch;
            using (var open = connection.CreateCommand())
            {
                open.CommandText =
                    """
                    -- CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch): any open attempt, silent or not.
                    SELECT COUNT(*) FILTER (WHERE state IN ('opened', 'running')),
                           COALESCE(MAX(ordinal), 0),
                           COALESCE(MAX(fencing_epoch), 0)
                    FROM attempts WHERE cell_id = $cell
                    """;
                open.Parameters.AddWithValue("$cell", cellId);
                using var reader = open.ExecuteReader();
                reader.Read();
                if (reader.GetInt64(0) != 0)
                {
                    return RegistryResult<StartedAttempt>.Rejected(
                        Invalid($"cell {cellId} already has an open attempt; one attempt at a time"));
                }
                nextOrdinal = reader.GetInt64(1) + 1;
                nextEpoch = reader.GetInt64(2) + 1;
            }

            // A failure the last attempt recorded as the MACHINE's rather than
            // the cell's does not get another try on that machine. Ported from
            // the retired `OtherProfile` retry class: the cell is still worth
            // running, just not here, and spending its budget on the one host
            // that cannot run it is how a cell ends up waiting for a person
            // while three other machines sit idle.
            // CHG 2026-09-11 night (slice 2): all four classes and the retry
            // window are settled here as well as in the projection, because the
            // projection can be stale; a person's retry-cell record lifts them.
            if (LastFailure(connection, cellId) is { } last && last.LastEventKind != RetryRequestedEvent)
            {
                switch (last.Class)
                {
                    case FailureDeterministic:
                        return RegistryResult<StartedAttempt>.Rejected(Invalid(
                            $"cell {cellId}'s last failure would repeat unchanged ({last.Sentence}); "
                            + "a person lifts that with `retry-cell --reason` after changing what caused it"));
                    case FailureHuman:
                        return RegistryResult<StartedAttempt>.Rejected(Invalid(
                            $"cell {cellId}'s last failure is a person's to judge ({last.Sentence}); "
                            + "`retry-cell --reason` records that decision"));
                    // CHG 2026-09-25 (slice M1): a compiler's refusal is the
                    // Site's, read for the whole source below, and lifts when
                    // a supervisor starts there after it.
                    case FailureSite when last.SiteId == siteId && last.Code != CodeCompilerRefusedField:
                        return RegistryResult<StartedAttempt>.Rejected(Invalid(
                            $"cell {cellId} last failed on this Site for a reason that belongs to the machine, not to the "
                            + "cell; it is claimable on another Site and will not be tried again on this one"));
                }
                // The window holds a transient failure back; a machine's failure
                // is claimable elsewhere at once, which is the point of the class.
                if (last.Class == FailureTransient && string.CompareOrdinal(last.EndedAt, RetryWindowSince()) >= 0)
                {
                    return RegistryResult<StartedAttempt>.Rejected(Invalid(
                        $"cell {cellId} failed at {last.EndedAt} and is offered again after the "
                        + $"{RetryWindow.TotalMinutes:F0}-minute window, or now with `retry-cell --reason`"));
                }
            }
            var paused = PendingRequests(connection, RequestPause, ScopeAll, SiteScope(siteId), StudyScope(studyId));
            if (paused.Count != 0)
            {
                return RegistryResult<StartedAttempt>.Rejected(Invalid(
                    $"scheduling is paused over {paused[0].Scope} by {paused[0].RequestedBy} ({paused[0].Reason}); "
                    + "no cell is claimed until the pause is released"));
            }
            // CHG 2026-09-25 (slices M1 and M3): what the failures of this
            // source's cells on this Site say about the Site, since its latest
            // supervisor started: its compiler refused a card, or several failed
            // the same way within the window.
            if (SourceRefusedOnSite(connection, sourceId, siteId, site.Name) is { } refusedHere)
            {
                return RegistryResult<StartedAttempt>.Rejected(Invalid($"cell {cellId}: {refusedHere}"));
            }

            // CHG 2026-09-11 night (slice 3): a hold over the Site is the Site
            // disabled (the retired `site disable`); it claims nothing until a
            // person enables it again.
            var holds = PendingRequests(connection, RequestHold, StudyScope(studyId), CellScope(cellId), SiteScope(siteId));
            if (holds.Count != 0)
            {
                return RegistryResult<StartedAttempt>.Rejected(Invalid(
                    $"cell {cellId} is held over {holds[0].Scope} by {holds[0].RequestedBy} ({holds[0].Reason}); "
                    + "it is not claimed there until the hold is released"));
            }

            // Which card this attempt gets, when the Site says which cards it
            // has. A Site that declares devices runs one cell per card and no
            // more, and the attempt records the card it holds, so a person
            // looking at a busy machine can say which run is on which GPU.
            // Ported from the retired per-device grants; the generation is the
            // digest of the Site's own declaration, so re-registering a machine
            // whose cards changed mints a new one, which is what that field
            // meant there.
            string? device = null;
            if (site.Devices.Count != 0)
            {
                // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch): a
                // silent attempt keeps its card until a person gives it up.
                var held = DevicesHeld(connection, siteId);
                device = site.Devices.FirstOrDefault(candidate => !held.Contains(candidate));
                if (device is null)
                {
                    return RegistryResult<StartedAttempt>.Rejected(Invalid(
                        $"site {site.Name} declares {site.Devices.Count} device(s) and every one is held by a live "
                        + $"attempt ({string.Join(", ", held)}); a machine runs one cell per card"));
                }
            }

            // The budget is settled here, not only in the projection that offered
            // the cell: the projection can be stale, and a second supervisor
            // reading it a moment earlier must not be able to spend a try the
            // first one already spent.
            // CHG: a retry on the latest attempt authorizes one additional
            // claim. The next attempt has no such event, so it cannot repeat itself.
            using var latestEvent = connection.CreateCommand();
            latestEvent.CommandText = """
                SELECT kind FROM events WHERE attempt_id =
                    (SELECT id FROM attempts WHERE cell_id = $cell ORDER BY ordinal DESC LIMIT 1)
                ORDER BY id DESC LIMIT 1
                """;
            latestEvent.Parameters.AddWithValue("$cell", cellId);
            // CHG 2026-09-25 (slice M1, owner's go): counted as the offer counts,
            // leaving out attempts a Site's compiler refused.
            using var counted = connection.CreateCommand();
            counted.CommandText = "SELECT " + CountedAttempts("$cell");
            counted.Parameters.AddWithValue("$cell", cellId);
            var spent = Convert.ToInt64(counted.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (spent >= maxAttempts && latestEvent.ExecuteScalar() as string != RetryRequestedEvent)
            {
                return RegistryResult<StartedAttempt>.Rejected(Invalid(
                    $"cell {cellId} has spent its {maxAttempts} attempts; a further try is a person's decision, "
                    + "recorded with `retry-cell --reason`"));
            }

            // An attempt that is still `opened` or `running` but has not beaten
            // within StaleAfter belongs to a supervisor that is gone. Rewriting
            // the row is what frees the cell — stopping the count was never
            // enough, because `ListRunnableCells` reads the row and not a
            // count — and it happens HERE, in the claim's own transaction, so a
            // second claimant cannot see the cell as free while the row still
            // says otherwise. The old supervisor needs no telling: every write
            // it has left carries its epoch, and this row is no longer at that
            // epoch or in a state those writes accept.
            // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch): the
            // claim no longer closes a silent attempt of this cell; the check
            // above refuses while any attempt of it is open.

            // CHG 2026-09-11: the Site's capacity is enforced HERE, in the claim,
            // not only in the placement that precedes it. `SchedulerDecision`
            // counts busy slots so it does not offer work a machine cannot take,
            // but that count is read outside any transaction: two
            // `run-study-cells` invocations on one machine each read "0 busy" and
            // each claimed, and two trainings landed on the one 4070, which is
            // what AGENTS.md rule 1 forbids. The count that decides is this one,
            // inside BEGIN IMMEDIATE, and only attempts whose supervisor has beaten
            // within `StaleAfter` count: an attempt nobody is behind holds no card.
            // CHG 2026-09-26 (slice W2): no longer; every open attempt counts, silent
            // or not, until a person gives it up (LiveAttempts).
            //
            // It comes AFTER the cell's own open-attempt check so that two
            // supervisors racing one cell are told that, which is the more useful
            // sentence, rather than being told the machine is full.
            var live = LiveAttempts(connection, siteId);
            if (live >= site.Slots)
            {
                return RegistryResult<StartedAttempt>.Rejected(Invalid(
                    $"site {site.Name} already holds {live} open attempt(s) and its capacity is {site.Slots}; "
                    + "a machine runs what its capacity declares. An attempt whose supervisor stopped beating still "
                    + "holds its slot until a person gives it up with `retry-cell`"));
            }

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO attempts (cell_id, ordinal, site_id, fencing_epoch, state, request_json, started_at)
                VALUES ($cell, $ordinal, $site, $epoch, 'opened', $request, $now)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$cell", cellId);
            insert.Parameters.AddWithValue("$ordinal", nextOrdinal);
            insert.Parameters.AddWithValue("$site", siteId);
            insert.Parameters.AddWithValue("$epoch", nextEpoch);
            insert.Parameters.AddWithValue("$request", requestJson);
            insert.Parameters.AddWithValue("$now", Now());
            var attemptId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (device is not null)
            {
                // CHG 2026-09-11 night (slice 3 of the cell port, rows 4 and 35):
                // the card is held by a `device_grants` row as well as named in
                // the event: the retired DeviceGrantAuthority's durable WHO, with
                // a per-card generation, released when the attempt ends and
                // reconciled when it is lost. An environment install holds a
                // card the same way, which is what keeps an install and a
                // trainer off one GPU; an event could not say that.
                var granted = DeviceGrantAuthority.AcquireInTransaction(
                    connection, device, site.Name, "cell", GrantOwner(attemptId), DeviceCapacity);
                if (granted.TryGetRefusal(out var grantRefusal))
                {
                    // DevicesHeld was consulted before the attempt row went in,
                    // so this cannot happen inside BEGIN IMMEDIATE; a thrown fault
                    // rolls the row back, where a returned refusal would commit it.
                    throw new InvalidOperationException(
                        $"device {device} on {site.Name} was free a moment ago and cannot be granted to attempt {attemptId}: {grantRefusal.Detail}");
                }
                using var grant = connection.CreateCommand();
                grant.CommandText =
                    """
                    INSERT INTO events (attempt_id, at, kind, phase, detail_json)
                    VALUES ($attempt, $now, $kind, NULL, $detail)
                    """;
                grant.Parameters.AddWithValue("$attempt", attemptId);
                grant.Parameters.AddWithValue("$now", Now());
                grant.Parameters.AddWithValue("$kind", DeviceGrantEvent);
                grant.Parameters.AddWithValue("$detail", System.Text.Json.JsonSerializer.Serialize(new
                {
                    detail = $"granted device {device} on {site.Name} under generation {site.DeviceGeneration}",
                    device,
                    generation = site.DeviceGeneration,
                    site = site.Name,
                }));
                grant.ExecuteNonQuery();
            }
            return RegistryResult<StartedAttempt>.Ok(new StartedAttempt(attemptId, nextOrdinal, nextEpoch, device, sourceRoot, code.From));
        });
    }

    /// <summary>
    /// The instant an attempt must have reported at or after to count as alive.
    /// One expression, read by the runnable list, by the claim and by the Site's
    /// capacity, so the three cannot drift into three different meanings of
    /// "silent".
    /// </summary>
    static string SilentSince() =>
        DateTimeOffset.UtcNow.Subtract(StaleAfter).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Close every open attempt of this cell whose supervisor stopped beating,
    /// with an event that says since when and against what bound. Called inside
    /// the claim's transaction and only after the freshness check found none
    /// alive.
    /// </summary>
    /// <summary>
    /// The same settlement for every attempt on one Site, before a card is
    /// chosen: a silent attempt's grant still stands until the attempt is
    /// declared lost (a lost transport does not release a grant; the owner is
    /// reconciled), so the claim reconciles the Site's silent attempts first,
    /// and the held-cards read and the grant it then takes agree.
    /// CHG 2026-09-11 night (slice 3).
    /// </summary>
    static void MarkSilentAttemptsLostOnSite(SqliteConnection connection, long siteId)
    {
        var silent = new List<(long AttemptId, long Epoch, string LastReport)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT id, fencing_epoch, COALESCE(last_progress_at, started_at)
                FROM attempts
                WHERE site_id = $site AND state IN ('opened', 'running')
                  AND COALESCE(last_progress_at, started_at) < $since
                """;
            read.Parameters.AddWithValue("$site", siteId);
            read.Parameters.AddWithValue("$since", SilentSince());
            using var rows = read.ExecuteReader();
            while (rows.Read())
            {
                silent.Add((rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2)));
            }
        }
        foreach (var (attemptId, epoch, lastReport) in silent)
        {
            CloseSilentAttempt(connection, attemptId, epoch, lastReport);
        }
    }

    static void MarkSilentAttemptsLost(SqliteConnection connection, long cellId)
    {
        var silent = new List<(long Id, long Epoch, string Last)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT id, fencing_epoch, COALESCE(last_progress_at, started_at)
                FROM attempts
                WHERE cell_id = $cell AND state IN ('opened', 'running')
                  AND COALESCE(last_progress_at, started_at) < $since
                """;
            read.Parameters.AddWithValue("$cell", cellId);
            read.Parameters.AddWithValue("$since", SilentSince());
            using var rows = read.ExecuteReader();
            while (rows.Read())
            {
                silent.Add((rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2)));
            }
        }

        foreach (var (id, epoch, last) in silent)
        {
            CloseSilentAttempt(connection, id, epoch, last);
        }
    }

    /// <summary>
    /// Rewrite one silent attempt as lost, with the event that says since when
    /// and against what bound. One writer for the claim and the reconciler, so
    /// the row and the sentence cannot differ depending on who noticed.
    /// </summary>
    static void CloseSilentAttempt(SqliteConnection connection, long attemptId, long epoch, string lastReport)
    {
        var now = Now();
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE attempts SET state = 'lost', ended_at = $now
                WHERE id = $id AND fencing_epoch = $epoch AND state IN ('opened', 'running')
                """;
            update.Parameters.AddWithValue("$id", attemptId);
            update.Parameters.AddWithValue("$epoch", epoch);
            update.Parameters.AddWithValue("$now", now);
            if (update.ExecuteNonQuery() == 0)
            {
                return;
            }
        }
        ReleaseDeviceGrantsOf(connection, attemptId, "lost: supervisor silent past the bound");
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO events (attempt_id, at, kind, phase, detail_json)
            VALUES ($attempt, $now, 'lost', NULL, $detail)
            """;
        insert.Parameters.AddWithValue("$attempt", attemptId);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$detail", System.Text.Json.JsonSerializer.Serialize(new
        {
            detail = $"declared lost: its supervisor last reported {lastReport} and the bound is "
                   + $"{StaleAfter.TotalMinutes:F0} minutes of silence",
            deterministic = false,
            silent_since = lastReport,
            stale_after_seconds = (long)StaleAfter.TotalSeconds,
        }));
        insert.ExecuteNonQuery();
    }

    /// <summary>What a Site is, for a claim: its name, how many cells it admits, and which cards it declares.</summary>
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): the claim
    // also reads where the Site takes its cells' code from.
    sealed record SiteFacts(string Name, long Slots, IReadOnlyList<string> Devices, string DeviceGeneration, string CapabilitiesJson);

    /// <summary>The Site's claim-facing facts, or null when it is not registered.</summary>
    static SiteFacts? ReadSite(SqliteConnection connection, long siteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, capacity_json, capabilities_json FROM sites WHERE id = $id";
        command.Parameters.AddWithValue("$id", siteId);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        var capabilities = row.GetString(2);
        return new SiteFacts(row.GetString(0), SlotsIn(row.GetString(1)), DevicesIn(capabilities),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(capabilities)))[..16], capabilities);
    }

    /// <summary>
    /// The cards a Site declares, in the order it declared them. A Site that
    /// names none is counted by slots instead, which is what a machine whose
    /// cards nobody has written down still needs.
    /// </summary>
    public static IReadOnlyList<string> DevicesIn(string capabilitiesJson)
    {
        try
        {
            using var capabilities = System.Text.Json.JsonDocument.Parse(capabilitiesJson);
            if (capabilities.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !capabilities.RootElement.TryGetProperty("devices", out var devices)
                || devices.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }
            return devices.EnumerateArray()
                .Where(device => device.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(device => device.GetString()!)
                .Where(device => device.Length != 0)
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The cell's most recent attempt when it ended without a result, read the way the claim judges it.</summary>
    public sealed record LastFailureView(
        long SiteId, string Class, string EndedAt, string LastEventKind, string Sentence, string? Code = null);

    /// <summary>
    /// The most recent attempt of this cell that ended without a result, with
    /// the class its last record carries. The record is the worker's own (it
    /// writes `failure_class` beside the sentence; older records carry only
    /// `deterministic` and `site_specific`, read the same way), and a later
    /// `retry_requested` record is a person's decision that lifts it.
    /// CHG 2026-09-11 night (slice 2): was `LastFailureWasSiteSpecific`, one
    /// class of the four.
    /// </summary>
    public static LastFailureView? LastFailure(SqliteConnection connection, long cellId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.site_id, a.ended_at,
                   COALESCE((SELECT e.kind FROM events e WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), ''),
                   COALESCE((SELECT e.detail_json FROM events e WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), '')
            FROM attempts a
            WHERE a.cell_id = $cell
            ORDER BY a.ordinal DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$cell", cellId);
        // Only the LAST attempt counts: an earlier failure followed by a later
        // attempt has been judged already, by whoever opened the later one.
        using var row = command.ExecuteReader();
        if (!row.Read() || row.IsDBNull(1))
        {
            return null;
        }
        var state = LastAttemptState(connection, cellId);
        if (state is not (AttemptFailed or AttemptCancelled))
        {
            return null;
        }
        var detail = row.GetString(3);
        return new LastFailureView(
            SiteId: row.GetInt64(0), Class: ClassIn(detail), EndedAt: row.GetString(1),
            LastEventKind: row.GetString(2), Sentence: SentenceIn(detail), Code: CodeIn(detail));
    }

    static string? LastAttemptState(SqliteConnection connection, long cellId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts WHERE cell_id = $cell ORDER BY ordinal DESC LIMIT 1";
        command.Parameters.AddWithValue("$cell", cellId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>The class a failure document carries; a document without the field is read through its two older flags.</summary>
    static string ClassIn(string detailJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return FailureTransient;
            if (root.TryGetProperty("failure_class", out var named) && named.ValueKind == System.Text.Json.JsonValueKind.String)
                return named.GetString() ?? FailureTransient;
            if (root.TryGetProperty("deterministic", out var deterministic) && deterministic.ValueKind == System.Text.Json.JsonValueKind.True)
                return FailureDeterministic;
            if (root.TryGetProperty("site_specific", out var site) && site.ValueKind == System.Text.Json.JsonValueKind.True)
                return FailureSite;
            return FailureTransient;
        }
        catch (System.Text.Json.JsonException)
        {
            return FailureTransient;
        }
    }

    static string? CodeIn(string detailJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == System.Text.Json.JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ── A failure that belongs to a machine (slices M1 and M3) ──────────────

    /// <summary>
    /// The code a failure record carries when the Site's compiler refused a
    /// field of a card: the Study's own compiler accepted it when the Study was
    /// registered, so the refusal is that Site's. Slice M1 of
    /// docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-25-what-stops-work-and-who-lifts-it.
    /// </summary>
    public const string CodeCompilerRefusedField = "compiler_refused_field";

    /// <summary>
    /// Slice M3, the owner's numbers of 2026-09-25 ("m3: sure"): this many
    /// failures with one signature on one Site within <see cref="BurstWindow"/>
    /// stop that Site taking the source.
    /// </summary>
    public const int BurstFailures = 3;
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The attempts of a cell that count against its budget, as a SQL scalar
    /// over <paramref name="cell"/> (a column or a parameter): every attempt
    /// except one a Site's compiler refused (slice M1, the owner's go of
    /// 2026-09-25).
    /// CHG 2026-09-25 (the reader's review): only when the worker found that
    /// Site's compiler differs from the one the study records
    /// (<c>compiler_differs</c>). When it could not tell — a study registered
    /// before studies named their compiler — the attempt counts, so a card no
    /// compiler accepts still spends its budget and reaches a person.
    /// </summary>
    public static string CountedAttempts(string cell) =>
        // CHG 2026-09-26: widened from the one compiler refusal to the whole
        // machine class, which M1 already records. A cell's attempts are its
        // own chances at the science; a Site that lacked the data, the CUDA
        // device or a module spent none of them. On 2026-09-26 a recording that
        // never travelled with the code failed every cell of a suite in about
        // two seconds and charged each one, because this counted the class only
        // when the compiler was the cause.
        //
        // A compiler refusal keeps its own narrower rule, which a review settled:
        // it is excused only when the compilers are known to differ, because a
        // field no compiler accepts is the cell's own fault and must reach a
        // person.
        //
        // What keeps this from looping: a cell is offered again elsewhere, and
        // slice M3 stops a Site that failed the same way repeatedly from taking
        // the work. A cell that fails this way on every Site is not protected
        // by the budget and must be caught by M3 or by a person.
        $"""
        (SELECT COUNT(*) FROM attempts counted WHERE counted.cell_id = {cell}
           AND NOT EXISTS (SELECT 1 FROM events refused
                           WHERE refused.attempt_id = counted.id AND refused.kind = '{AttemptFailed}'
                             AND ((json_extract(refused.detail_json, '$.failure_class') = '{FailureSite}'
                                   AND COALESCE(json_extract(refused.detail_json, '$.code'), '')
                                       <> '{CodeCompilerRefusedField}')
                                  OR (json_extract(refused.detail_json, '$.code') = '{CodeCompilerRefusedField}'
                                      AND json_extract(refused.detail_json, '$.compiler_differs') = 1))))
        """;

    /// <summary>
    /// The last failure of a cell on a Site, since that Site's latest supervisor
    /// started, that no person's retry has lifted: what slices M1 and M3 read.
    /// </summary>
    public sealed record SiteFailure(long SiteId, long SourceId, long CellId, string EndedAt, string Sentence, string? Code);

    /// <summary>
    /// Every such failure, on one Site or on all of them. The claim reads it for
    /// its Site and the Gleam round's reading carries it for every Site, so the
    /// two decide from the same rows (the reader's review of 2026-09-25).
    /// </summary>
    public static IReadOnlyList<SiteFailure> ListSiteFailures(SqliteConnection connection, long? siteId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT a.site_id, p.source_id, c.id, a.ended_at,
                   COALESCE((SELECT e.detail_json FROM events e WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), '')
            FROM cells c
            JOIN studies p ON p.id = c.study_id
            JOIN attempts a ON a.id = (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
            WHERE a.state = '{AttemptFailed}' AND a.ended_at IS NOT NULL
              AND ($site IS NULL OR a.site_id = $site)
              AND a.ended_at > COALESCE((SELECT MAX(started_at) FROM supervisors v WHERE v.site_id = a.site_id), '')
              AND COALESCE((SELECT e.kind FROM events e WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), '')
                  != '{RetryRequestedEvent}'
            ORDER BY a.site_id, p.source_id, a.ended_at
            """;
        command.Parameters.AddWithValue("$site", siteId is { } id ? id : DBNull.Value);
        var failures = new List<SiteFailure>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var detail = reader.GetString(4);
            failures.Add(new SiteFailure(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                FailureSentence(detail), CodeIn(detail)));
        }
        return failures;
    }

    /// <summary>
    /// Why this Site takes no cell of this source now, from <see cref="ListSiteFailures"/>,
    /// or null: its compiler refused a card (slice M1), or
    /// <see cref="BurstFailures"/> failures with one signature, the first
    /// line of their sentence, fell within <see cref="BurstWindow"/> (slice
    /// M3). The Gleam round reads the same rows the same way
    /// (<c>reason.gleam</c>, <c>source_failures_on</c>).
    /// </summary>
    public static string? SourceRefusedOnSite(SqliteConnection connection, long sourceId, long siteId, string siteName)
    {
        var failures = new List<(DateTimeOffset At, string Sentence, string? Code)>();
        foreach (var failure in ListSiteFailures(connection, siteId).Where(failure => failure.SourceId == sourceId))
        {
            if (DateTimeOffset.TryParse(failure.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                failures.Add((at, failure.Sentence, failure.Code));
            }
        }
        var refused = failures.Where(failure => failure.Code == CodeCompilerRefusedField).OrderByDescending(f => f.At).ToList();
        if (refused.Count != 0)
        {
            return $"the compiler on Site {siteName} refused a card of source {sourceId} at {refused[0].At:o} "
                + $"({refused[0].Sentence}); no cell of that source is claimed there until a supervisor starts there "
                + "with a compiler that knows it";
        }
        foreach (var group in failures.GroupBy(failure => Signature(failure.Sentence)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var times = group.Select(failure => failure.At).Order().ToList();
            foreach (var start in times)
            {
                var inside = times.Where(time => time >= start && time <= start + BurstWindow).ToList();
                if (inside.Count >= BurstFailures)
                {
                    return $"{inside.Count} cells of source {sourceId} failed the same way on Site {siteName} within "
                        + $"{BurstWindow.TotalMinutes:F0} minutes, the last at {inside[^1]:o} ({group.Key}); the Site takes no "
                        + "more of them until a person retries them or a supervisor starts there after it";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The one sentence of a failure document: its `detail`, else the runtime's
    /// `message`, else the text itself. <c>CellFailureDetail.Sentence</c> is
    /// this, so the claim and every monitor read one sentence (moved here
    /// 2026-09-25 so the claim's burst signature and the Gleam round's agree).
    /// </summary>
    public static string FailureSentence(string detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return "";
        }
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return detailJson.Trim();
            }
            var said = document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == System.Text.Json.JsonValueKind.String
                    ? detail.GetString() ?? ""
                    : "";
            var message = document.RootElement.TryGetProperty("message", out var runtime)
                && runtime.ValueKind == System.Text.Json.JsonValueKind.String
                    ? runtime.GetString() ?? ""
                    : "";
            if (said.Length == 0)
            {
                return message.Length == 0 ? detailJson.Trim() : message;
            }
            // The runtime's own message is already inside the sentence the
            // worker wrote; a monitor that appended it again would print it
            // twice.
            return said;
        }
        catch (System.Text.Json.JsonException)
        {
            return detailJson.Trim();
        }
    }

    /// <summary>A failure's signature: the first line of its sentence.</summary>
    static string Signature(string sentence)
    {
        var end = sentence.IndexOf('\n');
        return end < 0 ? sentence : sentence[..end];
    }

    static string SentenceIn(string detailJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var sentence)
                && sentence.ValueKind == System.Text.Json.JsonValueKind.String
                    ? sentence.GetString() ?? ""
                    : "";
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    /// <summary>The instant before which a failed attempt is old enough to be offered again.</summary>
    static string RetryWindowSince() =>
        (DateTimeOffset.UtcNow - RetryWindow).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Which of this Site's cards are held right now: by a live attempt's grant
    /// (a grant whose attempt went silent past the bound does not count, so a
    /// dead supervisor's card frees at the bound, as before), or by an
    /// environment install's grant, which counts for as long as it stands.
    /// CHG 2026-09-11 night (slice 3): read from `device_grants`, no longer from
    /// the grant event alone.
    /// </summary>
    static HashSet<string> DevicesHeld(SqliteConnection connection, long siteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT g.device_id
            FROM device_grants g
            JOIN sites s ON s.name = g.site_id
            LEFT JOIN attempts a ON g.owner_kind = 'cell' AND g.owner_attempt_id = 'attempt:' || a.id
            WHERE s.id = $site AND g.released_at_ms IS NULL
              AND (g.owner_kind = 'environment_install'
                   OR a.state IN ('opened', 'running'))
            """;
        // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch).
        command.Parameters.AddWithValue("$site", siteId);
        var held = new HashSet<string>(StringComparer.Ordinal);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (!rows.IsDBNull(0))
            {
                held.Add(rows.GetString(0));
            }
        }
        return held;
    }

    /// <summary>
    /// The `slots` a Site's capacity declares. A capacity that is unreadable or
    /// silent means ONE: a machine whose declaration cannot be read runs one cell
    /// at a time, which is the safe direction on a single-GPU host.
    /// </summary>
    static long SlotsIn(string capacityJson)
    {
        try
        {
            using var capacity = System.Text.Json.JsonDocument.Parse(capacityJson);
            return capacity.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && capacity.RootElement.TryGetProperty("slots", out var slots)
                && slots.TryGetInt64(out var value) && value > 0
                    ? value
                    : DefaultSlots;
        }
        catch (System.Text.Json.JsonException)
        {
            return DefaultSlots;
        }
    }

    /// <summary>
    /// How many attempts this Site is holding with a supervisor still behind them:
    /// open or running, and beaten within <see cref="StaleAfter"/>. An attempt
    /// that has gone quiet is not counted, because the machine it was on is gone
    /// and the alternative is a Site that never admits work again.
    /// CHG 2026-09-26 (slice W2, the owner's choice): every open attempt counts,
    /// quiet or not; the Home tells the owner after 30 minutes of silence and a
    /// person gives it up with retry-cell.
    /// </summary>
    static long LiveAttempts(SqliteConnection connection, long siteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM attempts
            WHERE site_id = $site AND state IN ('opened', 'running')
            """;
        // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner: notify, do not re-dispatch): a
        // silent attempt still holds its slot ("occupied or unknown").
        command.Parameters.AddWithValue("$site", siteId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ── 4b. ReconcileSilentAttempts ──────────────────────────────────────────

    /// <summary>
    /// Close every attempt, across a study or the whole Home, whose supervisor
    /// stopped beating — the reconciler, at cell level.
    ///
    /// Ported from the retired `PlanReconcile.ReconcileAttempts`, which ran every
    /// cycle inside the resident scheduler and rewrote a plan attempt whose
    /// leases had all expired as <c>lost</c>. Two things are simpler here and one
    /// is the same. The lease chain is gone, so the expiry it compared against is
    /// the heartbeat bound — the same signal and, as the census records, very
    /// nearly the same number. The scheduler's office is gone, so this asks no
    /// permit: the write is fenced by the attempt's own epoch, which is what
    /// actually settles a race. And what it does is the same and is the point —
    /// it REWRITES the row rather than merely ignoring it, because a row left
    /// open is what kept a cell unclaimable.
    ///
    /// The claim does this for the cell it is claiming. This does it for the
    /// cells nobody is claiming, so a study nobody can place still tells the
    /// truth about itself.
    /// </summary>
    public static RegistryResult<IReadOnlyList<ReconciledAttempt>> ReconcileSilentAttempts(
        RegistryWriter writer, long? studyId = null)
    {
        return writer.WriteImmediate<RegistryResult<IReadOnlyList<ReconciledAttempt>>>(connection =>
        {
            var silent = new List<ReconciledAttempt>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText =
                    """
                    SELECT a.id, a.cell_id, c.name, a.fencing_epoch, COALESCE(a.last_progress_at, a.started_at)
                    FROM attempts a JOIN cells c ON c.id = a.cell_id
                    WHERE a.state IN ('opened', 'running')
                      AND COALESCE(a.last_progress_at, a.started_at) < $since
                      AND ($study IS NULL OR c.study_id = $study)
                    ORDER BY a.id
                    """;
                read.Parameters.AddWithValue("$since", SilentSince());
                read.Parameters.AddWithValue("$study", studyId is { } id ? id : DBNull.Value);
                using var rows = read.ExecuteReader();
                while (rows.Read())
                {
                    silent.Add(new ReconciledAttempt(
                        rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2), rows.GetInt64(3), rows.GetString(4)));
                }
            }

            foreach (var attempt in silent)
            {
                CloseSilentAttempt(connection, attempt.AttemptId, attempt.FencingEpoch, attempt.SilentSince);
            }
            return RegistryResult<IReadOnlyList<ReconciledAttempt>>.Ok(silent);
        });
    }

    /// <summary>One attempt the reconciler closed, and since when it had been silent.</summary>
    public sealed record ReconciledAttempt(
        long AttemptId, long CellId, string CellName, long FencingEpoch, string SilentSince);

    // ── a registered source, for the code of a cell ──────────────────────
    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4): SiteSource, RecordSiteSource, ReadSiteSource and ListSiteSources
    // left with installed sources and the site_sources table.

    /// <summary>
    /// A registered source as a worker needs it to make a cell's code: the commit
    /// it was registered at and its declared files, as the registration wrote them.
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2).
    /// </summary>
    // CHG 2026-09-26 (schema 21): RegisteredFrom where BundleSha256 was.
    public sealed record SourceDeclaration(long SourceId, string CommitSha, string DeclaredFilesJson, string RegisteredFrom);

    /// <summary>
    /// What the Home needs to make one open attempt's code for a remote Site: the
    /// Site's declaration and the source its study was registered with
    /// (source-roots-stop-being-full-copies, slice 3).
    /// </summary>
    public sealed record AttemptCodeView(long AttemptId, long SiteId, long SourceId, string SiteCapabilitiesJson);

    /// <summary>
    /// The attempt's Site and source, when the attempt is open and
    /// <paramref name="fencingEpoch"/> is its epoch. A closed or displaced attempt
    /// is refused by name: its code is not handed out.
    /// </summary>
    public static RegistryResult<AttemptCodeView> ReadOpenAttemptCode(SqliteConnection connection, long attemptId, long fencingEpoch)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.state, a.fencing_epoch, a.site_id, s.source_id, si.capabilities_json
            FROM attempts a
            JOIN cells c ON c.id = a.cell_id
            JOIN studies s ON s.id = c.study_id
            JOIN sites si ON si.id = a.site_id
            WHERE a.id = $id
            """;
        command.Parameters.AddWithValue("$id", attemptId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return RegistryResult<AttemptCodeView>.Rejected(NotFound($"attempt {attemptId} is not registered"));
        }
        var state = reader.GetString(0);
        if (state is not (AttemptOpened or AttemptRunning))
        {
            return RegistryResult<AttemptCodeView>.Rejected(Invalid($"attempt {attemptId} is {state}, not open; its code is not handed out"));
        }
        if (reader.GetInt64(1) != fencingEpoch)
        {
            return RegistryResult<AttemptCodeView>.Rejected(Fenced(attemptId, fencingEpoch));
        }
        return RegistryResult<AttemptCodeView>.Ok(
            new AttemptCodeView(attemptId, reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
    }

    /// <summary>The registered source, or null when no source has this id.</summary>
    public static SourceDeclaration? ReadSource(SqliteConnection connection, long sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT commit_sha, declared_files_json, registered_from FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SourceDeclaration(sourceId, reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    // ── dispatch priorities ───────────────────────────────────────────────

    /// <summary>A person's priority over a study or a cell: where in the offer it goes. CHG 2026-09-11 night (slice 7, row 12).</summary>
    /// <remarks>
    /// CHG 2026-09-25 (slice P2 of the scheduling loop): <c>Priority</c> is one
    /// of the four rank words, and <c>ConvertedFrom</c> is the number a row
    /// written before the ranks held, or null.
    /// </remarks>
    public sealed record DispatchPriority(
        string Scope, string Priority, string Reason, string SetBy, string SetAt, long? ConvertedFrom = null);

    /// <summary>The four ranks, highest first. A study or cell without a priority is normal.</summary>
    public static readonly IReadOnlyList<string> PriorityRanks = ["immediate", "urgent", "normal", "slow"];

    /// <summary>
    /// Set the priority of a study or a cell (scope <c>study:&lt;id&gt;</c> or
    /// <c>cell:&lt;id&gt;</c>), replacing an earlier one in place.
    /// The retired queue's position was a rank among Plans; a priority is the
    /// same intent without a person having to renumber the rest.
    ///
    /// CHG 2026-09-25 (slice P2): the priority is one of four ranks, highest
    /// first immediate, urgent, normal, slow. A cell's own rank overrides its
    /// study's. Setting a rank never changes where the work stands within its
    /// rank, so setting the same rank again moves nothing. "last" is refused
    /// here: it is a move, made by <see cref="MoveLast"/>.
    /// </summary>
    public static RegistryResult<DispatchPriority> SetPriority(
        RegistryWriter writer, string scope, string priority, string reason, string setBy)
    {
        if (!WorkScope(scope) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(setBy))
        {
            return RegistryResult<DispatchPriority>.Rejected(
                Invalid("a priority names one study or one cell, a reason and who set it"));
        }
        if (priority == "last")
        {
            return RegistryResult<DispatchPriority>.Rejected(Invalid(
                "last is a move, not a priority: move-last puts the work behind every cell of its rank waiting now, once"));
        }
        if (!PriorityRanks.Contains(priority, StringComparer.Ordinal))
        {
            return RegistryResult<DispatchPriority>.Rejected(Invalid(
                $"a priority is one of immediate, urgent, normal or slow; got \"{priority}\""));
        }
        return writer.WriteImmediate(connection =>
        {
            if (ScopeMissing(connection, scope))
            {
                return RegistryResult<DispatchPriority>.Rejected(NotFound($"{scope} is not registered"));
            }
            var now = Now();
            using var upsert = connection.CreateCommand();
            upsert.CommandText =
                """
                INSERT INTO dispatch_priorities (scope, priority, reason, set_by, set_at)
                VALUES ($scope, $priority, $reason, $by, $now)
                ON CONFLICT (scope) DO UPDATE SET
                    priority = excluded.priority, reason = excluded.reason, set_by = excluded.set_by,
                    set_at = excluded.set_at, converted_from = NULL
                """;
            upsert.Parameters.AddWithValue("$scope", scope);
            upsert.Parameters.AddWithValue("$priority", priority);
            upsert.Parameters.AddWithValue("$reason", reason);
            upsert.Parameters.AddWithValue("$by", setBy);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
            return RegistryResult<DispatchPriority>.Ok(new DispatchPriority(scope, priority, reason, setBy, now));
        });
    }

    /// <summary>The priority set over a scope, or null when it has the default.</summary>
    public static DispatchPriority? ReadPriority(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT scope, priority, reason, set_by, set_at, converted_from FROM dispatch_priorities WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DispatchPriority(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5))
            : null;
    }

    /// <summary>Every priority set, with the number a converted row held, in scope order.</summary>
    public static IReadOnlyList<DispatchPriority> ListPriorities(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT scope, priority, reason, set_by, set_at, converted_from FROM dispatch_priorities ORDER BY scope";
        using var reader = command.ExecuteReader();
        var rows = new List<DispatchPriority>();
        while (reader.Read())
        {
            rows.Add(new DispatchPriority(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }
        return rows;
    }

    /// <summary>A person's move of a study or a cell behind every cell of its rank waiting then. CHG 2026-09-25 (slice P2).</summary>
    public sealed record LastMove(string Scope, long BehindStudyId, string Reason, string SetBy, string SetAt);

    /// <summary>
    /// Move a study or a cell to the back of its rank, once: behind every
    /// study registered so far, ahead of every study registered afterwards.
    /// Its rank is unchanged. Moving the same work again moves it again,
    /// behind what is registered by then.
    /// </summary>
    public static RegistryResult<LastMove> MoveLast(RegistryWriter writer, string scope, string reason, string setBy)
    {
        if (!WorkScope(scope) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(setBy))
        {
            return RegistryResult<LastMove>.Rejected(
                Invalid("a move names one study or one cell, a reason and who made it"));
        }
        return writer.WriteImmediate(connection =>
        {
            if (ScopeMissing(connection, scope))
            {
                return RegistryResult<LastMove>.Rejected(NotFound($"{scope} is not registered"));
            }
            long latest;
            using (var newest = connection.CreateCommand())
            {
                newest.CommandText = "SELECT MAX(id) FROM studies";
                latest = Convert.ToInt64(newest.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var now = Now();
            using var upsert = connection.CreateCommand();
            upsert.CommandText =
                """
                INSERT INTO dispatch_last_moves (scope, behind_study_id, reason, set_by, set_at)
                VALUES ($scope, $behind, $reason, $by, $now)
                ON CONFLICT (scope) DO UPDATE SET
                    behind_study_id = excluded.behind_study_id, reason = excluded.reason,
                    set_by = excluded.set_by, set_at = excluded.set_at
                """;
            upsert.Parameters.AddWithValue("$scope", scope);
            upsert.Parameters.AddWithValue("$behind", latest);
            upsert.Parameters.AddWithValue("$reason", reason);
            upsert.Parameters.AddWithValue("$by", setBy);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
            return RegistryResult<LastMove>.Ok(new LastMove(scope, latest, reason, setBy, now));
        });
    }

    /// <summary>The move made over a scope, or null when none was.</summary>
    public static LastMove? ReadLastMove(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scope, behind_study_id, reason, set_by, set_at FROM dispatch_last_moves WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new LastMove(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    /// <summary>The compiler that compiled a study (slice M2 of what-stops-work-and-who-lifts-it).</summary>
    public sealed record StudyCompiler(long StudyId, string Sha256, string Path, string RecordedBy, string RecordedAt);

    /// <summary>
    /// Record which compiler compiled a study, by the sha256 of its bytes and
    /// the path it ran from, replacing an earlier record. register-study calls
    /// it with what the compiled study names; set-study-compiler for a study
    /// registered before studies named it.
    /// </summary>
    public static RegistryResult<StudyCompiler> SetStudyCompiler(
        RegistryWriter writer, long studyId, string sha256, string path, string recordedBy)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(recordedBy))
        {
            return RegistryResult<StudyCompiler>.Rejected(
                Invalid("a study's compiler is the sha256 of its bytes (64 hexadecimal digits), a path and who records it"));
        }
        return writer.WriteImmediate(connection =>
        {
            if (ScopeMissing(connection, StudyScope(studyId)))
            {
                return RegistryResult<StudyCompiler>.Rejected(NotFound($"study {studyId} is not registered"));
            }
            var now = Now();
            using var upsert = connection.CreateCommand();
            upsert.CommandText =
                """
                INSERT INTO study_compilers (study_id, compiler_sha256, compiler_path, recorded_by, recorded_at)
                VALUES ($study, $sha, $path, $by, $now)
                ON CONFLICT (study_id) DO UPDATE SET
                    compiler_sha256 = excluded.compiler_sha256, compiler_path = excluded.compiler_path,
                    recorded_by = excluded.recorded_by, recorded_at = excluded.recorded_at
                """;
            upsert.Parameters.AddWithValue("$study", studyId);
            upsert.Parameters.AddWithValue("$sha", sha256.ToLowerInvariant());
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$by", recordedBy);
            upsert.Parameters.AddWithValue("$now", now);
            upsert.ExecuteNonQuery();
            return RegistryResult<StudyCompiler>.Ok(new StudyCompiler(studyId, sha256.ToLowerInvariant(), path, recordedBy, now));
        });
    }

    /// <summary>The compiler recorded for a study, or null when none is: a supervisor then uses its own.</summary>
    public static StudyCompiler? ReadStudyCompiler(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT study_id, compiler_sha256, compiler_path, recorded_by, recorded_at FROM study_compilers WHERE study_id = $study";
        command.Parameters.AddWithValue("$study", studyId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StudyCompiler(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    /// <summary>
    /// CHG 2026-09-26 (slice F6 of how-a-site-is-reached-and-started): every
    /// compiler digest a registered study was compiled with. The installer's
    /// cleanup keeps these: on 2026-09-26 it removed <c>fern-compile.888e83de…</c>
    /// while the only unit naming it was paused, and the density studies
    /// compiled with it could not have run again.
    /// </summary>
    public static IReadOnlyList<string> ListStudyCompilerDigests(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT compiler_sha256 FROM study_compilers ORDER BY compiler_sha256";
        using var reader = command.ExecuteReader();
        var digests = new List<string>();
        while (reader.Read()) digests.Add(reader.GetString(0));
        return digests;
    }

    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): SiteCompiler,
    // RecordSiteCompiler, ReadSiteCompiler and ListSiteCompilers left with the
    // site_compilers table; no compiler is carried to a Site any more.

    /// <summary>Every priority and every move, keyed by scope: what orders the offer, read in one go.</summary>
    public sealed record DispatchOrder(
        IReadOnlyDictionary<string, string> Priorities, IReadOnlyDictionary<string, LastMove> Moves);

    public static DispatchOrder ReadDispatchOrder(SqliteConnection connection)
    {
        var priorities = new Dictionary<string, string>(StringComparer.Ordinal);
        var moves = new Dictionary<string, LastMove>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scope, priority FROM dispatch_priorities";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) priorities[reader.GetString(0)] = reader.GetString(1);
        }
        command.CommandText = "SELECT scope, behind_study_id, reason, set_by, set_at FROM dispatch_last_moves";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                moves[reader.GetString(0)] = new LastMove(
                    reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            }
        }
        return new DispatchOrder(priorities, moves);
    }

    /// <summary>A priority or a move is over one study or one cell, never a Site or everything.</summary>
    static bool WorkScope(string scope) =>
        (scope.StartsWith("study:", StringComparison.Ordinal) && long.TryParse(scope.AsSpan(6), out _))
        || (scope.StartsWith("cell:", StringComparison.Ordinal) && long.TryParse(scope.AsSpan(5), out _));

    static bool ScopeMissing(SqliteConnection connection, string scope)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = scope.StartsWith("study:", StringComparison.Ordinal)
            ? "SELECT COUNT(*) FROM studies WHERE id = $id"
            : "SELECT COUNT(*) FROM cells WHERE id = $id";
        exists.Parameters.AddWithValue("$id", long.Parse(scope[(scope.IndexOf(':') + 1)..], CultureInfo.InvariantCulture));
        return Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    // ── 5. RenewAttempt ──────────────────────────────────────────────────────

    /// <summary>
    /// A worker says it is still alive, and moves its attempt to
    /// <c>running</c>. Fenced: the update carries the epoch the worker was
    /// claimed with, so a displaced worker's renewal touches no row.
    ///
    /// It OVERWRITES one column and never accumulates: a six-hour cell at a
    /// two-minute cadence is 180 beats and 180 in-place updates of
    /// <c>last_progress_at</c>, not 180 rows. Liveness is a current fact, and a
    /// history of it would be a table growing with wall-clock time for no reader.
    /// The worker writes the same fact as one overwritten file under the
    /// attempt's folder, so a person can answer "is this run alive" by reading
    /// that file rather than by hunting a process.
    /// </summary>
    public static RegistryResult<long> RenewAttempt(
        RegistryWriter writer, long attemptId, long fencingEpoch, string? remoteExecutionId = null)
    {
        return writer.WriteImmediate(connection =>
        {
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE attempts
                SET state = 'running',
                    last_progress_at = $now,
                    remote_execution_id = COALESCE($remote, remote_execution_id)
                WHERE id = $id AND fencing_epoch = $epoch AND state IN ('opened', 'running')
                """;
            update.Parameters.AddWithValue("$id", attemptId);
            update.Parameters.AddWithValue("$epoch", fencingEpoch);
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$remote", (object?)remoteExecutionId ?? DBNull.Value);
            var touched = update.ExecuteNonQuery();
            return touched == 0
                ? RegistryResult<long>.Rejected(Fenced(attemptId, fencingEpoch))
                : RegistryResult<long>.Ok(attemptId);
        });
    }

    // ── 6. RecordEvent ───────────────────────────────────────────────────────

    /// <summary>
    /// Append one observation about an attempt: a phase receipt, a progress
    /// report, an output disposition. Fenced the same way — the insert happens
    /// only if the attempt is still the caller's, which is expressed as an
    /// UPDATE of the attempt's progress time in the same transaction.
    /// </summary>
    public static RegistryResult<long> RecordEvent(
        RegistryWriter writer, long attemptId, long fencingEpoch, string kind, string? phase, string detailJson)
    {
        return writer.WriteImmediate(connection =>
        {
            var now = Now();
            using (var touch = connection.CreateCommand())
            {
                touch.CommandText =
                    """
                    UPDATE attempts SET last_progress_at = $now WHERE id = $id AND fencing_epoch = $epoch
                      AND (state IN ('opened', 'running') OR
                           ($kind = 'retry_requested' AND state IN ('failed', 'lost', 'cancelled')
                            AND NOT EXISTS (SELECT 1 FROM attempts newer
                                WHERE newer.cell_id = attempts.cell_id AND newer.ordinal > attempts.ordinal)))
                    """;
                touch.Parameters.AddWithValue("$kind", kind);
                touch.Parameters.AddWithValue("$id", attemptId);
                touch.Parameters.AddWithValue("$epoch", fencingEpoch);
                touch.Parameters.AddWithValue("$now", now);
                if (touch.ExecuteNonQuery() == 0)
                {
                    return RegistryResult<long>.Rejected(Fenced(attemptId, fencingEpoch));
                }
            }
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO events (attempt_id, at, kind, phase, detail_json)
                VALUES ($attempt, $now, $kind, $phase, $detail) RETURNING id
                """;
            insert.Parameters.AddWithValue("$attempt", attemptId);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$phase", (object?)phase ?? DBNull.Value);
            insert.Parameters.AddWithValue("$detail", detailJson);
            return RegistryResult<long>.Ok(Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture));
        });
    }

    // ── 7. FinishAttempt ─────────────────────────────────────────────────────

    /// <summary>
    /// An attempt completed, and its result is the cell's.
    ///
    /// Two writes in one transaction, both conditional. The attempt moves to
    /// <c>completed</c> only if it is still current at the presented epoch; the
    /// cell's <c>accepted_attempt_id</c> is set only if it is still NULL, so a
    /// second completion on the same cell is refused rather than overwriting
    /// the accepted result. That is the property a table of accepted results
    /// could not give: a column can be null once.
    /// </summary>
    public static RegistryResult<long> FinishAttempt(
        RegistryWriter writer, long attemptId, long fencingEpoch, string resultJson)
    {
        return writer.WriteImmediate(connection =>
        {
            // The accepted-result check comes BEFORE the attempt's update, and
            // it has to. A refusal is a returned value, not an exception, so
            // WriteImmediate commits the transaction either way: an update made
            // before a refusal would land. Inside BEGIN IMMEDIATE a read then a
            // write is atomic, so reading first costs nothing and is the only
            // ordering that cannot half-land.
            //
            // The FENCING check stays in the write below, where it belongs: it
            // is a race against a successor, and only a conditional update
            // settles that without a window.
            long cellId;
            using (var owner = connection.CreateCommand())
            {
                owner.CommandText = "SELECT cell_id FROM attempts WHERE id = $id AND fencing_epoch = $epoch";
                owner.Parameters.AddWithValue("$id", attemptId);
                owner.Parameters.AddWithValue("$epoch", fencingEpoch);
                var found = owner.ExecuteScalar();
                if (found is null)
                {
                    return RegistryResult<long>.Rejected(Fenced(attemptId, fencingEpoch));
                }
                cellId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
            }
            using (var accepted = connection.CreateCommand())
            {
                accepted.CommandText = "SELECT accepted_attempt_id FROM cells WHERE id = $cell";
                accepted.Parameters.AddWithValue("$cell", cellId);
                var already = accepted.ExecuteScalar();
                if (already is not null and not DBNull)
                {
                    return RegistryResult<long>.Rejected(Invalid(
                        $"cell {cellId} already has accepted attempt {Convert.ToInt64(already, CultureInfo.InvariantCulture)}; a cell accepts one result"));
                }
            }

            using (var update = connection.CreateCommand())
            {
                update.CommandText =
                    """
                    UPDATE attempts
                    SET state = 'completed', result_json = $result, ended_at = $now, last_progress_at = $now
                    WHERE id = $id AND fencing_epoch = $epoch AND state IN ('opened', 'running')
                    """;
                update.Parameters.AddWithValue("$id", attemptId);
                update.Parameters.AddWithValue("$epoch", fencingEpoch);
                update.Parameters.AddWithValue("$result", resultJson);
                update.Parameters.AddWithValue("$now", Now());
                if (update.ExecuteNonQuery() == 0)
                {
                    return RegistryResult<long>.Rejected(Fenced(attemptId, fencingEpoch));
                }
            }

            ReleaseDeviceGrantsOf(connection, attemptId, "completed");
            using var accept = connection.CreateCommand();
            accept.CommandText =
                "UPDATE cells SET accepted_attempt_id = $attempt WHERE id = $cell AND accepted_attempt_id IS NULL";
            accept.Parameters.AddWithValue("$attempt", attemptId);
            accept.Parameters.AddWithValue("$cell", cellId);
            accept.ExecuteNonQuery();
            return RegistryResult<long>.Ok(cellId);
        });
    }

    // ── 8. FailAttempt ───────────────────────────────────────────────────────

    /// <summary>
    /// An attempt ended without a result: failed, lost or cancelled. Fenced,
    /// and it never touches the cell's accepted result, so a failure after a
    /// success cannot unseat it.
    /// </summary>
    public static RegistryResult<long> FailAttempt(
        RegistryWriter writer, long attemptId, long fencingEpoch, string state, string detailJson)
    {
        if (state is not (AttemptFailed or AttemptLost or AttemptCancelled))
        {
            return RegistryResult<long>.Rejected(
                Invalid($"an attempt ends without a result as failed, lost or cancelled, not {state}"));
        }
        return writer.WriteImmediate(connection =>
        {
            using (var update = connection.CreateCommand())
            {
                update.CommandText =
                    """
                    UPDATE attempts SET state = $state, ended_at = $now, last_progress_at = $now
                    WHERE id = $id AND fencing_epoch = $epoch AND state IN ('opened', 'running')
                    """;
                update.Parameters.AddWithValue("$id", attemptId);
                update.Parameters.AddWithValue("$epoch", fencingEpoch);
                update.Parameters.AddWithValue("$state", state);
                update.Parameters.AddWithValue("$now", Now());
                if (update.ExecuteNonQuery() == 0)
                {
                    return RegistryResult<long>.Rejected(Fenced(attemptId, fencingEpoch));
                }
            }
            ReleaseDeviceGrantsOf(connection, attemptId, state);
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO events (attempt_id, at, kind, phase, detail_json)
                VALUES ($attempt, $now, $state, NULL, $detail)
                """;
            insert.Parameters.AddWithValue("$attempt", attemptId);
            insert.Parameters.AddWithValue("$now", Now());
            insert.Parameters.AddWithValue("$state", state);
            insert.Parameters.AddWithValue("$detail", detailJson);
            insert.ExecuteNonQuery();
            return RegistryResult<long>.Ok(attemptId);
        });
    }

    // ── 9. RequestStudyState ──────────────────────────────────────────────────

    /// <summary>
    /// Group control: run, pause or cancel a whole study. This is the field
    /// <see cref="TryStartAttempt"/> reads before it claims, so a paused or
    /// cancelled study claims no new cell.
    ///
    /// What happens to an attempt already running is NOT decided here; it is
    /// the owner's policy (gate g2), and this operation deliberately touches no
    /// attempt row.
    /// </summary>
    public static RegistryResult<string> RequestStudyState(RegistryWriter writer, long studyId, string state)
    {
        if (state is not (StateRun or StatePause or StateCancel))
        {
            return RegistryResult<string>.Rejected(Invalid($"a study is asked to run, pause or cancel, not {state}"));
        }
        return writer.WriteImmediate(connection =>
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE studies SET requested_state = $state WHERE id = $id";
            update.Parameters.AddWithValue("$id", studyId);
            update.Parameters.AddWithValue("$state", state);
            return update.ExecuteNonQuery() == 0
                ? RegistryResult<string>.Rejected(NotFound($"study {studyId} is not registered"))
                : RegistryResult<string>.Ok(state);
        });
    }

    // ── 10. ReadStudy ─────────────────────────────────────────────────────────

    /// <summary>One registered study as a person or a monitor reads it. Read-only; writes nothing.</summary>
    public static RegistryResult<StudyView> ReadStudy(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.id, p.name, p.revision, p.requested_state, p.source_id, p.compiled_study_json,
                   p.supervisor_lease, p.supervisor_epoch,
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = p.id),
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = p.id AND c.accepted_attempt_id IS NOT NULL)
            FROM studies p WHERE p.id = $id
            """;
        command.Parameters.AddWithValue("$id", studyId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return RegistryResult<StudyView>.Rejected(NotFound($"study {studyId} is not registered"));
        }
        return RegistryResult<StudyView>.Ok(new StudyView(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
            reader.GetInt32(8), reader.GetInt32(9)));
    }

    // ── 11. the supervisor office, one per Site ───────────────────────────────
    //
    // CHG 2026-09-11 night (slice 1 of the cell port, design note
    // docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
    // rows 24-27). The retired SchedulerOffice claimed ONE office per Home with
    // a written expiry and a fencing epoch, renewed it every cycle, released it
    // on exit, and SchedulerServiceAttempts recorded each resident lifetime with
    // the typed outcome the executable mapped to an exit code. Here the office
    // is per Site, because the machine that claims cells is the one that must
    // be minded, and lifetime and lease are one row: a supervisor that dies
    // leaves `ended_at` NULL behind an expired lease, which a reader projects as
    // unconfirmed and never as stopped.

    public const string DispositionCompleted = "completed";
    public const string DispositionRetryable = "retryable";
    public const string DispositionTerminalConfiguration = "terminal_configuration";
    public const string DispositionFatal = "fatal";

    /// <summary>The retired daemon's bound between beats, kept as the office lease.</summary>
    public const long DefaultSupervisorLeaseSeconds = 600;

    /// <summary>What a claimed office gives the supervisor: the row, its Site, the epoch every later write presents, and when the lease runs out.</summary>
    public sealed record SupervisorPermit(long SupervisorId, long SiteId, long Epoch, string LeaseUntil);

    /// <summary>One supervisor lifetime as stored.</summary>
    public sealed record SupervisorView(
        long SupervisorId, long SiteId, string Host, long Pid, long Epoch, string LeaseUntil,
        string BinarySha256, string StartedAt, string? EndedAt, string? Outcome, string? Disposition, long? ExitCode)
    {
        /// <summary>Not ended, and the written expiry has not passed at <paramref name="now"/>.</summary>
        public bool HoldsOfficeAt(string now) => EndedAt is null && string.CompareOrdinal(LeaseUntil, now) >= 0;

        /// <summary>Never ended, lease run out: the process died without saying how.</summary>
        public bool IsUnconfirmedAt(string now) => EndedAt is null && string.CompareOrdinal(LeaseUntil, now) < 0;
    }

    static string LeaseUntil(long leaseSeconds) =>
        DateTimeOffset.UtcNow.AddSeconds(leaseSeconds).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Claim the office of one Site for this process. Refused by name while
    /// another supervisor's lease is live; an expired or ended predecessor is
    /// left as it is (its row is its own record) and the successor takes the
    /// next epoch.
    /// </summary>
    public static RegistryResult<SupervisorPermit> StartSupervisor(
        RegistryWriter writer, long siteId, string host, long pid, string binarySha256,
        long leaseSeconds = DefaultSupervisorLeaseSeconds)
    {
        if (string.IsNullOrWhiteSpace(host) || pid <= 0 || string.IsNullOrWhiteSpace(binarySha256) || leaseSeconds <= 0)
        {
            return RegistryResult<SupervisorPermit>.Rejected(
                Invalid("a supervisor names its host, pid and binary digest, and holds a positive lease"));
        }
        return writer.WriteImmediate(connection =>
        {
            if (ReadSite(connection, siteId) is null)
            {
                return RegistryResult<SupervisorPermit>.Rejected(NotFound($"site {siteId} is not registered"));
            }
            var now = Now();
            var latest = ReadSupervisor(connection, siteId);
            if (latest is not null && latest.HoldsOfficeAt(now))
            {
                return RegistryResult<SupervisorPermit>.Rejected(new RegistryRefusal("refused", "held",
                    $"site {siteId} is supervised by {latest.Host} pid {latest.Pid} at epoch {latest.Epoch} "
                    + $"until {latest.LeaseUntil}; this process holds no office and should exit and be retried",
                    HolderActor: "pid " + latest.Pid.ToString(CultureInfo.InvariantCulture),
                    HolderHost: latest.Host));
            }
            var epoch = (latest?.Epoch ?? 0) + 1;
            var until = LeaseUntil(leaseSeconds);
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO supervisors (site_id, host, pid, epoch, lease_until, binary_sha256, started_at)
                VALUES ($site, $host, $pid, $epoch, $until, $binary, $now) RETURNING id
                """;
            insert.Parameters.AddWithValue("$site", siteId);
            insert.Parameters.AddWithValue("$host", host);
            insert.Parameters.AddWithValue("$pid", pid);
            insert.Parameters.AddWithValue("$epoch", epoch);
            insert.Parameters.AddWithValue("$until", until);
            insert.Parameters.AddWithValue("$binary", binarySha256);
            insert.Parameters.AddWithValue("$now", now);
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            return RegistryResult<SupervisorPermit>.Ok(new SupervisorPermit(id, siteId, epoch, until));
        });
    }

    /// <summary>
    /// Move the lease forward. Fenced: a supervisor that was displaced by a
    /// successor epoch, or that already ended, touches zero rows and is told so.
    /// A late renewal with no successor is accepted: nobody took the office.
    /// </summary>
    public static RegistryResult<string> RenewSupervisor(
        RegistryWriter writer, SupervisorPermit permit, long leaseSeconds = DefaultSupervisorLeaseSeconds)
    {
        return writer.WriteImmediate(connection =>
        {
            var until = LeaseUntil(leaseSeconds);
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE supervisors SET lease_until = $until
                WHERE id = $id AND epoch = $epoch AND ended_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM supervisors s WHERE s.site_id = supervisors.site_id AND s.epoch > $epoch)
                """;
            update.Parameters.AddWithValue("$id", permit.SupervisorId);
            update.Parameters.AddWithValue("$epoch", permit.Epoch);
            update.Parameters.AddWithValue("$until", until);
            return update.ExecuteNonQuery() == 0
                ? RegistryResult<string>.Rejected(new RegistryRefusal("refused", "fenced",
                    $"supervisor {permit.SupervisorId} no longer holds the office of site {permit.SiteId} at epoch {permit.Epoch}",
                    StaleEpoch: permit.Epoch))
                : RegistryResult<string>.Ok(until);
        });
    }

    /// <summary>
    /// Record how this lifetime ended: the typed outcome, the disposition the
    /// executable maps to an exit code, and that code. An identical terminal
    /// replay is zero mutation; a conflicting one is refused, because two
    /// different endings of one process cannot both be true.
    /// </summary>
    public static RegistryResult<long> EndSupervisor(
        RegistryWriter writer, SupervisorPermit permit, string outcome, string disposition, long exitCode)
    {
        if (string.IsNullOrWhiteSpace(outcome)
            || disposition is not (DispositionCompleted or DispositionRetryable or DispositionTerminalConfiguration or DispositionFatal))
        {
            return RegistryResult<long>.Rejected(
                Invalid("a supervisor ends with an outcome and one of the four dispositions: completed, retryable, terminal_configuration, fatal"));
        }
        return writer.WriteImmediate(connection =>
        {
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT ended_at, outcome, disposition, exit_code FROM supervisors WHERE id = $id AND epoch = $epoch";
                read.Parameters.AddWithValue("$id", permit.SupervisorId);
                read.Parameters.AddWithValue("$epoch", permit.Epoch);
                using var row = read.ExecuteReader();
                if (!row.Read())
                {
                    return RegistryResult<long>.Rejected(NotFound($"supervisor {permit.SupervisorId} at epoch {permit.Epoch} is not recorded"));
                }
                if (!row.IsDBNull(0))
                {
                    var same = row.GetString(1) == outcome && row.GetString(2) == disposition && row.GetInt64(3) == exitCode;
                    return same
                        ? RegistryResult<long>.Ok(permit.SupervisorId)
                        : RegistryResult<long>.Rejected(new RegistryRefusal("refused", "conflict",
                            $"supervisor {permit.SupervisorId} already ended as {row.GetString(1)} ({row.GetString(2)}, exit {row.GetInt64(3)}); "
                            + $"it cannot also have ended as {outcome} ({disposition}, exit {exitCode})"));
                }
            }
            var now = Now();
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE supervisors
                SET ended_at = $now, lease_until = $now, outcome = $outcome, disposition = $disposition, exit_code = $exit
                WHERE id = $id AND epoch = $epoch AND ended_at IS NULL
                """;
            update.Parameters.AddWithValue("$id", permit.SupervisorId);
            update.Parameters.AddWithValue("$epoch", permit.Epoch);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$outcome", outcome);
            update.Parameters.AddWithValue("$disposition", disposition);
            update.Parameters.AddWithValue("$exit", exitCode);
            update.ExecuteNonQuery();
            return RegistryResult<long>.Ok(permit.SupervisorId);
        });
    }

    /// <summary>The latest supervisor lifetime of one Site, or null when none was ever started.</summary>
    public static SupervisorView? ReadSupervisor(SqliteConnection connection, long siteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, site_id, host, pid, epoch, lease_until, binary_sha256, started_at,
                   ended_at, outcome, disposition, exit_code
            FROM supervisors WHERE site_id = $site ORDER BY epoch DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$site", siteId);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        return new SupervisorView(
            row.GetInt64(0), row.GetInt64(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4), row.GetString(5),
            row.GetString(6), row.GetString(7),
            row.IsDBNull(8) ? null : row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9),
            row.IsDBNull(10) ? null : row.GetString(10), row.IsDBNull(11) ? null : row.GetInt64(11));
    }

    // ── 12. operator requests: pause, stop, hold ──────────────────────────────
    //
    // The retired `scheduling_pauses`, `scheduler_stop_requests` and the queue's
    // per-Plan and per-cell holds were three tables for one question: what did
    // a person ask of the machines, and has it been acted on. Here they are one
    // table with a kind and a scope. A stop is consumed by the supervisor it
    // addresses, between cells; a pause or a hold stands until released.

    public const string RequestPause = "pause";
    public const string RequestStop = "stop";
    public const string RequestHold = "hold";
    public const string ScopeAll = "all";

    public static string SiteScope(long siteId) => "site:" + siteId.ToString(CultureInfo.InvariantCulture);
    public static string StudyScope(long studyId) => "study:" + studyId.ToString(CultureInfo.InvariantCulture);
    public static string CellScope(long cellId) => "cell:" + cellId.ToString(CultureInfo.InvariantCulture);

    public sealed record OperatorRequest(
        long RequestId, string Kind, string Scope, string Reason, string RequestedBy, string RequestedAt,
        string? ConsumedAt, long? ConsumedBy, string? ReleasedAt);

    static bool ValidScope(string scope) =>
        scope == ScopeAll
        || (scope.StartsWith("site:", StringComparison.Ordinal) && long.TryParse(scope.AsSpan(5), out _))
        || (scope.StartsWith("study:", StringComparison.Ordinal) && long.TryParse(scope.AsSpan(6), out _))
        || (scope.StartsWith("cell:", StringComparison.Ordinal) && long.TryParse(scope.AsSpan(5), out _));

    /// <summary>Record that a person asked for a pause, a stop or a hold, over one scope, with a reason.</summary>
    public static RegistryResult<long> RequestOperator(
        RegistryWriter writer, string kind, string scope, string reason, string requestedBy)
    {
        if (kind is not (RequestPause or RequestStop or RequestHold))
        {
            return RegistryResult<long>.Rejected(Invalid($"an operator asks for pause, stop or hold, not {kind}"));
        }
        if (!ValidScope(scope))
        {
            return RegistryResult<long>.Rejected(Invalid($"a request scope is all, site:<id>, study:<id> or cell:<id>, not {scope}"));
        }
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(requestedBy))
        {
            return RegistryResult<long>.Rejected(Invalid("an operator request carries a reason and who asked"));
        }
        return writer.WriteImmediate(connection =>
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO operator_requests (kind, scope, reason, requested_by, requested_at)
                VALUES ($kind, $scope, $reason, $by, $now) RETURNING id
                """;
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$reason", reason);
            insert.Parameters.AddWithValue("$by", requestedBy);
            insert.Parameters.AddWithValue("$now", Now());
            return RegistryResult<long>.Ok(Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture));
        });
    }

    /// <summary>The requests of one kind still standing (neither consumed nor released) over any of the given scopes, oldest first.</summary>
    public static IReadOnlyList<OperatorRequest> PendingRequests(SqliteConnection connection, string kind, params string[] scopes)
    {
        using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < scopes.Length; i++)
        {
            names.Add("$scope" + i.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(names[i], scopes[i]);
        }
        command.CommandText =
            $"""
            SELECT id, kind, scope, reason, requested_by, requested_at, consumed_at, consumed_by, released_at
            FROM operator_requests
            WHERE kind = $kind AND consumed_at IS NULL AND released_at IS NULL
              AND scope IN ({(names.Count == 0 ? "''" : string.Join(", ", names))})
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$kind", kind);
        var rows = new List<OperatorRequest>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRequest(reader));
        }
        return rows;
    }

    static OperatorRequest ReadRequest(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8));

    /// <summary>
    /// The oldest stop addressed to this supervisor's Site or to every Site,
    /// marked consumed by it, or null when none stands. Fenced by the office:
    /// a displaced supervisor consumes nothing, so a stop meant for the machine
    /// is acted on by the process that actually holds it.
    /// </summary>
    public static RegistryResult<OperatorRequest?> ConsumeStop(RegistryWriter writer, SupervisorPermit permit)
    {
        return writer.WriteImmediate(connection =>
        {
            using (var current = connection.CreateCommand())
            {
                current.CommandText =
                    """
                    SELECT COUNT(*) FROM supervisors
                    WHERE id = $id AND epoch = $epoch AND ended_at IS NULL
                      AND NOT EXISTS (SELECT 1 FROM supervisors s WHERE s.site_id = supervisors.site_id AND s.epoch > $epoch)
                    """;
                current.Parameters.AddWithValue("$id", permit.SupervisorId);
                current.Parameters.AddWithValue("$epoch", permit.Epoch);
                if (Convert.ToInt64(current.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    return RegistryResult<OperatorRequest?>.Rejected(new RegistryRefusal("refused", "fenced",
                        $"supervisor {permit.SupervisorId} no longer holds the office of site {permit.SiteId} at epoch {permit.Epoch}",
                        StaleEpoch: permit.Epoch));
                }
            }
            var pending = PendingRequests(connection, RequestStop, ScopeAll, SiteScope(permit.SiteId));
            if (pending.Count == 0)
            {
                return RegistryResult<OperatorRequest?>.Ok(null);
            }
            var request = pending[0];
            var now = Now();
            using var consume = connection.CreateCommand();
            consume.CommandText =
                "UPDATE operator_requests SET consumed_at = $now, consumed_by = $by WHERE id = $id AND consumed_at IS NULL";
            consume.Parameters.AddWithValue("$now", now);
            consume.Parameters.AddWithValue("$by", permit.SupervisorId);
            consume.Parameters.AddWithValue("$id", request.RequestId);
            consume.ExecuteNonQuery();
            return RegistryResult<OperatorRequest?>.Ok(request with { ConsumedAt = now, ConsumedBy = permit.SupervisorId });
        });
    }

    /// <summary>Release a standing pause or hold; a consumed stop cannot be released, it already happened.</summary>
    public static RegistryResult<long> ReleaseRequest(RegistryWriter writer, long requestId, string releasedBy, string reason)
    {
        if (string.IsNullOrWhiteSpace(releasedBy) || string.IsNullOrWhiteSpace(reason))
        {
            return RegistryResult<long>.Rejected(Invalid("a release carries who released and why"));
        }
        return writer.WriteImmediate(connection =>
        {
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE operator_requests SET released_at = $now, released_by = $by, release_reason = $reason
                WHERE id = $id AND released_at IS NULL AND consumed_at IS NULL
                """;
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$by", releasedBy);
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$id", requestId);
            return update.ExecuteNonQuery() == 0
                ? RegistryResult<long>.Rejected(NotFound($"operator request {requestId} is not standing: it was never made, was consumed, or was released"))
                : RegistryResult<long>.Ok(requestId);
        });
    }

    /// <summary>One accepted cell of a study: its name, which attempt was accepted, and the digest of that attempt's result bytes.</summary>
    public sealed record AcceptedCellView(string Cell, long AttemptOrdinal, string ResultSha256);

    /// <summary>The immutable registration and result of one accepted attempt; no filesystem claim is inferred.</summary>
    public sealed record AcceptedStudyCell(long StudyId, string Study, long Revision, long CellId, string Cell,
        long AttemptId, long SiteId, string Site, long SourceId, string Commit,
        string ConfigJson, string ResultJson);
    // CHG 2026-09-27 (config-hash-leaves, slice 4): ConfigHash left with cells.payload_sha256.
    // CHG 2026-09-26 (schema 21): SourceBundleSha256 left with sources.bundle_sha256.

    /// <summary>Read an exact Study revision's accepted cell for checkpoint naming. Opens no writer.</summary>
    public static RegistryResult<AcceptedStudyCell> ReadAcceptedStudyCell(
        SqliteConnection connection, string study, long revision, string cell)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.revision, c.id, c.name, a.id, a.site_id, site.name,
                   source.id, source.commit_sha,
                   c.config_json, a.result_json
            FROM studies s JOIN cells c ON c.study_id = s.id
            JOIN attempts a ON a.id = c.accepted_attempt_id AND a.cell_id = c.id
            JOIN sites site ON site.id = a.site_id JOIN sources source ON source.id = s.source_id
            WHERE s.name = $study AND s.revision = $revision AND c.name = $cell AND a.state = 'completed'
            """;
        command.Parameters.AddWithValue("$study", study);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$cell", cell);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(11))
            return RegistryResult<AcceptedStudyCell>.Rejected(NotFound(
                $"study {study} revision {revision} cell {cell} has no accepted completed attempt"));
        return RegistryResult<AcceptedStudyCell>.Ok(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7),
            reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
    }

    /// <summary>
    /// CHG 2026-09-11 night (slice 11 of the cell port): the accepted cells
    /// of a study, in cell order, from the rows that hold them: a cell's
    /// <c>accepted_attempt_id</c> names the attempt and the attempt's
    /// <c>result_json</c> is the result; the digest is over those bytes, so
    /// a statistic can say which results it read and a later re-acceptance is
    /// visibly other evidence. This is what <c>export-accepted-matrix</c>
    /// answered over the Matrix tables (design note
    /// 2026-09-11-statistics-admission-over-accepted-cells.md).
    /// </summary>
    public static IReadOnlyList<AcceptedCellView> AcceptedCells(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, a.ordinal, a.result_json
            FROM cells c JOIN attempts a ON a.id = c.accepted_attempt_id
            WHERE c.study_id = $study
            ORDER BY c.ordinal
            """;
        command.Parameters.AddWithValue("$study", studyId);
        using var reader = command.ExecuteReader();
        var cells = new List<AcceptedCellView>();
        while (reader.Read())
        {
            var result = reader.IsDBNull(2) ? "" : reader.GetString(2);
            cells.Add(new AcceptedCellView(
                reader.GetString(0),
                reader.GetInt64(1),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result)))));
        }
        return cells;
    }

    /// <summary>How many cells a study has, for "n accepted of m".</summary>
    public static long CellCount(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cells WHERE study_id = $study";
        command.Parameters.AddWithValue("$study", studyId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
