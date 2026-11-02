using System.CommandLine;
using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Fern.Scheduler.AttemptContracts;

namespace Fern.Run.Cli;

/// <summary>
/// CLI Pass 6's storage reclaimer. Everything it can remove is behind
/// <c>--apply</c>, and every refusal below is worded exactly as the Rust
/// handler worded it, because these are the sentences an operator reads
/// immediately before a deletion.
///
/// The typed commit check is the reason this verb was blocked until now: a
/// handoff directory only becomes a removal CANDIDATE once its
/// attempt_artifact_commit.v1.json validates against the worker request and
/// result beside it. That validation is
/// Fern.Scheduler.AttemptContracts.AttemptArtifactCommitContract.ValidateAgainst,
/// whose two content addresses are checked byte-for-byte against 1,876 real
/// committed attempts.
/// </summary>
public static class StorageReclaimVerbs
{
    static string DefaultRegistry => Fern.Registry.RegistryHome.DefaultRegistryPath;

    static readonly JsonSerializerOptions Report = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Add(Command parent) => parent.Subcommands.Add(ReclaimStorage());

    static int Refuse(string detail)
    {
        Console.Error.WriteLine($"reclaim-storage failed: {detail}");
        return 1;
    }

    /// <summary>One inventory row. `Decision` is one of the three words the
    /// Rust report used; nothing here invents a fourth.</summary>
    sealed record ReclaimItem(string Path, long Bytes, string Decision, string Reason);

    static Command ReclaimStorage()
    {
        var command = new Command(
            "reclaim-storage",
            "Preview or reclaim worker scratch from authoritative lifecycle facts.");
        var registry = new Option<string>("--registry")
        {
            Description = "The one Registry Home that owns lifecycle and accepted-artifact facts.",
            DefaultValueFactory = _ => string.Empty,
        };
        var workRoot = new Option<string>("--work-root")
        {
            Description = "Attempt handoff root. Only exact accepted and independently verified Attempt directories can be removed.",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.WorkerWorkRoot, // CHG 2026-09-26: was tmp/fern-run-worker
        };
        var scratch = new Option<bool>("--scratch") { Description = "Include removable worker handoff directories." };
        var deployRoot = new Option<string?>("--deploy-root")
        {
            Description = "Inventory this deploy/ directory too: per content-addressed family, what still names each digest "
                + "(running Plan attempt, live installation receipt, work.toml, profile, deployment record, Registry CAS) and what nothing names.",
        };
        var deploy = new Option<bool>("--deploy") { Description = "Include the deploy/ paths the inventory called removable." };
        var apply = new Option<bool>("--apply") { Description = "Perform the listed removals. Without this flag the command is read-only." };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text for humans, JSON for agents and scripts.",
            DefaultValueFactory = _ => "text",
        };
        format.AcceptOnlyFromAmong("text", "json");
        foreach (var option in new Option[] { registry, workRoot, scratch, deployRoot, deploy, apply, format })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            var wantScratch = parsed.GetValue(scratch);
            var wantDeploy = parsed.GetValue(deploy);
            var wantApply = parsed.GetValue(apply);

            var workspace = Path.GetFullPath(Directory.GetCurrentDirectory());
            string resolvedWork;
            string? resolvedDeploy = null;
            try
            {
                resolvedWork = ResolveInsideWorkspace(workspace, parsed.GetValue(workRoot)!);
                if (parsed.GetValue(deployRoot) is { } requestedDeploy)
                {
                    resolvedDeploy = ResolveInsideWorkspace(workspace, requestedDeploy);
                }
            }
            catch (StorageRefusal refusal)
            {
                return Refuse(refusal.Message);
            }

            var registryPath = parsed.GetValue(registry)!;
            var items = new List<ReclaimItem>();
            List<HandoffDirectoryEvidence> candidates;
            try
            {
                (var kept, candidates) = InspectHandoffFilesystem(resolvedWork);
                items.AddRange(kept);
            }
            catch (StorageRefusal refusal)
            {
                return Refuse(refusal.Message);
            }

            using (var reader = RegistryHome.OpenReader(registryPath))
            {
                var handoffs = HandoffStorageReclaimAuthority.Preview(
                    reader.Connection, new HandoffStorageReclaimRequest(resolvedWork, false, candidates));
                items.AddRange(handoffs.Items.Select(FromAuthority));
                var pinned = PinnedBinaryReclaimAuthority.Preview(
                    reader.Connection, new PinnedBinaryReclaimRequest(resolvedWork, Environment.MachineName, false));
                items.AddRange(pinned.Items.Select(FromAuthority));
                if (resolvedDeploy is not null)
                {
                    try
                    {
                        items.AddRange(DeployInventory.Inspect(reader.Connection, DeployRequest(workspace, resolvedDeploy)).Select(FromInventory));
                    }
                    catch (Exception failure) when (failure is DeployInventoryRefusal or StorageRefusal)
                    {
                        return Refuse(failure.Message);
                    }
                }
            }

            var removableBytes = items
                .Where(item => item.Decision is "removable" or "removable_if_registry_exclusive")
                .Sum(item => item.Bytes);

            long removedBytes = 0;
            if (wantApply)
            {
                if (!wantScratch && !wantDeploy)
                {
                    // The recorded sentence stays word-for-word when no deploy root is in play.
                    return Refuse(resolvedDeploy is null
                        ? "reclaim-storage --apply requires --scratch"
                        : "reclaim-storage --apply requires --scratch or --deploy");
                }
                if (wantDeploy && resolvedDeploy is null)
                {
                    return Refuse("reclaim-storage --deploy requires --deploy-root");
                }

                using var writer = RegistryHome.OpenWriter(registryPath);
                if (wantDeploy)
                {
                    // CHG 2026-09-10: the authority check, the inventory walk and the
                    // unlinks are one immediate transaction, which is the rule the
                    // sibling --scratch path already states: "the filesystem unlink
                    // stays inside the same immediate transaction that proves no
                    // current authority pointer exists". This branch had the same
                    // three steps on the raw connection with nothing spanning them,
                    // so a Plan could take a lease during the walk — which is real
                    // wall-clock time over a deploy root measured in tens of
                    // gigabytes — and have its source root deleted underneath it by a
                    // decision computed before it started. Re-reading narrowed that
                    // window; only the transaction closes it.
                    long deployedBytes;
                    try
                    {
                        deployedBytes = writer.WriteImmediate(connection =>
                        {
                            var current = PinnedBinaryReclaimAuthority.CurrentAuthorityPointers(connection);
                            if (current.Count != 0)
                            {
                                throw new RegistryFault(
                                    "refusing deploy cleanup while Registry Home still has current authority pointers ("
                                    + string.Join(", ", current) + "); finish, cancel or reconcile those objects first");
                            }

                            long bytes = 0;
                            foreach (var item in DeployInventory.Inspect(connection, DeployRequest(workspace, resolvedDeploy!)))
                            {
                                if (item.Decision != DeployInventory.Removable) continue;
                                if (File.ResolveLinkTarget(item.Path, returnFinalTarget: false) is not null) continue;
                                if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
                                else if (File.Exists(item.Path)) File.Delete(item.Path);
                                else continue;
                                bytes += item.Bytes;
                            }

                            return bytes;
                        });
                    }
                    catch (RegistryFault fault)
                    {
                        // The transaction rolled back, so the pointer check refused
                        // before any byte was unlinked.
                        return Refuse(fault.Message);
                    }

                    removedBytes += deployedBytes;
                }
                if (wantScratch)
                {
                    var pinned = PinnedBinaryReclaimAuthority.Reclaim(
                        writer, new PinnedBinaryReclaimRequest(resolvedWork, Environment.MachineName, true));
                    if (!pinned.TryGetValue(out var pinnedReport))
                    {
                        pinned.TryGetRefusal(out var refusal);
                        return Refuse($"cannot reclaim pinned binaries: {refusal.Detail}");
                    }

                    removedBytes += pinnedReport.RemovedBytes;

                    // Rust re-reads the filesystem before applying, so a
                    // directory that changed between preview and apply is
                    // re-judged rather than trusted from the earlier pass.
                    var (_, fresh) = InspectHandoffFilesystem(resolvedWork);
                    var handoffs = HandoffStorageReclaimAuthority.Reclaim(
                        writer, new HandoffStorageReclaimRequest(resolvedWork, true, fresh));
                    if (!handoffs.TryGetValue(out var handoffReport))
                    {
                        handoffs.TryGetRefusal(out var refusal);
                        return Refuse($"cannot reclaim handoff storage: {refusal.Detail}");
                    }

                    removedBytes += handoffReport.RemovedBytes;
                }
            }

            if (parsed.GetValue(format) == "json")
            {
                Console.Out.WriteLine(ReportJson.SortedPretty(new
                {
                    schema = "fern.storage_reclaim.v1",
                    status = wantApply ? "applied" : "preview",
                    registry = registryPath,
                    deploy_root = resolvedDeploy,
                    applied = wantApply,
                    removable_bytes = removableBytes,
                    removed_bytes = removedBytes,
                    items = items.Select(item => new
                    {
                        path = item.Path,
                        bytes = item.Bytes,
                        decision = item.Decision,
                        reason = item.Reason,
                    }),
                }, Report));
                return 0;
            }

            // --- Rust text renderer, ported verbatim ------------------------
            Console.Out.WriteLine($"Fern storage reclaim: {(wantApply ? "applied" : "preview")}");
            Console.Out.WriteLine($"Registry Home: {registryPath}");
            foreach (var item in items)
            {
                Console.Out.WriteLine(
                    $"{item.Decision} · {item.Bytes / (1024.0 * 1024.0):F1} MiB · {item.Path}\n  {item.Reason}");
            }

            if (!wantScratch && !wantDeploy)
            {
                Console.Out.WriteLine(resolvedDeploy is null
                    ? "\nRead-only inventory. Add --scratch to select handoff storage; add --apply to remove it."
                    : "\nRead-only inventory. Add --scratch to select handoff storage, --deploy to select the deploy/ paths called removable; add --apply to remove it.");
            }
            else if (!wantApply)
            {
                Console.Out.WriteLine("\nPreview only. Repeat with --apply after reviewing the exact paths above.");
            }
            else
            {
                Console.Out.WriteLine($"\nRemoved {removedBytes / (1024.0 * 1024.0):F1} MiB.");
            }

            return 0;
        });
        return command;
    }

    static ReclaimItem FromInventory(DeployInventory.Item item) => new(item.Path, item.Bytes, item.Decision, item.Reason);

    /// <summary>The selectors sit at fixed places of the workspace; only the deploy root is chosen.</summary>
    static DeployInventory.Request DeployRequest(string workspace, string deployRoot) =>
        new(
            deployRoot,
            WorktreeControlPaths.Profiles(workspace), // CHG 2026-09-26: config/profiles/ below the workspace
            Path.Combine(workspace, "work.toml"),
            Path.Combine(
                Environment.GetEnvironmentVariable("HOME") ?? throw new StorageRefusal("HOME is unset; the systemd user directory cannot be located"),
                ".config", "systemd", "user"));

    static ReclaimItem FromAuthority(PinnedBinaryReclaimItem item) =>
        item.Decision is "keep" or "removable"
            ? new ReclaimItem(item.Path, item.Bytes, item.Decision, item.Reason)
            : throw new StorageRefusal("C# pinned-binary reclaim returned an unknown decision");

    /// <summary>A refusal carried as an exception only across these private
    /// helpers; every public path turns it back into the operator's one line.</summary>
    sealed class StorageRefusal(string message) : Exception(message);

    /// <summary>
    /// Rust `resolve_inside_workspace`. The point is not tidiness: this is what
    /// stops `--work-root /` or a symlink from becoming a deletion target.
    /// </summary>
    /// CHG 2026-09-27: the target's final name is no longer required to be a
    /// fixed word (<c>deploy</c>, <c>fern-run-worker</c>); the layout moved
    /// these under runtime/ and names are the owner's to choose. What still
    /// stops a broad or linked target is below: inside the workspace, and not
    /// a symbolic link.
    static string ResolveInsideWorkspace(string workspace, string requested)
    {
        var joined = Path.IsPathRooted(requested) ? requested : Path.Combine(workspace, requested);
        var parent = Path.GetDirectoryName(joined)
            ?? throw new StorageRefusal($"{joined} has no parent directory");
        parent = Path.GetFullPath(parent);
        var name = Path.GetFileName(joined);
        if (string.IsNullOrEmpty(name))
        {
            throw new StorageRefusal($"{joined} has no portable final name");
        }

        if (!parent.StartsWith(workspace, StringComparison.Ordinal))
        {
            throw new StorageRefusal(
                $"refusing broad storage target {joined}: expected a directory inside {workspace}");
        }

        var resolved = Path.Combine(parent, name);
        // CHG 2026-08-29: ResolveLinkTarget throws FileNotFound on a path that
        // does not exist, and target/ no longer exists on this machine (the
        // Cargo workspace was retired 2026-08-28); an absent path is not a
        // link, and the caller decides what absence means for its item.
        if ((File.Exists(resolved) || Directory.Exists(resolved))
            && File.ResolveLinkTarget(resolved, returnFinalTarget: false) is not null)
        {
            throw new StorageRefusal($"refusing symbolic-link storage target {resolved}");
        }

        return resolved;
    }

    /// <summary>Rust `inspect_handoff_filesystem`: directories sorted by name,
    /// each either a removal candidate with its typed evidence or a kept row
    /// saying why it stays.</summary>
    static (List<ReclaimItem> Kept, List<HandoffDirectoryEvidence> Candidates) InspectHandoffFilesystem(string workRoot)
    {
        var kept = new List<ReclaimItem>();
        var candidates = new List<HandoffDirectoryEvidence>();
        if (!Directory.Exists(workRoot))
        {
            return (kept, candidates);
        }

        foreach (var directory in Directory.EnumerateDirectories(workRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var verdict = InspectOneHandoff(directory);
            if (verdict.Candidate is { } evidence)
            {
                candidates.Add(evidence);
            }
            else
            {
                kept.Add(verdict.Kept!);
            }
        }

        return (kept, candidates);
    }

    readonly record struct HandoffVerdict(HandoffDirectoryEvidence? Candidate, ReclaimItem? Kept);

    /// <summary>
    /// Rust `inspect_one_handoff_filesystem`. A directory is only a candidate
    /// when EVERY attempt under it carries all three typed files and its commit
    /// validates against them. Every "kept" reason below is Rust's wording.
    /// </summary>
    static HandoffVerdict InspectOneHandoff(string directory)
    {
        var bytes = DirectoryBytes(directory);
        ReclaimItem Keep(string reason) => new(directory, bytes, "keep", reason);

        var attemptsRoot = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsRoot))
        {
            return new HandoffVerdict(null, Keep("no typed Attempt directory; legacy or incomplete handoff"));
        }

        var entries = Directory.EnumerateFileSystemEntries(attemptsRoot).ToArray();
        if (entries.Length == 0)
        {
            return new HandoffVerdict(null, Keep("typed handoff has no Attempt"));
        }

        var attempts = new List<HandoffAttemptEvidence>();
        foreach (var entry in entries)
        {
            if (!Directory.Exists(entry))
            {
                return new HandoffVerdict(null, Keep("Attempt root contains an unexpected non-directory entry"));
            }

            var committed = Path.Combine(entry, "committed");
            var requestPath = Path.Combine(committed, "worker_request.v1.json");
            var resultPath = Path.Combine(committed, "worker_result_report.v1.json");
            var commitPath = Path.Combine(committed, "attempt_artifact_commit.v1.json");
            if (!File.Exists(requestPath) || !File.Exists(resultPath) || !File.Exists(commitPath))
            {
                return new HandoffVerdict(
                    null, Keep("Attempt has no complete typed commit and may still be reconcilable"));
            }

            var options = AttemptArtifactDigest.Canonical;
            var request = JsonSerializer.Deserialize<WorkerRequestV1>(File.ReadAllText(requestPath), options)!;
            var result = JsonSerializer.Deserialize<WorkerResultReportV1>(File.ReadAllText(resultPath), options)!;
            var commit = JsonSerializer.Deserialize<AttemptArtifactCommitV1>(File.ReadAllText(commitPath), options)!;
            if (commit.ValidateAgainst(request, result) is ContractCheck.Refused refusal)
            {
                throw new StorageRefusal(
                    $"typed commit {commitPath} is invalid: {refusal.Field}: {refusal.Detail}");
            }

            attempts.Add(new HandoffAttemptEvidence(
                request.AttemptId,
                request.RunHash,
                commit.Outputs.Select(output => output.Artifact.ByteDigest.Value).ToArray()));
        }

        return new HandoffVerdict(new HandoffDirectoryEvidence(directory, bytes, attempts), null);
    }

    /// <summary>Rust `directory_bytes`: a symlink counts as nothing, so a link
    /// into a large tree can never inflate what looks reclaimable.</summary>
    static long DirectoryBytes(string path)
    {
        if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
        {
            return 0;
        }

        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            total += DirectoryBytes(entry);
        }

        return total;
    }
}
