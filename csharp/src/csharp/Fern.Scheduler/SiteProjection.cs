using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// What a registered Site is doing, from durable facts only: its declaration,
/// the office (who supervises it, or that nobody confirmed does), the last
/// few supervisor lifetimes (a crash loop is several retryable endings in a
/// short time), the requests standing over it, the sources it holds, its
/// cards and who holds them, and the attempts open on it.
/// CHG 2026-09-11 night (slice 7 of the cell port, row 37): the retired
/// SchedulerReadiness and SchedulerSystemdDiagnostics asked the same
/// questions of the service's receipt, systemctl and journalctl; here every
/// answer is a row the supervisor itself wrote.
/// </summary>
public static class SiteProjection
{
    /// <summary>How many supervisor lifetimes the history shows.</summary>
    public const int HistoryLength = 5;

    /// <summary>Retryable endings within this window are a crash loop.</summary>
    public static readonly TimeSpan CrashLoopWindow = TimeSpan.FromHours(1);

    /// <summary>Retryable endings within the window that count as a crash loop.</summary>
    public const int CrashLoopCount = 3;

    public static string Render(SqliteConnection connection, RegistryOperations.Site site)
    {
        var text = new StringBuilder();
        var offer = SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson);
        var cards = RegistryOperations.DevicesIn(site.CapabilitiesJson);
        text.Append(CultureInfo.InvariantCulture,
            $"Site {site.Name}  executor={Capability(site, "executor") ?? "local"}  device={offer.Device}  slots={offer.Slots}"
            + $"  cards={(cards.Count == 0 ? "none declared" : string.Join(",", cards))}"
            + $"  admits={(offer.AdmittedModels.Count == 0 ? "nothing" : string.Join(",", offer.AdmittedModels))}\n");

        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var supervisor = RegistryOperations.ReadSupervisor(connection, site.SiteId);
        // CHG 2026-09-26 (slice 3 of the command-line design): the four office
        // states are named once, in OfficeState, which the payload reads too.
        var office = OfficeState(supervisor, now);
        if (supervisor is null)
        {
            text.Append("  office: no supervisor has ever held it (install-supervisor-service, or run-study-cells --serve here)\n");
        }
        else if (office == OfficeHeld)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  office: supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} holds it, lease until {supervisor.LeaseUntil}, binary {supervisor.BinarySha256}\n");
        }
        else if (office == OfficeUnconfirmed)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  office: UNCONFIRMED, supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} stopped renewing at {supervisor.LeaseUntil} without ending; a successor may take the Site\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  office: empty, supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} ended {supervisor.EndedAt} ({supervisor.Outcome}, {supervisor.Disposition}, exit {supervisor.ExitCode})\n");
        }
        AppendHistory(connection, site.SiteId, text);

        foreach (var kind in new[] { RegistryOperations.RequestHold, RegistryOperations.RequestPause, RegistryOperations.RequestStop })
        {
            foreach (var request in RegistryOperations.PendingRequests(connection, kind, RegistryOperations.ScopeAll, RegistryOperations.SiteScope(site.SiteId)))
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"  {kind} by {request.RequestedBy} over {request.Scope}: {request.Reason} (request {request.RequestId})\n");
            }
        }

        // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
        // slice 4): a Site no longer holds installed sources or carried
        // compilers; what it declares is where its cells take their code from.
        var code = RegistryOperations.CodeOf(site.CapabilitiesJson);
        text.Append(code.TryGetValue(out var declared)
            ? $"  code: {declared.From} of {declared.Repository}\n"
            : "  code: not declared (register-site with code_from working-tree or commit)\n");

        foreach (var card in cards)
        {
            var live = DeviceGrantAuthority.LiveForDevice(connection, card);
            text.Append(live.Count == 0
                ? $"  card {card}: free\n"
                : $"  card {card}: held by {string.Join(", ", live.Select(grant => $"{grant.OwnerKind} {grant.OwnerAttemptId} (generation {grant.GrantGeneration})"))}\n");
        }

        AppendAttempts(connection, site.SiteId, text);
        return text.ToString();
    }

    static void AppendHistory(SqliteConnection connection, long siteId, StringBuilder text)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT epoch, host, pid, started_at, ended_at, outcome, disposition, exit_code
            FROM supervisors WHERE site_id = $site ORDER BY epoch DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$site", siteId);
        command.Parameters.AddWithValue("$limit", HistoryLength);
        var retryable = 0;
        var since = DateTimeOffset.UtcNow - CrashLoopWindow;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ended = reader.IsDBNull(4) ? null : reader.GetString(4);
            var disposition = reader.IsDBNull(6) ? null : reader.GetString(6);
            text.Append(
                $"    epoch {reader.GetInt64(0)}  {reader.GetString(1)}/{reader.GetInt64(2)}  started {reader.GetString(3)}"
                + (ended is null ? "  not ended" : $"  ended {ended}  {reader.GetString(5)} ({disposition}, exit {reader.GetInt64(7)})") + "\n");
            if (disposition == RegistryOperations.DispositionRetryable && ended is not null
                && DateTimeOffset.TryParse(ended, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at >= since)
            {
                retryable++;
            }
        }
        if (retryable >= CrashLoopCount)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  CRASH LOOP: {retryable} retryable endings within {CrashLoopWindow.TotalMinutes:F0} min; the unit restarts a supervisor that keeps dying, read its journal\n");
        }
    }

    static void AppendAttempts(SqliteConnection connection, long siteId, StringBuilder text)
    {
        // CHG 2026-09-26 (slice 3 of the command-line design): the query moved to
        // OpenAttempts, which the payload's "running" reads too; the lines are unchanged.
        var open = OpenAttempts(connection, siteId);
        foreach (var attempt in open)
        {
            text.Append(
                $"  attempt {attempt.AttemptId} epoch {attempt.Epoch}: cell {attempt.Cell} of study {attempt.Study}"
                + $"  {attempt.State} since {attempt.StartedAt}, last progress {attempt.LastProgressAt}"
                + (attempt.Remote.Length == 0 ? "" : $", held by {attempt.Remote}") + "\n");
        }
        if (open.Count == 0)
        {
            text.Append("  attempts: none open\n");
        }
    }

    /// <summary>
    /// One attempt open on a Site: its id and epoch, the cell and study it is
    /// for, its state, when it started, its last progress (or when it started
    /// when it reported none), and the process its beat named, or empty.
    /// </summary>
    public sealed record OpenAttempt(
        long AttemptId, long Epoch, string Cell, string Study, string State, string StartedAt, string LastProgressAt, string Remote);

    /// <summary>The attempts open on one Site, oldest first. Read-only.</summary>
    public static IReadOnlyList<OpenAttempt> OpenAttempts(SqliteConnection connection, long siteId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.name, p.name, a.id, a.fencing_epoch, a.state, a.started_at, COALESCE(a.last_progress_at, a.started_at), COALESCE(a.remote_execution_id, '')
            FROM attempts a JOIN cells c ON c.id = a.cell_id JOIN studies p ON p.id = c.study_id
            WHERE a.site_id = $site AND a.state IN ('opened', 'running')
            ORDER BY a.id
            """;
        command.Parameters.AddWithValue("$site", siteId);
        using var reader = command.ExecuteReader();
        var open = new List<OpenAttempt>();
        while (reader.Read())
        {
            open.Add(new OpenAttempt(
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(0), reader.GetString(1), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }
        return open;
    }

    /// <summary>The office word of a Site whose supervisor has held it and still does.</summary>
    public const string OfficeHeld = "held";

    /// <summary>The office word of a Site whose supervisor stopped renewing without ending: it died without saying how.</summary>
    public const string OfficeUnconfirmed = "unconfirmed";

    /// <summary>The office word of a Site whose last supervisor ended.</summary>
    public const string OfficeEnded = "ended";

    /// <summary>The office word of a Site no supervisor has ever held.</summary>
    public const string OfficeNever = "never";

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// the office of a Site in one word, from its latest supervisor at
    /// <paramref name="now"/>: held, unconfirmed, ended, or never. The text's
    /// office line and the payload's <c>office.state</c> read it.
    /// </summary>
    public static string OfficeState(RegistryOperations.SupervisorView? supervisor, string now) =>
        supervisor is null ? OfficeNever
        : supervisor.HoldsOfficeAt(now) ? OfficeHeld
        : supervisor.IsUnconfirmedAt(now) ? OfficeUnconfirmed
        : OfficeEnded;

    /// <summary>
    /// CHG 2026-09-26 (slice 5 of the command-line design): the holds standing
    /// over the Site's own scope, oldest first. disable-site places one and
    /// enable-site releases them all, so a Site is enabled exactly when there
    /// is none.
    /// </summary>
    public static IReadOnlyList<RegistryOperations.OperatorRequest> DisablingHolds(SqliteConnection connection, long siteId) =>
        RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, RegistryOperations.SiteScope(siteId));

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of the command-line design): one Site as the
    /// Home answers <c>GET /v1/sites/{name}</c>: what it declares and whether it is
    /// enabled, its office, the attempts open on it, and where its cells take
    /// their code from. <c>office.since</c> is when the office state began:
    /// the supervisor's start while it holds the office, its lease end once it
    /// is unconfirmed (the text's "unconfirmed since"), its end once it ended.
    /// <c>running[].attempt</c> is the attempt's id, as the text prints it.
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the sources
    /// and compilers a Site held are gone from the answer; <c>code</c> says where
    /// its cells take their code from (null when the Site declares nothing
    /// readable).
    /// </summary>
    public static string SitePayload(SqliteConnection connection, RegistryOperations.Site site)
    {
        var offer = SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson);
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var supervisor = RegistryOperations.ReadSupervisor(connection, site.SiteId);
        var state = OfficeState(supervisor, now);
        var running = new JsonArray();
        foreach (var attempt in OpenAttempts(connection, site.SiteId))
        {
            running.Add(new JsonObject
            {
                ["study"] = attempt.Study,
                ["cell"] = attempt.Cell,
                ["attempt"] = attempt.AttemptId,
                ["epoch"] = attempt.Epoch,
                ["started_at"] = attempt.StartedAt,
                ["last_progress_at"] = attempt.LastProgressAt,
            });
        }
        var code = RegistryOperations.CodeOf(site.CapabilitiesJson).TryGetValue(out var declared)
            ? new JsonObject { ["from"] = declared.From, ["repository"] = declared.Repository }
            : null;
        return PayloadJson.Write(new JsonObject
        {
            ["site"] = new JsonObject
            {
                ["name"] = site.Name,
                ["executor"] = Capability(site, "executor") ?? "local",
                ["device"] = offer.Device,
                ["slots"] = offer.Slots,
                ["cards"] = new JsonArray([.. RegistryOperations.DevicesIn(site.CapabilitiesJson).Select(card => (JsonNode?)card)]),
                ["admits"] = new JsonArray([.. offer.AdmittedModels.Select(model => (JsonNode?)model)]),
                ["enabled"] = DisablingHolds(connection, site.SiteId).Count == 0,
            },
            ["office"] = new JsonObject
            {
                ["state"] = state,
                ["supervisor"] = supervisor is null ? null : new JsonObject
                {
                    ["host"] = supervisor.Host,
                    ["pid"] = supervisor.Pid,
                    ["epoch"] = supervisor.Epoch,
                },
                ["lease_until"] = supervisor?.LeaseUntil,
                ["since"] = state switch
                {
                    OfficeHeld => supervisor!.StartedAt,
                    OfficeUnconfirmed => supervisor!.LeaseUntil,
                    OfficeEnded => supervisor!.EndedAt,
                    _ => null,
                },
            },
            ["running"] = running,
            ["code"] = code,
        });
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of the command-line design): every registered
    /// Site as the Home answers <c>GET /v1/sites</c>, in registration order, with its office
    /// word and how many attempts are open on it.
    /// </summary>
    public static string SitesPayload(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var sites = new JsonArray();
        foreach (var site in RegistryOperations.ListSites(connection))
        {
            var offer = SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson);
            sites.Add(new JsonObject
            {
                ["name"] = site.Name,
                ["executor"] = Capability(site, "executor") ?? "local",
                ["device"] = offer.Device,
                ["enabled"] = DisablingHolds(connection, site.SiteId).Count == 0,
                ["office_state"] = OfficeState(RegistryOperations.ReadSupervisor(connection, site.SiteId), now),
                ["running"] = OpenAttempts(connection, site.SiteId).Count,
            });
        }
        return PayloadJson.Write(new JsonObject { ["sites"] = sites });
    }

    static string? Capability(RegistryOperations.Site site, string key)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(site.CapabilitiesJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
