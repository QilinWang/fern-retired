using System.Globalization;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// Make one cell of a registered study claimable again, and record who decided
/// that and why.
///
/// WHY THIS EXISTS. Two things could put a cell beyond reach with no way back.
/// An attempt left <c>opened</c> or <c>running</c> by a supervisor that was
/// killed stays that way forever — `ListRunnableCells` excludes a cell with an
/// open attempt, and the retired reconciler that marked expired leases lost has
/// no successor. And a cell that failed for a reason a person then fixed — a
/// dataset installed, a card corrected — is claimable again only by re-running
/// the command, which records nothing about the decision to retry.
///
/// So this is one verb doing one thing: it closes an open attempt as
/// <c>lost</c>, or records the decision against the last attempt when there is
/// nothing to close, and in both cases the reason, the person and the previous
/// state are written where `monitor-study` already reads them. It never touches
/// an earlier attempt's result and it never removes a row: the cell's history is
/// what it was, with one more record on the end.
///
/// What it refuses, by name: a cell whose result was accepted (a cell accepts
/// one result, and a second run would have nowhere to go), a study that is
/// cancelled (retrying a cell of it would be claimed by nothing), a cell
/// nobody has attempted (it is already claimable, and saying so is more useful
/// than a receipt for a no-op), and — the one that matters most — an open
/// attempt that still looks ALIVE.
///
/// CHG 2026-09-11, after review: the first version declared any open attempt
/// lost on the operator's word alone. That is how a cell training right now gets
/// taken away from the process training it: the attempt is marked lost, the cell
/// becomes runnable, a second supervisor claims it, and two processes write the
/// same cell's artifacts while the first one's fenced writes are refused at the
/// end of a six-hour run. The retired lease reconciler had an expiry check and
/// it did not come across.
///
/// What this side can actually see is thin, and the thinness is worth stating:
/// the one-store `attempts` table has no process identity. `runner_pid` and
/// `runner_host` exist only on the retired `stage_attempts` table, so there is
/// nothing to probe and no lease to expire — the only liveness signal is
/// `last_progress_at`, which `RenewAttempt` and `RecordEvent` move. So the rule
/// is: an open attempt is only declared lost when it has been silent longer than
/// a stated bound, or when the operator types `--force` having read the
/// attempt's facts, which every answer prints whether it acts or refuses.
/// </summary>
public static class StudyCellRetry
{
    /// <summary>
    /// The event kind written when there was no open attempt to close. The
    /// store owns the spelling (CHG 2026-09-11 night, slice 2): the claim reads
    /// this record as a person's decision that lifts a failure's class and the
    /// retry window.
    /// </summary>
    public const string RetryRequested = RegistryOperations.RetryRequestedEvent;

    /// <summary>
    /// How long an open attempt must have been silent before it is declared lost
    /// without <c>--force</c>: ten of the supervisor's heartbeats.
    ///
    /// "Silent" means no supervisor heartbeat, not "no training output". The
    /// supervisor renews the attempt every
    /// <see cref="LocalCellWorker.HeartbeatInterval"/> while the trainer runs
    /// (<see cref="LocalCellWorker.Beat"/>), so ten missed beats is a supervisor
    /// that is gone rather than one that is busy. This is deliberately NOT the
    /// six-hour cell timeout: how long a cell may take and how long a supervisor
    /// may be silent are different questions, and using the first as the second
    /// meant a machine that died at minute one held its cell until evening.
    /// </summary>
    public static TimeSpan DefaultStaleAfter => RegistryOperations.StaleAfter;

    /// <summary>
    /// What a retry did, and the attempt's facts as they were read: an operator
    /// who forced one has to be able to see afterwards what they overrode.
    /// </summary>
    public sealed record Retried(
        long CellId,
        string CellName,
        long AttemptId,
        long AttemptOrdinal,
        string PreviousState,
        bool ClosedOpenAttempt,
        string SiteName,
        string StartedAt,
        string? LastProgressAt,
        string? RemoteExecutionId,
        TimeSpan? SilentFor,
        bool Forced);

    public sealed record Observation(string StudyState, CellRow? Cell, long? SilentTicks, long StaleTicks, bool Force);
    public enum RefusalReason { CancelledStudy, MissingCell, AcceptedCell, UnattemptedCell, RecentProgress, UnknownSilence }
    /// <summary>
    /// What a retry does to the cell's last attempt. CHG 2026-09-27: three named
    /// cases where two booleans (<c>CloseOpen</c>, <c>Forced</c>) could spell
    /// "not closed, yet forced", which means nothing; the design of 2026-09-24
    /// (docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-24-whether-a-cell-tries-again)
    /// asked for that state to be unrepresentable, on both sides of the wire.
    /// </summary>
    public abstract record Decision;
    /// <summary>The last attempt already ended; nothing is closed.</summary>
    public sealed record RetryAllowedAfterEnd : Decision;
    /// <summary>The open attempt has been silent past the stale bound; it is closed as lost.</summary>
    public sealed record RetryAllowedClosingSilent : Decision;
    /// <summary>A person forced it while the attempt was recent or its silence unknown; the open attempt is closed as lost on their word.</summary>
    public sealed record RetryAllowedClosingForced : Decision;
    public sealed record RetryRefused(RefusalReason Reason) : Decision;

    public static RegistryResult<Retried> Request(
        RegistryWriter writer,
        long studyId,
        string cellName,
        string requestedBy,
        string reason,
        TimeSpan? staleAfter = null,
        bool force = false,
        string? evidence = null,
        IStudyCellRetryDecision? implementedBy = null)
    {
        var bound = staleAfter ?? DefaultStaleAfter;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Refused("a retry records why it was asked for; give --reason");
        }
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            return Refused("a retry records who asked for it; give --by");
        }

        var study = RegistryOperations.ReadStudy(writer.Connection, studyId);
        if (!study.TryGetValue(out var head))
        {
            study.TryGetRefusal(out var refusal);
            return RegistryResult<Retried>.Rejected(refusal);
        }
        var cell = ReadCell(writer, studyId, cellName);
        var silentFor = cell is { LatestAttemptState: RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning }
            ? SilenceOf(cell) : null;
        var observation = new Observation(head.RequestedState, cell, silentFor?.Ticks, bound.Ticks, force);
        var decision = (implementedBy ?? CSharpStudyCellRetryDecision.Instance).Decide(observation);
        if (decision is RetryRefused denied)
        {
            return Refused(RefusalDetail(denied.Reason, head.Name, cellName, cell, silentFor, bound));
        }
        if (cell?.LatestAttemptId is not { } attemptId)
        {
            throw new InvalidOperationException("a retry permission requires an observed attempt");
        }
        var (closeOpen, forced) = decision switch
        {
            RetryAllowedAfterEnd => (false, false),
            RetryAllowedClosingSilent => (true, false),
            RetryAllowedClosingForced => (true, true),
            _ => throw new InvalidOperationException($"unknown retry decision {decision}"),
        };

        // CHG: never hold the writer while asking another process. Under the
        // writer, require the same facts the decision observed before writing.
        return writer.WriteImmediate(connection =>
        {
            if (!RegistryOperations.ReadStudy(connection, studyId).TryGetValue(out var current)
                || current.RequestedState != head.RequestedState || ReadCell(writer, studyId, cellName) != cell)
            {
                return Refused($"cell {cellName} or its study changed while its retry was being decided; read the current facts and ask again");
            }
            var detail = new JsonObject
            {
                ["detail"] = $"retry requested by {requestedBy}: {reason}",
                ["requested_by"] = requestedBy,
                ["reason"] = reason,
                ["previous_state"] = cell.LatestAttemptState,
                ["forced"] = forced,
                ["silent_for_seconds"] = silentFor is { } silent ? (long)silent.TotalSeconds : null,
                // CHG 2026-09-11 night (slice 2, row 39): the retired abandonment
                // kept an evidence receipt beside the decision; the same fact
                // rides on this record when a person gives it.
                ["evidence"] = evidence,
            }.ToJsonString();

            var written = closeOpen
                ? RegistryOperations.FailAttempt(
                    writer, attemptId, cell.LatestAttemptEpoch, RegistryOperations.AttemptLost, detail)
                : RegistryOperations.RecordEvent(
                    writer, attemptId, cell.LatestAttemptEpoch, RetryRequested, null, detail);
            if (!written.IsOk)
            {
                written.TryGetRefusal(out var refusal);
                return RegistryResult<Retried>.Rejected(refusal);
            }
            return RegistryResult<Retried>.Ok(new Retried(
                cell.CellId, cellName, attemptId, cell.LatestAttemptOrdinal, cell.LatestAttemptState, closeOpen,
                cell.SiteName, cell.StartedAt, cell.LastProgressAt, cell.RemoteExecutionId, silentFor, forced));
        });
    }

    // CHG: presentation stays here, shared by the C# and Gleam decisions.
    static string RefusalDetail(RefusalReason reason, string studyName, string cellName, CellRow? cell,
        TimeSpan? silentFor, TimeSpan bound) => reason switch
    {
        RefusalReason.CancelledStudy => $"study {studyName} is cancelled, so no cell of it will be claimed; request it to run before retrying a cell",
        RefusalReason.MissingCell => $"study {studyName} has no cell named {cellName}",
        RefusalReason.AcceptedCell => $"cell {cellName} already has accepted attempt {cell!.AcceptedAttemptId}; a cell accepts one result and is not retried",
        RefusalReason.UnattemptedCell => $"cell {cellName} has never been attempted, so it is already claimable; run-study-cells will take it",
        RefusalReason.RecentProgress =>
            $"cell {cellName} attempt {cell!.LatestAttemptOrdinal} is {cell.LatestAttemptState} on {cell.SiteName} and "
            + $"last reported {Describe(silentFor!.Value)} ago, inside the {Describe(bound)} bound: a supervisor may still be "
            + $"training it, and declaring it lost would let a second machine claim the same cell. "
            + $"{Facts(cell)} Wait for the bound, or pass --force having read those facts.",
        RefusalReason.UnknownSilence => $"cell {cellName} supervisor silence is unknown because its last progress time cannot be read. "
            + $"{Facts(cell!)} Check the supervisor, or pass --force having read those facts.",
        _ => throw new InvalidOperationException($"unknown retry refusal {reason}"),
    };

    /// <summary>
    /// How long the attempt has been silent: now minus its last progress, or
    /// minus when it started when it has reported none. Null when neither
    /// timestamp parses, which is a reason to refuse rather than to assume
    /// silence.
    /// </summary>
    static TimeSpan? SilenceOf(CellRow cell)
    {
        var last = cell.LastProgressAt ?? cell.StartedAt;
        return DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? DateTimeOffset.UtcNow - at
            : null;
    }

    /// <summary>The attempt's facts, in one sentence, printed whether the verb acts or refuses.</summary>
    public static string Facts(CellRow cell) =>
        $"attempt {cell.LatestAttemptOrdinal} on {cell.SiteName}: state {cell.LatestAttemptState}, started {cell.StartedAt}, "
        + $"last progress {cell.LastProgressAt ?? "none"}, remote execution {cell.RemoteExecutionId ?? "none"}. "
        + "This store keeps no runner pid or host for an attempt, so silence is the only liveness signal there is.";

    static string Describe(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:F0} seconds"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:F0} minutes"
        : $"{span.TotalHours:F1} hours";

    public sealed record CellRow(
        long CellId,
        long? AcceptedAttemptId,
        long? LatestAttemptId,
        long LatestAttemptOrdinal,
        long LatestAttemptEpoch,
        string LatestAttemptState,
        string SiteName,
        string StartedAt,
        string? LastProgressAt,
        string? RemoteExecutionId);

    /// <summary>The cell, its accepted attempt if it has one, and its most recent attempt.</summary>
    static CellRow? ReadCell(RegistryWriter writer, long studyId, string cellName)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.id, c.accepted_attempt_id, a.id, a.ordinal, a.fencing_epoch, a.state,
                   COALESCE(s.name, '-'), COALESCE(a.started_at, '-'), a.last_progress_at, a.remote_execution_id
            FROM cells c
            LEFT JOIN attempts a ON a.id = (
                SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
            LEFT JOIN sites s ON s.id = a.site_id
            WHERE c.study_id = $study AND c.name = $cell
            """;
        command.Parameters.AddWithValue("$study", studyId);
        command.Parameters.AddWithValue("$cell", cellName);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        return new CellRow(
            row.GetInt64(0),
            row.IsDBNull(1) ? null : row.GetInt64(1),
            row.IsDBNull(2) ? null : row.GetInt64(2),
            row.IsDBNull(3) ? 0 : row.GetInt64(3),
            row.IsDBNull(4) ? 0 : row.GetInt64(4),
            row.IsDBNull(5) ? "" : row.GetString(5),
            row.GetString(6),
            row.GetString(7),
            row.IsDBNull(8) ? null : row.GetString(8),
            row.IsDBNull(9) ? null : row.GetString(9));
    }

    /// <summary>What the operator is told, in one line, when it worked.</summary>
    public static string Sentence(Retried retried, string studyName)
    {
        var ordinal = retried.AttemptOrdinal.ToString(CultureInfo.InvariantCulture);
        var facts = $"attempt {ordinal} on {retried.SiteName}: started {retried.StartedAt}, last progress "
                  + $"{retried.LastProgressAt ?? "none"}"
                  + (retried.SilentFor is { } silent ? $" ({Describe(silent)} ago)" : "")
                  + $", remote execution {retried.RemoteExecutionId ?? "none"}";
        var what = retried.ClosedOpenAttempt
            ? $"cell {retried.CellName} of study {studyName}: attempt {ordinal} was {retried.PreviousState} and is now lost; the cell is claimable again"
            : $"cell {retried.CellName} of study {studyName}: attempt {ordinal} is {retried.PreviousState} and the cell was already claimable; the decision is recorded against it";
        return retried.Forced
            ? facts + "\n" + what + (retried.SilentFor is null
                ? " (forced: supervisor silence was unknown)"
                : " (forced: it was still inside the silence bound)")
            : facts + "\n" + what;
    }

    static RegistryResult<Retried> Refused(string detail) =>
        RegistryResult<Retried>.Rejected(new RegistryRefusal("refused", "invalid_request", detail));
}

public interface IStudyCellRetryDecision
{
    StudyCellRetry.Decision Decide(StudyCellRetry.Observation observed);
}

/// <summary>The existing operator retry rule, separated from observations and writes.</summary>
public sealed class CSharpStudyCellRetryDecision : IStudyCellRetryDecision
{
    public static readonly CSharpStudyCellRetryDecision Instance = new();

    public StudyCellRetry.Decision Decide(StudyCellRetry.Observation observed)
    {
        if (observed.StudyState == RegistryOperations.StateCancel)
            return new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.CancelledStudy);
        if (observed.Cell is not { } cell)
            return new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.MissingCell);
        if (cell.AcceptedAttemptId is not null)
            return new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.AcceptedCell);
        if (cell.LatestAttemptId is null)
            return new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.UnattemptedCell);
        if (cell.LatestAttemptState is not (RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning))
            return new StudyCellRetry.RetryAllowedAfterEnd();
        if (observed.SilentTicks is null)
            return observed.Force ? new StudyCellRetry.RetryAllowedClosingForced()
                : new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.UnknownSilence);
        if (observed.SilentTicks < observed.StaleTicks)
            return observed.Force ? new StudyCellRetry.RetryAllowedClosingForced()
                : new StudyCellRetry.RetryRefused(StudyCellRetry.RefusalReason.RecentProgress);
        return new StudyCellRetry.RetryAllowedClosingSilent();
    }
}
