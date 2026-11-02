using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// What is written against a cell that ran and produced no result, and how it
/// is read back.
///
/// It is one small file because three places share the document: the worker
/// writes it, the run loop prints one line of it, and the monitor prints it
/// beside the attempt's state. Before this existed the reason reached the
/// Registry as an opaque string and no reader lifted it out, so on 2026-09-11
/// a study refused thirteen cells with "unknown dataset 'pointmaze.physical_state';
/// datasets: []" and every human-facing surface said only "failed".
///
/// The fields:
///   detail          — one sentence, the thing a person reads first
///   deterministic   — true when the same request would be refused the same way
///   outcome_kind    — what the runtime called the ending, when it wrote a report
///   code, message   — the runtime's own words, kept apart from this side's
///   stderr_tail     — the last of the process's stderr, when there was no report
/// </summary>
public static class CellFailureDetail
{
    /// <summary>The field a reader prints, and the field that says whether to expect the same answer again.</summary>
    public const string SentenceField = "detail";
    public const string DeterministicField = "deterministic";

    /// <summary>
    /// The field that says this failure belongs to the MACHINE rather than to
    /// the cell: the card was missing, an input the card names is not on this
    /// host, the environment refused. Ported from the retired `OtherProfile`
    /// retry class, whose whole meaning was "try this again, but not here".
    /// Without it a machine-specific fault spends a cell's whole budget on the
    /// one machine that cannot run it, and the cell then waits for a person
    /// while three other machines sit idle.
    /// </summary>
    public const string SiteSpecificField = "site_specific";

    /// <summary>
    /// CHG 2026-09-11 night (slice 2 of the cell port, design note
    /// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
    /// rows 8 and 10). The retired StageAttemptAuthority had four retry classes:
    /// Never, SameProfile, OtherProfile, RequiresHuman. They come back under
    /// plain names as one field of the failure record, which is where a class
    /// belongs: it is a fact about how the attempt ended, and the claim reads
    /// the last failure's record. The two booleans above stay for their readers;
    /// the class is derived from them when a writer does not name one.
    ///   deterministic  the same request would fail the same way; never retried
    ///   transient      retry anywhere, while the budget lasts
    ///   site           retry, but not on the Site it failed on
    ///   human          a person decides; retry-cell records that decision
    /// `before_execution` says the failure crossed no execution boundary (a
    /// refusal before a process started, a worker that never began), which is
    /// never a loss and never semantic progress; it is a fact beside the class,
    /// not a fifth class.
    /// </summary>
    public const string FailureClassField = "failure_class";
    public const string BeforeExecutionField = "before_execution";
    public const string ClassDeterministic = "deterministic";
    public const string ClassTransient = "transient";
    public const string ClassSite = "site";
    public const string ClassHuman = "human";

    /// <summary>
    /// Write the document. Everything after <paramref name="failureClass"/> is
    /// present only when known.
    ///
    /// CHG 2026-09-14: the class is stated, not derived. It used to default to
    /// whatever two booleans implied -- deterministic, else site-specific, else
    /// transient -- which is right for the paths that existed and cannot be
    /// right for a path that means something else. A human decision reads as
    /// transient under that rule, and nothing says so. Now every failure site
    /// answers the question, and a new one cannot be added without answering
    /// it, because the compiler asks.
    /// </summary>
    public static string Write(
        string sentence,
        bool deterministic,
        string failureClass,
        string? outcomeKind = null,
        string? code = null,
        string? runtimeMessage = null,
        string? stderrTail = null,
        bool siteSpecific = false,
        bool beforeExecution = false,
        bool? compilerDiffers = null)
    {
        if (failureClass is not (ClassDeterministic or ClassTransient or ClassSite or ClassHuman))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureClass),
                $"a cell failure is one of {ClassDeterministic}, {ClassTransient}, {ClassSite} or {ClassHuman}; got \"{failureClass}\"");
        }
        var detail = new JsonObject
        {
            [SentenceField] = sentence,
            [DeterministicField] = deterministic,
            [SiteSpecificField] = siteSpecific,
            [FailureClassField] = failureClass,
            [BeforeExecutionField] = beforeExecution,
        };
        if (outcomeKind is not null) detail["outcome_kind"] = outcomeKind;
        if (code is not null) detail["code"] = code;
        if (runtimeMessage is not null) detail["message"] = runtimeMessage;
        if (!string.IsNullOrEmpty(stderrTail)) detail["stderr_tail"] = stderrTail;
        // CHG 2026-09-25 (slice M1, the reader's review): whether the Site's
        // compiler was found to differ from the one the study records; absent
        // when nobody could tell.
        if (compilerDiffers is { } differs) detail["compiler_differs"] = differs;
        return detail.ToJsonString();
    }

    /// <summary>The class of a failure record; older records without the field are read through their booleans.</summary>
    public static string ClassOf(string detailJson)
    {
        try
        {
            using var document = JsonDocument.Parse(detailJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ClassTransient;
            if (document.RootElement.TryGetProperty(FailureClassField, out var named) && named.ValueKind == JsonValueKind.String)
                return named.GetString() ?? ClassTransient;
            if (document.RootElement.TryGetProperty(DeterministicField, out var deterministic) && deterministic.ValueKind == JsonValueKind.True)
                return ClassDeterministic;
            if (document.RootElement.TryGetProperty(SiteSpecificField, out var site) && site.ValueKind == JsonValueKind.True)
                return ClassSite;
            return ClassTransient;
        }
        catch (JsonException)
        {
            return ClassTransient;
        }
    }

    /// <summary>
    /// The sentence inside the document, or the document itself when it is not
    /// one of ours. A reader never gets nothing: an unreadable detail is shown
    /// as it was stored rather than replaced by silence.
    /// </summary>
    // CHG 2026-09-25: the body moved verbatim, with its comment, to
    // RegistryOperations.FailureSentence, so the claim's burst signature (slice
    // M3) reads a failure's sentence the way every monitor and the Gleam round do.
    public static string Sentence(string detailJson) => RegistryOperations.FailureSentence(detailJson);

    /// <summary>Whether the document says this refusal would repeat unchanged.</summary>
    public static bool IsDeterministic(string detailJson) => Flag(detailJson, DeterministicField);

    /// <summary>Whether the document says the failure belongs to the machine, not to the cell.</summary>
    public static bool IsSiteSpecific(string detailJson) => Flag(detailJson, SiteSpecificField);

    static bool Flag(string detailJson, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(detailJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
