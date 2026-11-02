using System.Text.Json.Nodes;

namespace Fern.Scheduler;

/// <summary>Operator retry decisions over the shared transport. The caller owns the program.</summary>
public sealed class GleamStudyCellRetryDecision(GleamProgram program) : IStudyCellRetryDecision
{
    public StudyCellRetry.Decision Decide(StudyCellRetry.Observation observed)
    {
        var reply = program.Ask(new JsonObject
        {
            ["ask"] = "retry_cell",
            ["study_state"] = observed.StudyState,
            ["cell"] = observed.Cell is { } cell ? new JsonObject
            {
                ["accepted"] = cell.AcceptedAttemptId,
                ["latest"] = cell.LatestAttemptId,
                ["state"] = cell.LatestAttemptState,
            } : null,
            ["silent_ticks"] = observed.SilentTicks,
            ["stale_ticks"] = observed.StaleTicks,
            ["force"] = observed.Force,
        }, TimeSpan.FromSeconds(30), null);
        if (!reply.IsOk) throw new GleamProgramException("the Gleam retry decision did not answer: " + reply.RefusalDetail);
        try
        {
            var answer = reply.Value["answer"] as JsonObject
                ?? throw new InvalidOperationException("missing answer");
            return answer["kind"]?.GetValue<string>() switch
            {
                // CHG 2026-09-27: `closing` says what the retry closes, where two
                // booleans (close_open, forced) could contradict each other.
                "allowed" => answer["closing"]?.GetValue<string>() switch
                {
                    "none" => new StudyCellRetry.RetryAllowedAfterEnd(),
                    "silent" => new StudyCellRetry.RetryAllowedClosingSilent(),
                    "forced" => new StudyCellRetry.RetryAllowedClosingForced(),
                    _ => throw new InvalidOperationException("unknown retry closing"),
                },
                "refused" => new StudyCellRetry.RetryRefused(answer["reason"]?.GetValue<string>() switch
                {
                    "cancelled_study" => StudyCellRetry.RefusalReason.CancelledStudy,
                    "missing_cell" => StudyCellRetry.RefusalReason.MissingCell,
                    "accepted_cell" => StudyCellRetry.RefusalReason.AcceptedCell,
                    "unattempted_cell" => StudyCellRetry.RefusalReason.UnattemptedCell,
                    "recent_progress" => StudyCellRetry.RefusalReason.RecentProgress,
                    "unknown_silence" => StudyCellRetry.RefusalReason.UnknownSilence,
                    _ => throw new InvalidOperationException("unknown retry refusal"),
                }),
                _ => throw new InvalidOperationException("unknown retry answer"),
            };
        }
        catch (InvalidOperationException error)
        {
            throw new GleamProgramException("the Gleam retry decision returned an invalid answer: " + error.Message);
        }
    }
}
