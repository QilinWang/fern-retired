using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fern.Scheduler;

/// <summary>
/// Where the controller command ran. This is declared by the caller and
/// prevents a restricted Codex process from impersonating the host.
///
/// It is explicit because the Codex sandbox has no stable environment variable
/// that distinguishes it from a host-authorized command. A missing CUDA device
/// or SSH agent inside that sandbox is therefore an observation limit, not
/// evidence that the host or Worker Site is broken.
/// </summary>
public enum ObserverContext
{
    Host,
    CodexSandbox,
}

/// <summary>Where the execution facts were actually measured.</summary>
public enum ExecutionFactsSource
{
    Host,
    CodexSandbox,
    RemoteWorkerSite,
}

/// <summary>
/// A stable category for a preflight problem. The category is intentionally
/// smaller than the full lifecycle vocabulary because this front door has not
/// started work yet: it cannot honestly report worker or artifact failures.
/// </summary>
public enum PreflightProblemCode
{
    InvalidRequest,
    InvalidProfile,
    MissingPrerequisite,
    CapacityBlocked,
    RemoteProbeRequired,
    HostCheckRequired,
    ProfileDrift,
    CatalogInconsistency,
}

public sealed record PreflightProblem(PreflightProblemCode Code, string Meaning, string FixCommand);

public sealed record StorageCheck(
    string InspectedPath,
    string Filesystem,
    double AvailableGib,
    double FreePercent,
    bool Warning,
    bool HardStop);

/// <summary>
/// One reason a declaration cannot be used, named by the field that carries it.
/// Ports ExecutionProfileIssue (crates/fern-execution-profile).
/// </summary>
public sealed record ExecutionProfileIssue(string Field, string Explanation);

/// <summary>
/// Everything the profile-validation owner needs from its command adapter.
/// The caller resolves the checkout and binary identity; validation performs
/// the profile read, machine observation, drift check, and storage check.
/// </summary>
public sealed record ProfileValidationRequest(
    string WorkspaceRoot,
    string ProfileId,
    ObserverContext Observer,
    string FernVersion);

public enum ProfileValidationStatus
{
    Ready,
    NeedsHostCheck,
    Blocked,
}

/// <summary>
/// What profile validation reports: what was asked, what was observed, and
/// every reason the answer is not simply yes.
///
/// CHG 2026-09-04: the former FrontDoorReport also carried recipe-plan
/// selection and timing fields. The recipe route retired, no current writer
/// populated them, and profile validation now returns only its own facts.
/// The retired Rust bytes remain in the historical contract fixtures.
/// </summary>
public sealed record ProfileValidationResult(
    ProfileValidationStatus Status,
    /// <summary>This is an invariant of this boundary, not merely a best-effort claim.</summary>
    bool GpuWorkStarted,
    ObserverContext Observer,
    /// <summary>None means the probe never reached an execution environment.</summary>
    ExecutionFactsSource? ExecutionFactsFrom,
    string ProfileId,
    string ProfilePath,
    /// <summary>
    /// The content identity of the validated declaration this report inspected.
    /// None means the declaration was invalid or unavailable, so it must not be
    /// used to launch work.
    /// </summary>
    string? ProfileRevision,
    string? Executor,
    string? Device,
    string? CampaignRoot,
    ObservedExecutionFingerprint? ObservedExecution,
    IReadOnlyList<ExecutionProfileDrift> ProfileDrift,
    IReadOnlyList<StorageCheck> Storage,
    IReadOnlyList<PreflightProblem> Problems,
    /// <summary>
    /// Typed failure envelopes for the paths whose failures are typed.
    /// `Problems` remains the complete human list. Carried verbatim because the
    /// probe that mints them is not this owner's.
    /// </summary>
    IReadOnlyList<JsonNode> TypedFailures,
    string NextStep)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Fern.Site.SiteFailure? ObservationFailure { get; init; }
}

/// <summary>
/// The front door's report: how it is built, closed, and printed.
///
/// Ported 2026-08-28 from fern-cli-rs/src/experiment_front_door.rs
/// (`blocked_report`, `close_report`, `finish_report`, `print_text_report`,
/// `profile_report`, `inspect_local_profile`, `record_local_execution_observation`).
/// The machine observation itself is NOT here: it belongs to
/// <see cref="ObservedExecutionProbe"/>, and this owner takes the fingerprint
/// it returns. The remote SSH probe is deliberately not ported.
/// </summary>
public static class Preflight
{
    /// <summary>The warning thresholds are `probe_storage`'s own, not the profile's declared ones.</summary>
    public const double StorageWarnFreeGib = 50.0;
    public const double StorageWarnFreePercent = 10.0;

    /// <summary>
    /// Rust's `doctor_report`: the profile read, the machine observed by the
    /// route the profile declares, drift and storage checked, nothing written.
    /// The C# operation is named for its one remaining public entrance.
    /// </summary>
    public static ProfileValidationResult ValidateProfile(ProfileValidationRequest request)
    {
        if (!ExecutionProfileV5.ValidProfileId(request.ProfileId))
        {
            // Rust's `profile_at` first branch. An id that cannot name a file
            // under profiles/ is a bad REQUEST, not a bad or absent profile,
            // and the report must not invent a path for it — "../not-a-profile"
            // would otherwise be reported as the missing file it points at.
            return Blocked(
                request.ProfileId,
                Fern.Registry.RepositoryLayout.ProfilesDirectory + "/(invalid).json",
                request.Observer,
                [new PreflightProblem(
                    PreflightProblemCode.InvalidRequest,
                    $"profile id \"{request.ProfileId}\": {ExecutionProfileV5.ProfileIdRule}",
                    "fern-run profile init local-4070")]);
        }

        // CHG 2026-09-26: profiles/ moved to config/profiles/ (Fern.Registry.RepositoryLayout).
        var profilePath = Path.Combine(Fern.Registry.RepositoryLayout.ProfilesDirectory, request.ProfileId + ".json");
        var read = ExecutionProfileV5.Read(
            WorktreeControlPaths.Profiles(request.WorkspaceRoot), request.ProfileId, null);
        if (!read.IsOk)
        {
            // Rust's `profile_at` keeps ABSENT and UNREADABLE apart, and an
            // operator acts differently on each: an absent profile is written
            // with `profile init`, an unreadable one is repaired. Collapsing
            // both into invalid_profile told the first operator to fix a file
            // that does not exist. Measured 2026-08-28 against
            // target/debug/fern-run: on an unknown profile Rust answers
            // missing_prerequisite, not invalid_profile.
            //
            // The path both problems name is the RELATIVE profiles/<id>.json
            // Rust prints, not the absolute one the reader's own detail
            // carries; a generated report is read on other machines.
            var problem = !File.Exists(Path.Combine(request.WorkspaceRoot, profilePath))
                ? new PreflightProblem(
                    PreflightProblemCode.MissingPrerequisite,
                    $"the execution profile {profilePath} does not exist",
                    $"fern-run profile init {request.ProfileId}")
                : new PreflightProblem(
                    PreflightProblemCode.InvalidProfile,
                    read.Failure.Detail,
                    $"repair {profilePath} and run fern-run profile validate {request.ProfileId} --observer host");
            return Blocked(request.ProfileId, profilePath, request.Observer, [problem]);
        }

        var profile = read.Value;
        var report = ForProfile(
            request.ProfileId, profilePath, profile, request.Observer);
        // CHG: a Site answers for its own filesystems. Observing remote CUDA
        // and then applying this host's df to /workspace mixed two machines.
        if (profile.Executor == "ssh")
        {
            var helper = StagePackageTransport.MaintainedSshHelper(WorktreeControlPaths.SshHelper(request.WorkspaceRoot));
            if (!helper.IsOk)
                return Close(report with { Problems = [ObservationRefused(request.ProfileId, helper.Failure.Detail)], ObservationFailure = helper.Failure });
            var remote = ObservedExecutionProbe.ObserveRemote(profile, helper.Value,
                request.WorkspaceRoot, request.FernVersion, ReadyExecutionGate.RemoteProbeDeadline);
            if (!remote.IsOk)
                return Close(report with { Problems = [ObservationRefused(request.ProfileId, remote.Failure.Detail)], ObservationFailure = remote.Failure });
            report = WithObservation(report, profile, remote.Value.Fingerprint) with
            {
                ExecutionFactsFrom = ExecutionFactsSource.RemoteWorkerSite,
            };
            var pathProblems = remote.Value.Paths.Select(path => ReadyExecutionGate.RemotePathProblem(profile, path))
                .Where(problem => problem is not null)
                .Select(problem => new PreflightProblem(PreflightProblemCode.MissingPrerequisite, problem!, "repair the named path on the selected Site"));
            return Close(WithRemoteStorage(report with { Problems = [.. report.Problems, .. pathProblems] }, remote.Value.Storage));
        }
        var observed = ObservedExecutionProbe.ObserveFor(request.WorkspaceRoot, profile, request.FernVersion);
        if (!observed.IsOk)
        {
            return Close(report with
            {
                Problems = [.. report.Problems, ObservationRefused(request.ProfileId, observed.Failure.Detail)],
                ObservationFailure = observed.Failure,
            });
        }

        return Close(WithDeclaredStorage(WithObservation(report, profile, observed.Value), profile));
    }

    /// <summary>The one problem a report carries when the probe itself refused.</summary>
    static PreflightProblem ObservationRefused(string profileId, string detail) => new(
        PreflightProblemCode.MissingPrerequisite,
        $"the machine facts for {profileId} could not be observed: {detail}",
        $"repair what the probe names, then repeat fern-run profile validate {profileId} --observer host");

    public static string Wire(ObserverContext observer) => observer switch
    {
        ObserverContext.Host => "host",
        ObserverContext.CodexSandbox => "codex_sandbox",
        _ => throw new UnreachableException("ObserverContext gained a case the wire name map does not name"),
    };

    /// <summary>The human label, which hyphenates where the wire name does not.</summary>
    public static string Label(ObserverContext observer) => observer switch
    {
        ObserverContext.Host => "host",
        ObserverContext.CodexSandbox => "codex-sandbox",
        _ => throw new UnreachableException("ObserverContext gained a case the label map does not name"),
    };

    public static string Wire(ExecutionFactsSource source) => source switch
    {
        ExecutionFactsSource.Host => "host",
        ExecutionFactsSource.CodexSandbox => "codex_sandbox",
        ExecutionFactsSource.RemoteWorkerSite => "remote_worker_site",
        _ => throw new UnreachableException("ExecutionFactsSource gained a case the wire name map does not name"),
    };

    public static string Label(ExecutionFactsSource source) => source switch
    {
        ExecutionFactsSource.Host => "host",
        ExecutionFactsSource.CodexSandbox => "codex-sandbox",
        ExecutionFactsSource.RemoteWorkerSite => "remote-worker-site",
        _ => throw new UnreachableException("ExecutionFactsSource gained a case the label map does not name"),
    };

    public static string Wire(PreflightProblemCode code) => code switch
    {
        PreflightProblemCode.InvalidRequest => "invalid_request",
        PreflightProblemCode.InvalidProfile => "invalid_profile",
        PreflightProblemCode.MissingPrerequisite => "missing_prerequisite",
        PreflightProblemCode.CapacityBlocked => "capacity_blocked",
        PreflightProblemCode.RemoteProbeRequired => "remote_probe_required",
        PreflightProblemCode.HostCheckRequired => "host_check_required",
        PreflightProblemCode.ProfileDrift => "profile_drift",
        PreflightProblemCode.CatalogInconsistency => "catalog_inconsistency",
        _ => throw new UnreachableException("PreflightProblemCode gained a case the wire name map does not name"),
    };

    /// <summary>Rust blocked_report: nothing was resolved, so nothing but the problems is reported.</summary>
    public static ProfileValidationResult Blocked(
        string profileId,
        string profilePath,
        ObserverContext observer,
        IReadOnlyList<PreflightProblem> problems) =>
        new(ProfileValidationStatus.Blocked,
            GpuWorkStarted: false,
            observer,
            ExecutionFactsFrom: null,
            profileId,
            profilePath,
            ProfileRevision: null,
            Executor: null,
            Device: null,
            CampaignRoot: null,
            ObservedExecution: null,
            ProfileDrift: [],
            Storage: [],
            problems,
            TypedFailures: [],
            NextStep: "Fix the listed prerequisite, then repeat this command. No GPU work has started.");

    /// <summary>
    /// The starting report for a profile that could be read: everything known
    /// without touching the machine, with `ready` still to be earned.
    /// Ports the head of Rust profile_report_with_remote_helper.
    /// </summary>
    public static ProfileValidationResult ForProfile(
        string profileId,
        string profilePath,
        ExecutionProfileV5 profile,
        ObserverContext observer) =>
        new(ProfileValidationStatus.Ready,
            GpuWorkStarted: false,
            observer,
            ExecutionFactsFrom: null,
            profileId,
            profilePath,
            profile.Revision,
            profile.Executor,
            profile.Device,
            profile.CampaignRoot,
            ObservedExecution: null,
            ProfileDrift: [],
            Storage: [],
            Problems: [],
            TypedFailures: [],
            NextStep: "just catalog");

    /// <summary>
    /// The declaration problems Rust pushes before it inspects anything: one
    /// per failed rule, each naming the field that carries it.
    /// </summary>
    public static IReadOnlyList<PreflightProblem> DeclarationProblems(
        string profileId,
        string profilePath,
        string executor,
        IReadOnlyList<ExecutionProfileIssue> issues) =>
        issues.Select(issue => new PreflightProblem(
            PreflightProblemCode.InvalidProfile,
            $"profile field {issue.Field}: {issue.Explanation}",
            $"edit {profilePath} and run fern-run profile validate {profileId} --observer host")).ToList();

    /// <summary>
    /// Record what the machine actually reported, and the ways it disagrees
    /// with the declaration. Ports record_local_execution_observation.
    ///
    /// A sandbox that cannot see the declared CUDA device is a limit of the
    /// observation, never evidence about the host, so it asks for a host check
    /// instead of listing drift it is in no position to claim.
    /// </summary>
    public static ProfileValidationResult WithObservation(
        ProfileValidationResult report,
        ExecutionProfileV5 profile,
        ObservedExecutionFingerprint observed)
    {
        var drift = ReadyExecutionGate.DriftFrom(observed, profile);
        var problems = new List<PreflightProblem>(report.Problems);
        var sandboxCannotSeeDeclaredCuda = profile.Executor == "local" && report.Observer == ObserverContext.CodexSandbox
            && profile.Device == "cuda"
            && !observed.CudaAvailable;
        if (sandboxCannotSeeDeclaredCuda)
        {
            problems.Add(new PreflightProblem(
                PreflightProblemCode.HostCheckRequired,
                "this Codex sandbox cannot see the declared CUDA device; this does not establish that the host GPU is unavailable",
                $"repeat this read-only check with host access: fern-run profile validate {profile.ProfileId} --observer host"));
        }
        else
        {
            problems.AddRange(DriftProblems(
                drift,
                "execution profile drift",
                "repair the declared environment or create a new profile revision for the machine actually being used"));
        }

        return report with
        {
            ProfileDrift = drift,
            ExecutionFactsFrom = report.Observer == ObserverContext.Host
                ? ExecutionFactsSource.Host
                : ExecutionFactsSource.CodexSandbox,
            ObservedExecution = observed,
            Problems = problems,
        };
    }

    /// <summary>Project Site-reported storage without reading any local filesystem.</summary>
    public static ProfileValidationResult WithRemoteStorage(ProfileValidationResult report, IReadOnlyList<RemoteStorage> volumes)
    {
        var problems = new List<PreflightProblem>(report.Problems);
        var checks = new List<StorageCheck>();
        foreach (var volume in volumes.DistinctBy(volume => volume.Filesystem))
        {
            if (volume.TotalBytes == 0)
            {
                problems.Add(new(PreflightProblemCode.MissingPrerequisite, $"remote storage {volume.RequestedPath} returned no capacity", "repeat the Site storage probe"));
                continue;
            }
            var free = volume.AvailableBytes / (1024.0 * 1024 * 1024);
            var percent = 100.0 * volume.AvailableBytes / volume.TotalBytes;
            var hardStop = free < ReadyExecutionGate.CampaignStorageHardStopFreeGib;
            checks.Add(new(volume.InspectedPath, volume.Filesystem, free, percent,
                free < StorageWarnFreeGib || percent < StorageWarnFreePercent, hardStop));
            if (hardStop)
                problems.Add(new(PreflightProblemCode.CapacityBlocked,
                    $"remote filesystem {volume.Filesystem} has {free:F1} GiB free, below the {ReadyExecutionGate.CampaignStorageHardStopFreeGib} GiB floor",
                    "free space on the selected Site"));
        }
        if (volumes.Count == 0)
            problems.Add(new(PreflightProblemCode.MissingPrerequisite, "remote storage observation is empty", "repeat the Site storage probe"));
        return report with { Storage = checks, Problems = problems };
    }

    /// <summary>Ports append_profile_drift_problems' report half: one problem per drifted field.</summary>
    public static IReadOnlyList<PreflightProblem> DriftProblems(
        IReadOnlyList<ExecutionProfileDrift> drift,
        string heading,
        string fixCommand) =>
        drift.Select(item => new PreflightProblem(
            PreflightProblemCode.ProfileDrift,
            $"{heading}: {item.Field} declares {item.Expected}, machine reports {item.Observed}",
            fixCommand)).ToList();

    /// <summary>
    /// Inspect every volume the profile will write to, plus the Windows volume
    /// when this checkout sits beside one. Deduplicated by filesystem, so one
    /// full disk is reported once however many roots live on it.
    /// Ports the storage half of inspect_local_profile.
    /// </summary>
    public static ProfileValidationResult WithDeclaredStorage(ProfileValidationResult report, ExecutionProfileV5 profile)
    {
        var problems = new List<PreflightProblem>(report.Problems);
        var checks = new List<StorageCheck>();
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { profile.CampaignRoot, profile.ArtifactRoot, profile.WorkRoot })
        {
            var probed = StorageProbe.Inspect(root);
            if (probed.Check is not { } check)
            {
                problems.Add(new PreflightProblem(
                    PreflightProblemCode.MissingPrerequisite,
                    probed.Refusal!,
                    $"make the parent of {root} available, then run fern-run profile validate {profile.ProfileId} --observer host"));
                continue;
            }

            if (!seen.Add(check.Filesystem))
            {
                continue;
            }

            if (check.HardStop)
            {
                problems.Add(new PreflightProblem(
                    PreflightProblemCode.CapacityBlocked,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} has only {1:F1} GiB / {2:F1}% free; Fern must not start a sweep below {3} GiB",
                        check.Filesystem,
                        check.AvailableGib,
                        check.FreePercent,
                        ReadyExecutionGate.CampaignStorageHardStopFreeGib),
                    $"free space on that volume, then run fern-run profile validate {profile.ProfileId} --observer host"));
            }

            checks.Add(check);
        }

        if (Directory.Exists("/mnt/c"))
        {
            var probed = StorageProbe.Inspect("/mnt/c");
            if (probed.Check is { } windows)
            {
                if (seen.Add(windows.Filesystem))
                {
                    if (windows.HardStop)
                    {
                        problems.Add(new PreflightProblem(
                            PreflightProblemCode.CapacityBlocked,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0} has only {1:F1} GiB / {2:F1}% free; WSL cannot safely grow its backing disk",
                                windows.Filesystem,
                                windows.AvailableGib,
                                windows.FreePercent),
                            $"free Windows C: space, then run fern-run profile validate {profile.ProfileId} --observer host"));
                    }

                    checks.Add(windows);
                }
            }
            else
            {
                problems.Add(new PreflightProblem(
                    PreflightProblemCode.MissingPrerequisite,
                    probed.Refusal!,
                    "make /mnt/c readable before starting a broad build or sweep"));
            }
        }

        return report with { Storage = checks, Problems = problems };
    }

    /// <summary>Rust close_report: problems decide the status and the closing sentence.</summary>
    public static ProfileValidationResult Close(ProfileValidationResult report)
    {
        if (report.Problems.Count == 0)
        {
            return report;
        }

        var needsOnlyHostCheck = report.Problems.All(
            problem => problem.Code == PreflightProblemCode.HostCheckRequired);
        var nextStep = needsOnlyHostCheck
            ? $"Repeat this read-only check outside the Codex sandbox: fern-run profile validate {report.ProfileId} --observer host. No GPU work has started."
            : report.NextStep == "just catalog"
                ? "Fix the listed prerequisite, then repeat this command. No GPU work has started."
                : report.NextStep;
        return report with
        {
            Status = needsOnlyHostCheck ? ProfileValidationStatus.NeedsHostCheck : ProfileValidationStatus.Blocked,
            NextStep = nextStep,
        };
    }

}

/// <summary>
/// How much room is left on the volumes a campaign writes to.
///
/// Ported 2026-08-28 from fern-cli-rs experiment_front_door.rs `probe_storage`.
/// The nearest EXISTING ancestor of a declared root is inspected, because a
/// campaign root that has not been created yet still lives on a filesystem that
/// can be full. `df -Pk` is asked rather than a .NET drive enumeration: the
/// answer must name the same filesystem string the old executable printed.
/// </summary>
public static class StorageProbe
{
    /// <summary>Either a reading, or the reason there is none.</summary>
    public sealed record Reading(StorageCheck? Check, string? Refusal);

    public static Reading Inspect(string requestedPath)
    {
        var existing = NearestExistingAncestor(requestedPath);
        if (existing is null)
        {
            return new Reading(null, $"no existing ancestor for declared path {requestedPath}");
        }

        string output;
        try
        {
            var start = new ProcessStartInfo("df")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-Pk");
            start.ArgumentList.Add(existing);
            using var process = Process.Start(start)
                ?? throw new IOException("df did not start");
            var errors = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return new Reading(null, $"df cannot inspect {existing}: {errors.GetAwaiter().GetResult().Trim()}");
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            return new Reading(null, $"cannot inspect storage for {existing}: {error.Message}");
        }

        var line = output.Split('\n').Skip(1).FirstOrDefault(row => row.Trim().Length > 0);
        if (line is null)
        {
            return new Reading(null, $"df returned no data row for {existing}");
        }

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 6)
        {
            return new Reading(null, $"df returned an incomplete row for {existing}");
        }

        if (!double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var availableKib))
        {
            return new Reading(null, $"df returned invalid available space for {existing}");
        }

        if (!double.TryParse(
                fields[4].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var usedPercent))
        {
            return new Reading(null, $"df returned invalid usage percent for {existing}");
        }

        var availableGib = availableKib / (1024.0 * 1024.0);
        var freePercent = Math.Max(100.0 - usedPercent, 0.0);
        var (warning, hardStop) = Verdict(availableGib, freePercent);
        return new Reading(new StorageCheck(existing, fields[0], availableGib, freePercent, warning, hardStop), null);
    }

    /// <summary>
    /// What a reading MEANS, separated from the volume it was taken on. The
    /// thresholds are probe_storage's own two constants and the campaign hard
    /// stop; a live disk rarely sits near any of them, so the rule is testable
    /// only when it can be asked about a number instead of a machine.
    /// </summary>
    public static (bool Warning, bool HardStop) Verdict(double availableGib, double freePercent) => (
        availableGib < Preflight.StorageWarnFreeGib || freePercent < Preflight.StorageWarnFreePercent,
        availableGib < ReadyExecutionGate.CampaignStorageHardStopFreeGib);

    static string? NearestExistingAncestor(string requestedPath)
    {
        var path = requestedPath;
        while (path.Length > 0)
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                return path;
            }

            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent == path)
            {
                return null;
            }

            path = parent;
        }

        return null;
    }
}

/// <summary>
/// The execution-profile rules that need no machine access and change no state.
///
/// Ported 2026-08-28 from crates/fern-execution-profile `declaration_issues`.
/// It answers a DIFFERENT question from <see cref="ExecutionProfileV5.Read"/>,
/// which is the admission gate and refuses a bad file with one message: this
/// answers "what does an operator have to fix", as the list `profile validate`
/// prints. Rust keeps the same split, and the rules a reader
/// can only see in the raw declaration — a local profile that names an SSH
/// destination, a warn threshold below the campaign hard stop, a worker module
/// that is not a dotted Python name — live only here.
/// </summary>
public static class ExecutionProfileDeclaration
{
    public const string Schema = "fern.execution_profile.v5";
    public const double CampaignStorageHardStopFreeGib = ReadyExecutionGate.CampaignStorageHardStopFreeGib;

    public static IReadOnlyList<ExecutionProfileIssue> Issues(JsonElement declaration)
    {
        var issues = new List<ExecutionProfileIssue>();
        var schema = Text(declaration, "schema");
        if (schema != Schema)
        {
            issues.Add(new ExecutionProfileIssue(
                "schema",
                $"expected \"{Schema}\", found {Quoted(schema)}"));
        }

        foreach (var retired in new[] { "worker_binary", "worker_binary_sha256" })
        {
            if (declaration.TryGetProperty(retired, out _))
            {
                issues.Add(new ExecutionProfileIssue(
                    retired,
                    "is retired; whole-Cell execution uses the installed fern-site session and declared Python interpreter"));
            }
        }

        var profileId = Text(declaration, "profile_id") ?? "";
        if (ProfileIdProblem(profileId) is { } profileIdProblem)
        {
            issues.Add(new ExecutionProfileIssue("profile_id", profileIdProblem));
        }

        var executor = Text(declaration, "executor");
        var isSsh = executor == "ssh";
        var destination = Text(declaration, "ssh_destination");
        if (!isSsh && destination is { } local && local.Trim().Length > 0)
        {
            issues.Add(new ExecutionProfileIssue(
                "ssh_destination", "a local profile must not name an SSH destination"));
        }
        else if (isSsh && (destination is null || destination.Trim().Length == 0))
        {
            issues.Add(new ExecutionProfileIssue(
                "ssh_destination", "an SSH profile needs a non-empty host alias or destination"));
        }

        var registryHome = Text(declaration, "registry_home_profile_id");
        if (!isSsh && registryHome is not null)
        {
            issues.Add(new ExecutionProfileIssue(
                "registry_home_profile_id",
                "a local profile is already its own Registry Home and must not name another profile"));
        }
        else if (isSsh && registryHome is null)
        {
            issues.Add(new ExecutionProfileIssue(
                "registry_home_profile_id", "an SSH Worker Site must name the local Registry Home profile"));
        }
        else if (isSsh && registryHome is { } homeId && ProfileIdProblem(homeId) is { } homeProblem)
        {
            issues.Add(new ExecutionProfileIssue("registry_home_profile_id", homeProblem));
        }

        foreach (var (field, description) in new[]
                 {
                     ("source_root", "must be absolute so a command behaves the same from every working directory"),
                     ("python", "must be absolute so a command behaves the same from every working directory"),
                     ("campaign_root", "must be absolute so a command behaves the same from every working directory"),
                     ("artifact_root", "must be absolute so a command behaves the same from every working directory"),
                     ("work_root", "must be absolute so a command behaves the same from every working directory"),
                 })
        {
            var value = Text(declaration, field) ?? "";
            if (!value.StartsWith('/'))
            {
                issues.Add(new ExecutionProfileIssue(field, $"{value} {description}"));
            }
        }

        if (Text(declaration, "known_data_root") is { } knownDataRoot && !knownDataRoot.StartsWith('/'))
        {
            issues.Add(new ExecutionProfileIssue(
                "known_data_root",
                $"{knownDataRoot} must be absolute so recorded-data preparation is unambiguous"));
        }

        // CHG: the local Home declares its database independently of campaign storage.
        if (declaration.TryGetProperty("registry_path", out var registryPath)
            && (isSsh || registryPath.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(registryPath.GetString())
                || !Path.IsPathFullyQualified(registryPath.GetString()!)))
        {
            issues.Add(new ExecutionProfileIssue("registry_path",
                isSsh ? "belongs to the named local Registry Home profile, not the remote Site"
                    : "must name an absolute Registry database path"));
        }

        var workerModule = declaration.TryGetProperty("worker", out var worker)
            ? Text(worker, "module") ?? ""
            : "";
        if (!PythonModule(workerModule))
        {
            issues.Add(new ExecutionProfileIssue(
                "worker.module",
                $"{Quoted(workerModule)} is not a dotted Python module name such as fern_train.worker or package.worker"));
        }

        if (Number(worker, "slots") != 1)
        {
            issues.Add(new ExecutionProfileIssue(
                "worker.slots",
                "the current local executor supports exactly one capacity-one slot; declare 1 instead of relying on accidental GPU sharing"));
        }

        declaration.TryGetProperty("supervisor", out var supervisor);
        var maxTarget = Number(supervisor, "max_target_seconds");
        var maxSweep = Number(supervisor, "max_sweep_seconds");
        var maxRestarts = Number(supervisor, "max_restarts");
        var restartDelay = Number(supervisor, "restart_delay_seconds");
        if (maxTarget == 0)
        {
            issues.Add(new ExecutionProfileIssue(
                "supervisor.max_target_seconds",
                "must be positive; an unbounded target can consume a rented machine indefinitely"));
        }

        if (maxSweep < maxTarget)
        {
            issues.Add(new ExecutionProfileIssue(
                "supervisor.max_sweep_seconds",
                "must be at least max_target_seconds so the declared target budget can actually be used"));
        }

        if (maxRestarts > 0 && restartDelay == 0)
        {
            issues.Add(new ExecutionProfileIssue(
                "supervisor.restart_delay_seconds",
                "must be positive when restarts are enabled; immediate crash loops waste compute"));
        }

        issues.AddRange(AcceleratorIssues(declaration));

        if (Number(declaration, "warn_free_gib") < CampaignStorageHardStopFreeGib)
        {
            issues.Add(new ExecutionProfileIssue(
                "warn_free_gib",
                $"must be at least {CampaignStorageHardStopFreeGib} GiB; a warning threshold may be stricter but cannot sit below the campaign hard stop"));
        }

        var warnPercent = Number(declaration, "warn_free_percent");
        if (warnPercent is < 1 or > 100)
        {
            issues.Add(new ExecutionProfileIssue(
                "warn_free_percent",
                $"must be between 1 and 100; this is an early warning only, while the campaign hard stop is {CampaignStorageHardStopFreeGib} GiB absolute free space"));
        }

        return issues;
    }

    static IEnumerable<ExecutionProfileIssue> AcceleratorIssues(JsonElement declaration)
    {
        var device = Text(declaration, "device");
        var hasAccelerator = declaration.TryGetProperty("accelerator", out var accelerator)
            && accelerator.ValueKind == JsonValueKind.Object;
        if (device == "cpu" && hasAccelerator)
        {
            yield return new ExecutionProfileIssue("accelerator", "a CPU profile cannot promise a CUDA accelerator");
            yield break;
        }

        if (device != "cuda")
        {
            yield break;
        }

        if (!hasAccelerator)
        {
            yield return new ExecutionProfileIssue(
                "accelerator", "a CUDA profile must declare at least its minimum GPU memory");
            yield break;
        }

        if (Number(accelerator, "min_memory_gib") == 0)
        {
            yield return new ExecutionProfileIssue("accelerator.min_memory_gib", "must be positive for a CUDA profile");
        }

        if (accelerator.TryGetProperty("min_compute_capability", out var capability)
            && capability.ValueKind == JsonValueKind.Array
            && capability.GetArrayLength() > 0
            && capability[0].TryGetInt64(out var major)
            && major == 0)
        {
            yield return new ExecutionProfileIssue(
                "accelerator.min_compute_capability",
                "must be omitted or name a positive CUDA major capability");
        }

        if (Text(accelerator, "required_device_uuid") is { } uuid
            && !(uuid == uuid.Trim() && uuid.StartsWith("GPU-", StringComparison.Ordinal) && uuid.Length > 4))
        {
            yield return new ExecutionProfileIssue(
                "accelerator.required_device_uuid",
                "must be omitted or use the exact NVIDIA GPU- UUID reported by nvidia-smi");
        }
    }

    /// <summary>Rust validate_profile_id, whose one message covers every way the name can be wrong.</summary>
    public static string? ProfileIdProblem(string profileId)
    {
        var valid = profileId.Length is > 0 and <= 80
            && !profileId.StartsWith('-')
            && !profileId.EndsWith('-')
            && profileId.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
        return valid
            ? null
            : "must be 1–80 lowercase letters, digits, and interior hyphens; this keeps its file path predictable";
    }

    static bool PythonModule(string module) =>
        module.Length > 0
        && module.Split('.').All(segment =>
            segment.Length > 0
            && (segment[0] == '_' || char.IsAsciiLetter(segment[0]))
            && segment.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character)));

    static bool LowerSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static string? Text(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static long Number(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    static string Quoted(string? value) => value is null ? "null" : "\"" + value + "\"";
}
