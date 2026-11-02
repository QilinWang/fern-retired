namespace Fern.Site;

/// <summary>
/// The fixed names inside a Stage package, its runtime, and its return. Home
/// and the Site agree on these strings; nothing here is configurable, because a
/// configurable path is a path the two sides can disagree about.
/// </summary>
public static class SitePaths
{
    /// <summary>
    /// CHG 2026-08-29 (v1 → v2, textbook case 10): a package no longer carries the
    /// Site's own binary nor the frozen Python source. Both are installed on
    /// the Site ONCE, content-addressed, and the instructions name what the
    /// Site must find there: the digest of the binary Home verified before the
    /// spawn, the installed source root, and every source file with its digest
    /// so the Site checks the bytes it will import. "Ship a binary" means
    /// install once (owner rule); the v1 shape moved ~40 MB per Stage attempt.
    /// </summary>
    public const string InstructionsSchema = "fern.remote_attempt_instructions.v2";
    public const string InstructionsFile = "remote_attempt_instructions.v1.json";
    public const string ReturnReportSchema = "fern.remote_attempt_report.v1";
    public const string ReturnReportFile = "remote_attempt_report.v1.json";
    public const string StageRequestFile = "request/stage_runner_request.v1.json";
    public const string StageReportFile = "report/stage_runner_report.v1.json";
    // CHG 2026-08-29 (Program 18, Site-C): one phase of a cell on the Site's
    // resident worker. The phase file rides in the package and names only
    // science-wired facts (which cell, which ordinal, which earlier phases feed
    // which ports); machine paths arrive as command-line flags, never in a
    // digested file. The Site receipt is the Site binary's own reading of the
    // sealed outputs, returned beside the runner report while the bytes stay.
    public const string PhaseFile = "phase/cell_phase.v1.json";
    public const string PhaseSchema = "fern.cell_phase.v1";
    public const string SitePhaseReceiptFile = "report/site_phase_receipt.v1.json";
    public const string SitePhaseReceiptSchema = "fern.site_phase_receipt.v1";
    /// <summary>
    /// CHG 2026-08-29 (observation layer, gate 1): the bounded end of the Stage
    /// runner's merged stdout and stderr, written beside the report on EVERY
    /// attempt, success or failure, and returned with it. Before this only a
    /// failing attempt's tail travelled, glued into the refusal text; a Stage
    /// that exited 1 after printing its reason to stdout reached the operator
    /// as "exited 1" and nothing else.
    /// </summary>
    public const string RunnerOutputTailFile = "report/runner_output.tail";
    public const string PackagedRunnerFile = "implementation/fern-stage-runner";
    public const string WorkerRequestFile = "worker_request.v1.json";

    /// <summary>The most failure output the Site will ever hold in memory or report.</summary>
    public const int MaximumCommandOutputDetailBytes = 16 * 1024;

    /// <summary>The longest progress line the Site will assemble before dropping it.</summary>
    public const int MaximumStageProgressLineBytes = 4 * 1024;

    /// <summary>The only prefix on runner stdout the Site forwards. Everything else is trainer prose.</summary>
    public const string StageProgressPrefix = "FERN_TRAIN_PROGRESS ";
}

/// <summary>One file inside a package or a return, with the identity Home declared for it.</summary>
public sealed record AttemptFile(string RelativePath, long ByteCount, string ByteDigest);

/// <summary>
/// What Home issued: which Attempt this is, and the exact file set with the
/// bytes each file must have. Everything else about the Stage is opaque here.
/// </summary>
public sealed record AttemptInstructions(
    string Schema,
    string MatrixId,
    string CoordinateId,
    string AttemptId,
    string RunHash,
    string ExecutionProfileRevision,
    string WorkerRequestContentDigest,
    IReadOnlyList<AttemptFile> Files,
    string SiteBinaryDigest,
    string ImplementationSourceRoot,
    IReadOnlyList<AttemptFile> ImplementationSourceFiles);

/// <summary>One verified package directory and the instructions it satisfies.</summary>
public sealed record StagePackage(string Root, AttemptInstructions Instructions);

/// <summary>
/// The transport identity of the runner report the Site reopens. Scientific
/// admission remains a Home responsibility after the return is imported.
/// </summary>
public sealed record StageExecutionResult(string RunnerReportContentDigest);

/// <summary>
/// What the Site publishes back: which instructions it answered, and every file
/// it is returning with that file's identity.
/// </summary>
public sealed record AttemptReturnReport(
    string Schema,
    string InstructionsDigest,
    string AttemptId,
    // The wire name is retained for existing return readers. It now binds the
    // runner report bytes; only Home may mint a scientific result digest.
    string RunnerReportContentDigest,
    IReadOnlyList<AttemptFile> Files);

/// <summary>One published return directory and the report that describes it.</summary>
public sealed record StageReturnPackage(string Root, AttemptReturnReport Report);
