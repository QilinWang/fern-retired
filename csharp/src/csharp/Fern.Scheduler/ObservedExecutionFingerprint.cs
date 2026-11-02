using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;
using Fern.Site;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>Host facts that explain where one Attempt actually ran.</summary>
public sealed record ObservedPlatform(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("release")] string Release,
    [property: JsonPropertyName("machine")] string Machine);

/// <summary>Python process facts observed by the no-write preflight probe.</summary>
public sealed record ObservedPythonRuntime(
    /// <summary>Exact path Fern asked the operating system to execute.</summary>
    [property: JsonPropertyName("requested_executable")] string RequestedExecutable,
    /// <summary><c>sys.executable</c> reported inside that process.</summary>
    [property: JsonPropertyName("sys_executable")] string SysExecutable,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("implementation")] string Implementation,
    [property: JsonPropertyName("worker_module")] string WorkerModule,
    [property: JsonPropertyName("worker_module_available")] bool WorkerModuleAvailable);

/// <summary>
/// Numerical-library facts that may change results without changing the
/// scientific Coordinate.
/// </summary>
public sealed record ObservedTorchRuntime(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("cuda_runtime_version")] string? CudaRuntimeVersion,
    [property: JsonPropertyName("cudnn_version")] ulong? CudnnVersion,
    [property: JsonPropertyName("deterministic_algorithms")] bool DeterministicAlgorithms,
    [property: JsonPropertyName("float32_matmul_precision")] string Float32MatmulPrecision,
    [property: JsonPropertyName("cuda_matmul_allow_tf32")] bool CudaMatmulAllowTf32,
    [property: JsonPropertyName("cudnn_allow_tf32")] bool CudnnAllowTf32,
    [property: JsonPropertyName("cudnn_deterministic")] bool CudnnDeterministic,
    [property: JsonPropertyName("cudnn_benchmark")] bool CudnnBenchmark);

/// <summary>
/// One GPU visible to the probed Python process. <c>uuid</c> and
/// <c>driver_version</c> are the only two fields that disappear rather than
/// appear as null when unknown, because Rust declared them
/// <c>skip_serializing_if = "Option::is_none"</c> and they are inside the
/// content digest.
/// </summary>
public sealed record ObservedCudaDevice(
    [property: JsonPropertyName("index")] uint Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("capability")] IReadOnlyList<uint> Capability,
    [property: JsonPropertyName("total_memory_bytes")] ulong TotalMemoryBytes,
    [property: JsonPropertyName("multi_processor_count")] uint MultiProcessorCount,
    [property: JsonPropertyName("uuid")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uuid,
    [property: JsonPropertyName("driver_version")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DriverVersion);

/// <summary>
/// Immutable, hashable facts observed before a worker receives authority.
///
/// This is provenance, not the scientific experiment identity. It lets Fern
/// say "the profile required at least 7 GiB and CUDA capability 8.9, while
/// this Attempt observed a laptop 4070" without making every marketing model
/// name a different research question.
///
/// The property ORDER below is load-bearing and is Rust's struct order
/// (crates/fern-execution-profile/src/lib.rs ObservedExecutionFingerprint).
/// Both digests hash this object's own bytes, and
/// <see cref="ObservedExecutionProbe.EnvironmentContentDigest"/> is defined as
/// "these bytes with one property removed" — the same definition
/// <c>StartOrResumeAuthority.EnvironmentContentDigest</c> applies to a
/// fingerprint it is handed. Reordering a property here silently changes every
/// Worker Site capability identity.
/// </summary>
public sealed record ObservedExecutionFingerprint(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("observed_at_unix_ms")] ulong ObservedAtUnixMs,
    [property: JsonPropertyName("fern_version")] string FernVersion,
    [property: JsonPropertyName("platform")] ObservedPlatform Platform,
    [property: JsonPropertyName("python")] ObservedPythonRuntime Python,
    [property: JsonPropertyName("torch")] ObservedTorchRuntime Torch,
    [property: JsonPropertyName("cuda_available")] bool CudaAvailable,
    [property: JsonPropertyName("cuda_current_device")] uint? CudaCurrentDevice,
    /// <summary>
    /// The logical-device mask seen by the probed Python process. It is
    /// recorded because CUDA index 0 need not mean physical GPU 0.
    /// </summary>
    [property: JsonPropertyName("cuda_visible_devices")] string? CudaVisibleDevices,
    /// <summary>
    /// CUDA's device-enumeration policy, which can make a numeric visibility
    /// token differ from <c>nvidia-smi</c>'s management index.
    /// </summary>
    [property: JsonPropertyName("cuda_device_order")] string? CudaDeviceOrder,
    /// <summary>The container-runtime GPU exposure rule, when present.</summary>
    [property: JsonPropertyName("nvidia_visible_devices")] string? NvidiaVisibleDevices,
    [property: JsonPropertyName("cuda_devices")] IReadOnlyList<ObservedCudaDevice> CudaDevices);

/// <summary>One remote filesystem the Site reported for a declared root.</summary>
public sealed record RemoteStorage(
    string RequestedPath,
    string InspectedPath,
    string Filesystem,
    ulong AvailableBytes,
    ulong TotalBytes);

/// <summary>One declared path as the Site actually found it.</summary>
public sealed record RemotePath(
    string RequestedPath,
    bool Exists,
    bool IsDirectory,
    bool Executable,
    bool WritableAncestor);

/// <summary>
/// What one remote observation returns: the same fingerprint a local probe
/// produces, plus the two things only the Site can answer — how much room its
/// volumes have, and whether the declared paths are actually there.
/// </summary>
public sealed record RemoteObservation(
    ObservedExecutionFingerprint Fingerprint,
    IReadOnlyList<RemoteStorage> Storage,
    IReadOnlyList<RemotePath> Paths);

/// <summary>One `nvidia-smi` inventory row: which physical card, on which driver.</summary>
public sealed record NvidiaManagementFact(string Uuid, string DriverVersion);

/// <summary>
/// Observe this machine — or the machine an execution profile names — and
/// return the `fern.observed_execution_fingerprint.v2` a Worker Site
/// registration and an Attempt start are identified by.
///
/// The observation itself is Python, because every fact in it belongs to the
/// interpreter that will run the work: its own path, its torch build, the
/// numerical policy that build defaults to, and the CUDA devices torch can
/// actually see. Asking C# for those would answer about a different process.
/// So this class owns the request, the deadline, the typed result and the
/// identity, and `python/fern_stage_runtime/observe_execution.py` — invoked with the
/// profile's own interpreter — owns the observation.
///
/// CHG 2026-08-28: ported from fern-cli-rs/src/experiment_front_door.rs
/// (`probe_execution`, `observed_execution_from_probe`,
/// `attach_nvidia_management_facts`, `attach_known_nvidia_management_facts`),
/// whose embedded Python string became the module named above.
/// </summary>
/// <summary>
/// Where a caller that holds a worktree keeps its profiles and its maintained
/// SSH helper.
///
/// The installed scheduler holds neither root: its installation receipt names
/// the profiles directory and the helper file outright, because an installed
/// binary set is bound to content and must keep running when the tree it was
/// built from is gone. These two lines exist for the callers that DO run from
/// a worktree — the operator CLI and the readiness gate.
/// </summary>
/// <summary>
/// The capability a launch-time observation reduces to: what this machine is,
/// as seen once. CHG 2026-09-11 night (slice 9 of the cell port): this record
/// lived beside the old Worker Site registry (`WorkerSiteAuthority`), whose
/// rows it filled; the registry retired with `worker_sites`, and the record
/// stays with the observation that produces it.
/// </summary>
public sealed record SiteCapabilityFacts(
    string Device,
    string Hostname,
    bool CudaAvailable,
    string? CudaDeviceName,
    string? CudaComputeCapability,
    long? CudaTotalMemoryBytes,
    string PythonVersion,
    string PythonExecutable,
    string TorchVersion,
    string? WorkerBinarySha256,
    string SourceRoot,
    string ObservedEnvironmentDigest,
    long ObservedAtMs,
    string ObservedFingerprintJson);

public static class WorktreeControlPaths
{
    /// <summary>
    /// The repository root a worktree caller runs from: the nearest ancestor
    /// of the current directory that holds both the Python worker and the
    /// profiles directory. Moved here verbatim on 2026-09-04 from
    /// `ExperimentLaunchPlanning.RepoRoot`, which retired with the recipe route;
    /// it was already the one answer the operator CLI and this project shared.
    /// CHG 2026-09-26: the profiles directory moved to config/profiles/
    /// (Fern.Registry.RepositoryLayout); the root is still the ancestor that
    /// holds both.
    /// </summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "python", "fern_train", "worker.py"))
                && Directory.Exists(Profiles(directory.FullName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Fern.Registry.RegistryFault("cannot locate the Fern repository root");
    }

    /// <summary>The execution profiles of a workspace: config/profiles/ below its root since 2026-09-26.</summary>
    public static string Profiles(string workspaceRoot) =>
        Path.Combine(workspaceRoot, Fern.Registry.RepositoryLayout.ProfilesDirectory);

    public static string SshHelper(string workspaceRoot) => Path.Combine(
        workspaceRoot,
        StagePackageTransport.MaintainedSshHelperRelativePath.Replace('/', Path.DirectorySeparatorChar));
}

public static class ObservedExecutionProbe
{
    public const string Schema = "fern.observed_execution_fingerprint.v2";

    /// <summary>
    /// serde_json's compact form: no whitespace, declaration order, and no
    /// escaping of non-ASCII. The same encoder
    /// <c>StartOrResumeAuthority.EnvironmentContentDigest</c> writes with, so
    /// the two agree on the bytes they hash.
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The exact bytes both digests are taken over, and the bytes a
    /// capability row stores as its `observed_fingerprint_json`.</summary>
    public static string Render(ObservedExecutionFingerprint fingerprint) =>
        JsonSerializer.Serialize(fingerprint, Wire);

    /// <summary>
    /// Hash the complete observation receipt, including when it was sampled.
    ///
    /// This answers "are these the same recorded bytes/facts?" It must not be
    /// used as an idempotent operation identity because a later probe has a
    /// different timestamp even when the environment is unchanged.
    /// </summary>
    public static string ObservationDigest(ObservedExecutionFingerprint fingerprint) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Render(fingerprint))));

    /// <summary>
    /// Hash stable environment content while excluding the observation time.
    ///
    /// A lost-response retry may probe the same machine one minute later and
    /// must still replay the first start instead of creating an idempotency
    /// conflict. Actual runtime, host, GPU, or numerical-policy changes remain
    /// visible because every field except <c>observed_at_unix_ms</c> is
    /// included.
    ///
    /// Rust re-serialized a struct that repeated every other field in order;
    /// this drops the one property from the rendered object, which is the same
    /// bytes and cannot drift from the fingerprint when a field is added.
    /// <c>StartOrResumeAuthority.EnvironmentContentDigest</c> computes the
    /// identical hash from the JSON alone, without the `sha256:` prefix Rust's
    /// callers store; ObservedExecutionFingerprintTests holds both to that.
    /// </summary>
    public static string EnvironmentContentDigest(ObservedExecutionFingerprint fingerprint)
    {
        using var document = JsonDocument.Parse(Render(fingerprint));
        return EnvironmentContentDigest(document.RootElement);
    }

    /// <summary>
    /// Recompute the stable environment-content digest from the durable
    /// Registry copy of <c>observed_fingerprint_json</c>. EnvironmentAdmission
    /// consumes MachineAdmission instead of running the same runtime probe a
    /// second time, so its receipt still needs the exact content digest derived
    /// from the parent proof bytes.
    /// </summary>
    public static string EnvironmentContentDigestFromRenderedJson(string renderedFingerprintJson)
    {
        using var document = JsonDocument.Parse(renderedFingerprintJson);
        return EnvironmentContentDigest(document.RootElement);
    }

    static string EnvironmentContentDigest(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("observed_at_unix_ms"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// Observe the machine an execution profile names, by the route the profile
    /// declares: the local probe for a local profile, the maintained SSH helper
    /// for an ssh one. This is the one place a report-only command (plan or
    /// profile validate) obtains machine facts; the ready-execution
    /// gate below takes the same two routes and then applies its refusals.
    /// A remote observation's storage and path answers are dropped here
    /// because the report family reads storage through its own probe.
    /// </summary>
    public static SiteResult<ObservedExecutionFingerprint> ObserveFor(
        string workspaceRoot,
        ExecutionProfileV5 profile,
        string fernVersion)
    {
        if (profile.Executor != "ssh")
        {
            return Observe(profile, fernVersion, ReadyExecutionGate.LocalProbeDeadline);
        }
        var helper = StagePackageTransport.MaintainedSshHelper(WorktreeControlPaths.SshHelper(workspaceRoot));
        if (!helper.IsOk)
        {
            return helper.RefusalAs<ObservedExecutionFingerprint>();
        }
        var remote = ObserveRemote(
            profile, helper.Value, workspaceRoot, fernVersion, ReadyExecutionGate.RemoteProbeDeadline);
        return remote.IsOk
            ? SiteResult<ObservedExecutionFingerprint>.Ok(remote.Value.Fingerprint)
            : remote.RefusalAs<ObservedExecutionFingerprint>();
    }

    /// <summary>
    /// Run the observation on this host with the profile's own interpreter.
    ///
    /// <paramref name="fernVersion"/> is required and never defaulted: it
    /// names the program that made the observation, it is inside the content
    /// digest, and therefore inside every Worker Site capability identity. A
    /// caller that guesses it moves those identities silently.
    ///
    /// Nothing here claims a Coordinate, opens a database, or creates a worker
    /// directory, so a caller may truthfully report that no GPU work started
    /// when it refuses.
    /// </summary>
    public static SiteResult<ObservedExecutionFingerprint> Observe(
        ExecutionProfileV5 profile,
        string fernVersion,
        TimeSpan deadline)
    {
        if (string.IsNullOrWhiteSpace(fernVersion))
        {
            return SiteResult<ObservedExecutionFingerprint>.Permanent(
                "the observing program's version is required: it is inside the environment content digest");
        }

        var observedAt = DateTimeOffset.UtcNow;
        // Rust's environment, unchanged: no bytecode beside the source it
        // reads, the source root the probe reports, and the two import roots
        // that make the declared worker module importable.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["FERN_PROBE_SOURCE_ROOT"] = profile.SourceRoot,
            // CHG 2026-09-26: src/ joins the import roots (serie/ moved to
            // src/serie/). The probe does not report its import path, so the
            // environment content digest is unchanged by this.
            ["PYTHONPATH"] = $"{profile.SourceRoot}:{profile.SourceRoot}/python:{profile.SourceRoot}/src",
        };
        var outcome = BoundedProbe.Run(
            profile.Python,
            ["-m", ProbeModule, profile.WorkerModule],
            (long)deadline.TotalMilliseconds,
            MaxProbeBytes,
            workingDirectory: null,
            environment);

        switch (outcome)
        {
            case BoundedProbe.Outcome.Missing:
                return SiteResult<ObservedExecutionFingerprint>.Permanent(
                    $"the declared Python executable {profile.Python} does not exist");
            case BoundedProbe.Outcome.NotRegular:
            case BoundedProbe.Outcome.NotExecutable:
                return SiteResult<ObservedExecutionFingerprint>.Permanent(
                    $"the declared Python executable {profile.Python} is not an executable file");
            case BoundedProbe.Outcome.TimedOut timedOut:
                return SiteResult<ObservedExecutionFingerprint>.Permanent(
                    $"the execution probe under {profile.Python} did not finish within {timedOut.TimeoutMs} ms");
            case BoundedProbe.Outcome.UnboundedOutput unbounded:
                return SiteResult<ObservedExecutionFingerprint>.Permanent(
                    $"the execution probe under {profile.Python} produced more than {unbounded.MaxBytes} bytes");
            case BoundedProbe.Outcome.Completed completed when completed.ExitCode != 0:
                return SiteResult<ObservedExecutionFingerprint>.Permanent(
                    $"the execution probe under {profile.Python} failed: {completed.Stderr.Trim()}");
            case BoundedProbe.Outcome.Completed completed:
                try
                {
                    return SiteResult<ObservedExecutionFingerprint>.Ok(FromProbeReport(
                        profile, fernVersion, Encoding.UTF8.GetString(completed.Stdout), observedAt));
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or OverflowException)
                {
                    return SiteResult<ObservedExecutionFingerprint>.Permanent(
                        $"the execution probe under {profile.Python} returned unreadable output: {error.Message}");
                }
            default:
                throw new InvalidOperationException($"unknown bounded-probe outcome {outcome.GetType().Name}");
        }
    }

    /// <summary>
    /// The probe's report is a fixed set of machine facts, not a data stream:
    /// one interpreter, one host, one torch build, and one row per visible
    /// GPU. A quarter of a megabyte is far more than that and far less than
    /// anything that could exhaust this process.
    /// </summary>
    const long MaxProbeBytes = 256 * 1024;

    /// <summary>
    /// Observe the machine an SSH execution profile names, through the one
    /// maintained entrance.
    ///
    /// The helper authenticates and transports one read-only probe. It does
    /// not own a Matrix, claim a Coordinate, inspect process names, or start a
    /// worker. A failed probe therefore means only "remote state unknown" and
    /// blocks the caller before any GPU work begins — it is never evidence
    /// about Python, CUDA, the source, or the science (correction 356).
    ///
    /// Two differences from the local route, both deliberate. The probe is
    /// sent as SOURCE rather than run as an installed module, exactly as Rust
    /// sent it: the Site then runs the same bytes this host holds, whatever
    /// its deployed source root contains, and registering a Site needs nothing
    /// deployed first. And the declared roots are passed as arguments, because
    /// only the Site can say whether they exist, are writable, and have space.
    /// </summary>
    /// <param name="reviewedSourceRoot">
    /// THIS host's reviewed checkout, which is where the probe script is read
    /// from. It is not the Site's <c>source_root</c>.
    ///
    /// CHG 2026-08-28: it used to be. `ProbeScript(profile.SourceRoot)` opened
    /// the file with File.Exists on the machine composing the command, so an
    /// SSH profile could only be observed if its REMOTE source root happened to
    /// exist locally at the same absolute path. None does: `profile validate
    /// runpod-5090` refused with "the execution probe
    /// /tmp/fern-runtime-hot/source/&lt;digest&gt;/... is missing from the
    /// reviewed source" on the host that has no such directory, which means no
    /// Worker Site could be registered at all. The script is this host's own
    /// reviewed copy and always was; only the path it was read from was wrong.
    /// </param>
    public static SiteResult<RemoteObservation> ObserveRemote(
        ExecutionProfileV5 profile,
        string helper,
        string reviewedSourceRoot,
        string fernVersion,
        TimeSpan deadline)
    {
        if (string.IsNullOrWhiteSpace(fernVersion))
        {
            return SiteResult<RemoteObservation>.Permanent(
                "the observing program's version is required: it is inside the environment content digest");
        }

        if (profile.SshDestination is not { Length: > 0 } destination)
        {
            return SiteResult<RemoteObservation>.Permanent(
                "an SSH Worker Site has no ssh_destination to observe");
        }

        var script = ProbeScript(reviewedSourceRoot);
        if (!script.IsOk)
        {
            return script.RefusalAs<RemoteObservation>();
        }

        var observedAt = DateTimeOffset.UtcNow;
        var seconds = (long)deadline.TotalSeconds;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The helper's own two inputs. The endpoint is an observed input,
            // never Site or profile identity, so it is read from the profile
            // at every call and never cached.
            ["RUNPOD_SSH_TARGET"] = destination,
            ["RUNPOD_COMMAND_TIMEOUT_SECONDS"] = seconds.ToString(CultureInfo.InvariantCulture),
        };
        var arguments = new List<string>
        {
            "exec", "--",
            "env",
            "PYTHONDONTWRITEBYTECODE=1",
            $"FERN_PROBE_SOURCE_ROOT={profile.SourceRoot}",
            // CHG 2026-09-26: src/ joins the import roots (serie/ moved to src/serie/).
            $"PYTHONPATH={profile.SourceRoot}:{profile.SourceRoot}/python:{profile.SourceRoot}/src",
            profile.Python,
            "-c", script.Value, profile.WorkerModule,
            profile.CampaignRoot, profile.ArtifactRoot, profile.WorkRoot,
        };

        if (profile.KnownDataRoot is { } knownDataRoot)
        {
            arguments.Add(knownDataRoot);
        }

        // The helper owns the deadline and this process gives it a margin, so
        // a slow Site is diagnosed by the end that can see it — "gateway
        // handshake failed", "connection refused" — instead of being killed
        // here and reported as a bare parent timeout that names nothing.
        var outcome = BoundedProbe.Run(
            helper,
            arguments,
            (long)(deadline + HelperReportingMargin).TotalMilliseconds,
            MaxProbeBytes,
            workingDirectory: null,
            environment);
        switch (outcome)
        {
            case BoundedProbe.Outcome.Missing:
            case BoundedProbe.Outcome.NotRegular:
            case BoundedProbe.Outcome.NotExecutable:
                return SiteResult<RemoteObservation>.Permanent(
                    $"the maintained SSH helper {helper} is not an executable file; "
                    + "Fern will not construct an ad-hoc ssh command");
            case BoundedProbe.Outcome.TimedOut timedOut:
                return SiteResult<RemoteObservation>.Permanent(
                    $"the remote execution probe on {destination} did not answer within {timedOut.TimeoutMs} ms; "
                    + "remote state is unknown. No GPU work started.");
            case BoundedProbe.Outcome.UnboundedOutput unbounded:
                return SiteResult<RemoteObservation>.Permanent(
                    $"the remote execution probe on {destination} produced more than {unbounded.MaxBytes} bytes; "
                    + "remote state is unknown. No GPU work started.");
            case BoundedProbe.Outcome.Completed completed when completed.ExitCode != 0:
                return SiteResult<RemoteObservation>.Refused(StagePackageTransport.ClassifyMaintainedSshHelperFailure(
                    $"the transport to {destination} or the remote probe failed: {completed.Stderr.Trim()}. "
                    + "Remote state is unknown; this says nothing about Python, CUDA, the source, or the science.", completed.ExitCode, completed.Stderr));
            case BoundedProbe.Outcome.Completed completed:
                try
                {
                    return SiteResult<RemoteObservation>.Ok(RemoteObservationFrom(
                        profile, fernVersion, Encoding.UTF8.GetString(completed.Stdout), observedAt));
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or OverflowException)
                {
                    return SiteResult<RemoteObservation>.Permanent(
                        $"the remote execution probe on {destination} returned an unreadable report: {error.Message}");
                }
            default:
                throw new InvalidOperationException($"unknown bounded-probe outcome {outcome.GetType().Name}");
        }
    }

    /// <summary>How much longer than the helper's own deadline this process
    /// waits, so the helper's diagnosis arrives instead of being killed.</summary>
    static readonly TimeSpan HelperReportingMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The probe's own source, read from the module the local route imports,
    /// so a remote observation and a local one are the same program.
    /// </summary>
    static SiteResult<string> ProbeScript(string sourceRoot)
    {
        var path = Path.Combine(sourceRoot, "python", "fern_stage_runtime", "observe_execution.py");
        if (!File.Exists(path))
        {
            return SiteResult<string>.Permanent($"the execution probe {path} is missing from the reviewed source");
        }

        return SiteResult<string>.Ok(File.ReadAllText(path));
    }

    static RemoteObservation RemoteObservationFrom(
        ExecutionProfileV5 profile,
        string fernVersion,
        string report,
        DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(report);
        var root = document.RootElement;
        var storage = new List<RemoteStorage>();
        if (root.TryGetProperty("storage", out var reported) && reported.ValueKind == JsonValueKind.Array)
        {
            foreach (var volume in reported.EnumerateArray())
            {
                storage.Add(new RemoteStorage(
                    RequestedPath: volume.GetProperty("requested_path").GetString() ?? string.Empty,
                    InspectedPath: volume.GetProperty("inspected_path").GetString() ?? string.Empty,
                    Filesystem: volume.GetProperty("filesystem").GetString() ?? string.Empty,
                    AvailableBytes: volume.GetProperty("available_bytes").GetUInt64(),
                    TotalBytes: volume.GetProperty("total_bytes").GetUInt64()));
            }
        }

        var paths = new List<RemotePath>();
        if (root.TryGetProperty("paths", out var reportedPaths) && reportedPaths.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in reportedPaths.EnumerateArray())
            {
                paths.Add(new RemotePath(
                    RequestedPath: entry.GetProperty("requested_path").GetString() ?? string.Empty,
                    Exists: entry.GetProperty("exists").GetBoolean(),
                    IsDirectory: entry.GetProperty("is_dir").GetBoolean(),
                    Executable: entry.GetProperty("executable").GetBoolean(),
                    WritableAncestor: entry.GetProperty("writable_ancestor").GetBoolean()));
            }
        }

        return new RemoteObservation(
            FromProbeReport(profile, fernVersion, report, observedAt), storage, paths);
    }

    /// <summary>The module the profile's interpreter is asked to run.</summary>
    public const string ProbeModule = "fern_stage_runtime.observe_execution";

    /// <summary>
    /// Rust's <c>observed_execution_from_probe</c>: the probe reports what the
    /// interpreter saw, and the caller adds the three facts only it knows —
    /// the schema, the moment, and which program asked. The requested
    /// interpreter path comes from the profile rather than the report, because
    /// the difference between what Fern asked to run and what answered is
    /// exactly what the pair of executable fields exists to show.
    /// </summary>
    static ObservedExecutionFingerprint FromProbeReport(
        ExecutionProfileV5 profile,
        string fernVersion,
        string report,
        DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(report);
        var root = document.RootElement;
        var python = Member(root, "python");
        var platform = Member(root, "platform");
        var torch = Member(root, "torch");
        var cudaAvailable = Bool(root, "cuda_available");
        var devices = new List<ObservedCudaDevice>();
        foreach (var device in Member(root, "cuda_devices").EnumerateArray())
        {
            var capability = device.GetProperty("capability").EnumerateArray().Select(value => value.GetUInt32()).ToList();
            if (capability.Count != 2)
            {
                throw new InvalidDataException("a CUDA device capability must be a major/minor pair");
            }

            devices.Add(new ObservedCudaDevice(
                Index: device.GetProperty("index").GetUInt32(),
                Name: device.GetProperty("name").GetString() ?? throw new InvalidDataException("a CUDA device has no name"),
                Capability: capability,
                TotalMemoryBytes: device.GetProperty("total_memory_bytes").GetUInt64(),
                MultiProcessorCount: device.GetProperty("multi_processor_count").GetUInt32(),
                Uuid: null,
                DriverVersion: null));
        }

        var cudaVisibleDevices = Nullable(root, "cuda_visible_devices");
        AttachKnownNvidiaManagementFacts(devices, cudaVisibleDevices, ManagementFacts(root));

        return new ObservedExecutionFingerprint(
            Schema: Schema,
            ObservedAtUnixMs: (ulong)observedAt.ToUnixTimeMilliseconds(),
            FernVersion: fernVersion,
            Platform: new ObservedPlatform(
                Hostname: Text(platform, "hostname"),
                System: Text(platform, "system"),
                Release: Text(platform, "release"),
                Machine: Text(platform, "machine")),
            Python: new ObservedPythonRuntime(
                RequestedExecutable: profile.Python,
                SysExecutable: Text(python, "sys_executable"),
                Version: Text(python, "version"),
                Implementation: Text(python, "implementation"),
                WorkerModule: Text(python, "worker_module"),
                WorkerModuleAvailable: Bool(python, "worker_module_available")),
            Torch: new ObservedTorchRuntime(
                Version: Text(torch, "version"),
                CudaRuntimeVersion: Nullable(torch, "cuda_runtime_version"),
                CudnnVersion: torch.GetProperty("cudnn_version") is { ValueKind: JsonValueKind.Number } cudnn
                    ? cudnn.GetUInt64()
                    : null,
                DeterministicAlgorithms: Bool(torch, "deterministic_algorithms"),
                Float32MatmulPrecision: Text(torch, "float32_matmul_precision"),
                CudaMatmulAllowTf32: Bool(torch, "cuda_matmul_allow_tf32"),
                CudnnAllowTf32: Bool(torch, "cudnn_allow_tf32"),
                CudnnDeterministic: Bool(torch, "cudnn_deterministic"),
                CudnnBenchmark: Bool(torch, "cudnn_benchmark")),
            CudaAvailable: cudaAvailable,
            CudaCurrentDevice: root.GetProperty("cuda_current_device") is { ValueKind: JsonValueKind.Number } current
                ? current.GetUInt32()
                : null,
            CudaVisibleDevices: cudaVisibleDevices,
            CudaDeviceOrder: Nullable(root, "cuda_device_order"),
            NvidiaVisibleDevices: Nullable(root, "nvidia_visible_devices"),
            CudaDevices: devices);
    }

    /// <summary>
    /// The `nvidia-smi` inventory, as the probe collected it ON THE OBSERVED
    /// MACHINE. Rust ran `nvidia-smi` a second time in the parent for the
    /// local route, which answers about the parent's host; the probe's own
    /// list is the same list locally and the only correct one for a remote
    /// Site, so this uses it for both.
    /// </summary>
    static List<NvidiaManagementFact> ManagementFacts(JsonElement root)
    {
        var facts = new List<NvidiaManagementFact>();
        if (!root.TryGetProperty("nvidia_management_facts", out var reported)
            || reported.ValueKind != JsonValueKind.Array)
        {
            return facts;
        }

        foreach (var fact in reported.EnumerateArray())
        {
            var uuid = fact.TryGetProperty("uuid", out var id) ? id.GetString() : null;
            var driver = fact.TryGetProperty("driver_version", out var version) ? version.GetString() : null;
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(driver))
            {
                facts.Add(new NvidiaManagementFact(uuid, driver));
            }
        }

        return facts;
    }

    /// <summary>
    /// Rust's <c>attach_known_nvidia_management_facts</c>, moved unchanged.
    ///
    /// A logical CUDA index is not a physical card, so a fact is attached only
    /// when the pairing is unambiguous: either CUDA_VISIBLE_DEVICES names this
    /// device by a UUID token that exactly one inventory row bears, or there is
    /// one device and one row and no ambiguity is possible. Anything else
    /// leaves the device without a uuid rather than guessing one.
    /// </summary>
    internal static void AttachKnownNvidiaManagementFacts(
        List<ObservedCudaDevice> devices,
        string? cudaVisibleDevices,
        IReadOnlyList<NvidiaManagementFact> facts)
    {
        var mask = cudaVisibleDevices?.Trim();
        if (string.IsNullOrEmpty(mask))
        {
            mask = null;
        }

        var oneToOneInventory = devices.Count == 1 && facts.Count == 1;
        for (var position = 0; position < devices.Count; position++)
        {
            var device = devices[position];
            var tokens = mask?.Split(',');
            var selector = tokens is not null && device.Index < tokens.Length
                ? tokens[device.Index].Trim()
                : null;
            NvidiaManagementFact? chosen;
            if (selector is not null && selector.StartsWith("GPU-", StringComparison.Ordinal))
            {
                var matches = facts
                    .Where(fact => fact.Uuid == selector || fact.Uuid.StartsWith(selector, StringComparison.Ordinal))
                    .ToList();
                chosen = matches.Count == 1 ? matches[0] : null;
            }
            else if (selector is null && oneToOneInventory)
            {
                chosen = facts[0];
            }
            else
            {
                chosen = null;
            }

            if (chosen is not null)
            {
                devices[position] = device with { Uuid = chosen.Uuid, DriverVersion = chosen.DriverVersion };
            }
        }
    }

    static JsonElement Member(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"the execution probe reported no {name}");

    static string Text(JsonElement owner, string name) =>
        Member(owner, name).GetString() ?? throw new InvalidDataException($"{name} must be text");

    static string? Nullable(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static bool Bool(JsonElement owner, string name) => Member(owner, name).GetBoolean();
}

/// <summary>One declared-vs-observed mismatch that must be resolved before launch.</summary>
public sealed record ExecutionProfileDrift(string Field, string Expected, string Observed);

/// <summary>
/// The one local execution declaration a mutating runner may use, with the
/// facts observed at the moment it was resolved.
/// </summary>
public sealed record ReadyExecution(ExecutionProfileV5 Profile, ObservedExecutionFingerprint Observed);

/// <summary>
/// Resolve the one local execution declaration a mutating runner may use.
///
/// `profile validate` explains the same checks interactively. A runner repeats
/// them at the actual launch boundary because a successful validation from ten
/// minutes ago cannot authorize a later command after a profile or machine has
/// changed.
/// This helper still does not claim a Coordinate, open a run database, or
/// create a worker directory; callers can truthfully report that no GPU work
/// started when it refuses.
///
/// CHG 2026-08-28: ported from fern-cli-rs/src/experiment_front_door.rs
/// `resolve_ready_execution`, which read the same facts out of the full
/// profile-validation report. THREE of that report's gates are not here yet and
/// are named in <see cref="UnportedGates"/> rather than left to be discovered.
/// </summary>
public static class ReadyExecutionGate
{
    /// <summary>
    /// Fern must not start a sweep on a volume below this much free space.
    /// Rust: fern_execution_profile::CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB.
    /// </summary>
    public const double CampaignStorageHardStopFreeGib = 5.0;

    /// <summary>
    /// What `profile validate` still refuses that this gate does not, stated so
    /// an operator is never surprised by a Site this admitted and validation
    /// would have rejected. Each is a bounded piece of work with a named
    /// blocker, not an opinion that the check is unnecessary.
    ///
    /// 1. The local `df` failure branch — Rust refused when a declared root's
    ///    parent could not be inspected at all. Here an uninspectable volume is
    ///    reported as unknown rather than refused. The remote route has no such
    ///    gap: the Site reports its own volumes and a zero-byte answer refuses.
    /// 2. Everything `profile validate` checks that is not about being able to
    ///    execute: the free-space WARNING thresholds (warn_free_gib,
    ///    warn_free_percent)
    ///    and the Codex-sandbox observer rule, neither of which gates `ready`.
    /// </summary>
    public static readonly IReadOnlyList<string> UnportedGates = Array.AsReadOnly(new[]
    {
        "a local declared root whose filesystem cannot be inspected at all",
        "free-space warning thresholds and the sandbox-observer rule, which never gated readiness",
    });

    /// <summary>
    /// Read the profile, observe the machine it names — this host or the
    /// Worker Site at the end of the maintained SSH helper — and refuse unless
    /// that machine can actually execute what the profile declares.
    /// </summary>
    public static SiteResult<ReadyExecution> Resolve(
        string workspaceRoot,
        string profileId,
        string fernVersion)
    {
        var loaded = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(workspaceRoot), profileId, expectedRevision: null);
        if (!loaded.IsOk)
        {
            return loaded.RefusalAs<ReadyExecution>();
        }

        var profile = loaded.Value;
        var knownDataRoot = ApplyDeclaredKnownDataRoot(profile);
        if (knownDataRoot is not null)
        {
            return Refuse(profileId, knownDataRoot);
        }

        return profile.Executor == "ssh"
            ? ResolveRemote(workspaceRoot, profile, fernVersion, RemoteProbeDeadline)
            : ResolveLocal(profile, fernVersion, LocalProbeDeadline);
    }

    /// <summary>
    /// How long the maintained helper is given to carry the read-only probe
    /// and bring its answer back. Rust:
    /// REMOTE_EXECUTION_PROBE_TIMEOUT_SECONDS, and the same number is handed
    /// to the helper as RUNPOD_COMMAND_TIMEOUT_SECONDS, so the two ends agree
    /// on when to give up rather than one waiting for the other.
    /// </summary>
    public static readonly TimeSpan RemoteProbeDeadline = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long the local observation may take. Rust bounded this at nothing
    /// at all; a bound is better, but it must not be able to refuse a machine
    /// that is merely slow — correction 310, a deadline is not permission to
    /// kill healthy work. Measured 2026-08-28 on the 4070: a warm probe takes
    /// about eleven seconds, and one cold CUDA initialisation on a loaded
    /// machine took over two minutes and would have failed a two-minute bound.
    /// Ten minutes cannot plausibly be a healthy import and still ends.
    /// </summary>
    public static readonly TimeSpan LocalProbeDeadline = TimeSpan.FromMinutes(10);

    static SiteResult<ReadyExecution> ResolveLocal(
        ExecutionProfileV5 profile,
        string fernVersion,
        TimeSpan deadline)
    {
        var observed = ObservedExecutionProbe.Observe(profile, fernVersion, deadline);
        if (!observed.IsOk)
        {
            return Refuse(profile.ProfileId, observed.Failure.Detail + ".");
        }

        var drift = DriftFrom(observed.Value, profile);
        if (drift.Count > 0)
        {
            return RefuseDrift(profile.ProfileId, drift, "execution profile drift");
        }

        var blocked = StorageHardStop(profile);
        if (blocked is not null)
        {
            return Refuse(profile.ProfileId, blocked);
        }

        return SiteResult<ReadyExecution>.Ok(new ReadyExecution(profile, observed.Value));
    }

    /// <summary>
    /// The SSH half of Rust's <c>inspect_remote_profile</c>: one read-only
    /// observation through the maintained helper, then the same drift rules
    /// the local route applies plus the two questions only the Site can
    /// answer. A transport failure is reported as remote state unknown and
    /// never as a Python, CUDA, source, or scientific failure.
    /// </summary>
    static SiteResult<ReadyExecution> ResolveRemote(
        string workspaceRoot,
        ExecutionProfileV5 profile,
        string fernVersion,
        TimeSpan deadline)
    {
        var helper = StagePackageTransport.MaintainedSshHelper(WorktreeControlPaths.SshHelper(workspaceRoot));
        if (!helper.IsOk)
        {
            return Refuse(profile.ProfileId, helper.Failure.Detail + ".");
        }

        var observed = ObservedExecutionProbe.ObserveRemote(
            profile, helper.Value, workspaceRoot, fernVersion, deadline);
        if (!observed.IsOk)
        {
            return Refuse(profile.ProfileId, observed.Failure.Detail);
        }

        var drift = DriftFrom(observed.Value.Fingerprint, profile);
        if (drift.Count > 0)
        {
            return RefuseDrift(profile.ProfileId, drift, "remote execution profile drift");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var volume in observed.Value.Storage)
        {
            if (volume.TotalBytes == 0)
            {
                return Refuse(profile.ProfileId,
                    $"remote storage probe returned zero total bytes for {volume.RequestedPath}.");
            }

            if (!seen.Add(volume.Filesystem))
            {
                continue;
            }

            var availableGib = volume.AvailableBytes / (1024.0 * 1024.0 * 1024.0);
            if (availableGib < CampaignStorageHardStopFreeGib)
            {
                return Refuse(profile.ProfileId, string.Format(
                    CultureInfo.InvariantCulture,
                    "remote filesystem {0} has only {1:F1} GiB / {2:F1}% free; Fern must not start a sweep below {3} GiB.",
                    volume.Filesystem,
                    availableGib,
                    100.0 * volume.AvailableBytes / volume.TotalBytes,
                    CampaignStorageHardStopFreeGib));
            }
        }

        foreach (var path in observed.Value.Paths)
        {
            var unusable = RemotePathProblem(profile, path);
            if (unusable is not null)
            {
                return Refuse(profile.ProfileId, unusable);
            }
        }

        return SiteResult<ReadyExecution>.Ok(new ReadyExecution(profile, observed.Value.Fingerprint));
    }

    /// <summary>
    /// What each declared remote path has to be, which is not the same
    /// question for all three: the runner must be an executable file, the
    /// recorded-data root must be a directory that is already there, and a
    /// root Fern will CREATE only needs an existing ancestor it may write to.
    /// </summary>
    internal static string? RemotePathProblem(ExecutionProfileV5 profile, RemotePath path)
    {
        if (profile.KnownDataRoot is { } known && path.RequestedPath == known)
        {
            return path.Exists && path.IsDirectory
                ? null
                : $"the remote recorded-data root {path.RequestedPath} is absent or is not a directory.";
        }

        return path.WritableAncestor
            ? null
            : $"the remote path {path.RequestedPath} has no writable existing ancestor.";
    }

    /// <summary>
    /// Make the profile's declared known-data root the one that actually gets
    /// read, and refuse when the shell already says something else.
    ///
    /// The field existed and nothing consumed it. serie resolves recorded
    /// assets from SERIE_KNOWN_ROOT or ~/.cache/serie/known, so a profile could
    /// declare /workspace/known, be validated as ready, and then have every
    /// recorded cell look somewhere else entirely. A declaration nothing reads
    /// is worse than no declaration: it is believed.
    ///
    /// Omitting the variable resolves to the profile. Setting it to the SAME
    /// directory is accepted, because a script that exports it is not wrong.
    /// Setting it to a DIFFERENT one fails closed rather than silently picking
    /// a winner — two sources of truth for where the data lives is the shape
    /// this repairs. Returns the refusal, or null when the environment agrees.
    /// </summary>
    static string? ApplyDeclaredKnownDataRoot(ExecutionProfileV5 profile)
    {
        if (profile.KnownDataRoot is not { } declared)
        {
            return null;
        }

        var existing = Environment.GetEnvironmentVariable(SerieKnownRootVariable);
        if (existing is null)
        {
            Environment.SetEnvironmentVariable(SerieKnownRootVariable, declared);
            return null;
        }

        return Path.TrimEndingDirectorySeparator(existing) == Path.TrimEndingDirectorySeparator(declared)
            ? null
            : $"{SerieKnownRootVariable}={existing} disagrees with the execution profile, which declares "
              + $"{declared}. Unset it to use the profile, or change the profile deliberately.";
    }

    const string SerieKnownRootVariable = "SERIE_KNOWN_ROOT";

    /// <summary>Rust's refusal sentence, whose last clause is the one that
    /// matters to whoever is reading it at 2am.</summary>
    static SiteResult<ReadyExecution> Refuse(string profileId, string detail) =>
        SiteResult<ReadyExecution>.Permanent(
            $"cannot start on profile \"{profileId}\": {detail} No GPU work started.");

    static SiteResult<ReadyExecution> RefuseDrift(
        string profileId,
        IReadOnlyList<ExecutionProfileDrift> drift,
        string label)
    {
        var lines = string.Join("\n", drift.Select(item =>
            $"- {label} at {item.Field}: expected {item.Expected}; observed {item.Observed}"));
        return SiteResult<ReadyExecution>.Permanent(
            $"cannot start on profile \"{profileId}\":\n{lines}\nNo GPU work started.");
    }

    /// <summary>
    /// Rust's <c>ObservedExecutionFingerprint::drift_from</c>, moved unchanged:
    /// every way the machine can fail to be the machine the profile declares.
    /// The CUDA branch returns early twice, because once CUDA is missing or no
    /// device is selected there is nothing left to compare a requirement
    /// against.
    /// </summary>
    public static IReadOnlyList<ExecutionProfileDrift> DriftFrom(
        ObservedExecutionFingerprint observed,
        ExecutionProfileV5 profile)
    {
        var drift = new List<ExecutionProfileDrift>();
        if (observed.Python.RequestedExecutable != profile.Python)
        {
            drift.Add(new ExecutionProfileDrift("python", profile.Python, observed.Python.RequestedExecutable));
        }

        if (observed.Python.WorkerModule != profile.WorkerModule || !observed.Python.WorkerModuleAvailable)
        {
            drift.Add(new ExecutionProfileDrift(
                "worker.module",
                $"{profile.WorkerModule} (importable)",
                $"{observed.Python.WorkerModule} (importable={Lowercase(observed.Python.WorkerModuleAvailable)})"));
        }

        if (profile.Device != "cuda")
        {
            return drift;
        }

        if (!observed.CudaAvailable)
        {
            drift.Add(new ExecutionProfileDrift("device", "cuda available", "cuda unavailable"));
            return drift;
        }

        var selected = SelectedDevice(observed);
        if (selected is null || observed.CudaCurrentDevice is null)
        {
            drift.Add(new ExecutionProfileDrift(
                "accelerator",
                "one selected CUDA device",
                "CUDA reported available but no selected device was described"));
            return drift;
        }

        if (profile.AcceleratorMinComputeCapability is { } required
            && (selected.Capability[0] < required[0]
                || (selected.Capability[0] == required[0] && selected.Capability[1] < required[1])))
        {
            drift.Add(new ExecutionProfileDrift(
                "accelerator.compute_capability",
                $"at least {required[0]}.{required[1]}",
                $"{selected.Capability[0]}.{selected.Capability[1]}"));
        }

        if (profile.AcceleratorMinMemoryGib is { } minimumGib)
        {
            var requiredBytes = minimumGib > ulong.MaxValue / 1_073_741_824UL
                ? ulong.MaxValue
                : minimumGib * 1_073_741_824UL;
            if (selected.TotalMemoryBytes < requiredBytes)
            {
                drift.Add(new ExecutionProfileDrift(
                    "accelerator.memory",
                    $"at least {minimumGib} GiB",
                    $"{selected.TotalMemoryBytes} bytes"));
            }
        }

        if (profile.AcceleratorRequiredDeviceUuid is { } requiredUuid && selected.Uuid != requiredUuid)
        {
            drift.Add(new ExecutionProfileDrift(
                "accelerator.device_uuid", requiredUuid, selected.Uuid ?? "unavailable"));
        }

        return drift;
    }

    /// <summary>
    /// The CUDA device a run would actually use: the one CUDA selected, or the
    /// first visible one when nothing is selected. Rust's
    /// <c>observed_site_capability</c> and <c>drift_from</c> both pick this
    /// way, so a capability row and a drift refusal always describe one card.
    /// </summary>
    public static ObservedCudaDevice? SelectedDevice(ObservedExecutionFingerprint observed) =>
        observed.CudaDevices.FirstOrDefault(device => device.Index == observed.CudaCurrentDevice)
        ?? observed.CudaDevices.FirstOrDefault();

    /// <summary>
    /// Refuse when any volume a campaign writes to is below the hard stop.
    ///
    /// The reading, the `df -Pk` parse and the thresholds all belong to
    /// <see cref="StorageProbe"/> (csharp/Fern.Scheduler/Preflight.cs), which
    /// is the one owner of what a volume's numbers MEAN — `profile validate`
    /// renders the same readings and must not be able to disagree with what
    /// registration refuses. This is only the campaign's use of them: the three
    /// declared roots, deduplicated by filesystem so one full disk is reported once
    /// however many roots live on it.
    /// </summary>
    static string? StorageHardStop(ExecutionProfileV5 profile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { profile.CampaignRoot, profile.ArtifactRoot, profile.WorkRoot })
        {
            if (StorageProbe.Inspect(root).Check is not { } check || !seen.Add(check.Filesystem))
            {
                continue;
            }

            if (check.HardStop)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} has only {1:F1} GiB / {2:F1}% free; Fern must not start a sweep below {3} GiB.",
                    check.Filesystem, check.AvailableGib, check.FreePercent, CampaignStorageHardStopFreeGib);
            }
        }

        return null;
    }

    /// <summary>
    /// Reduce the launch-time observation to the exact capability Registry
    /// Home records for a Worker Site. Registration and launch refresh share
    /// this one mapping so a profile edit cannot be interpreted differently by
    /// two callers. Rust: <c>observed_site_capability</c>.
    /// </summary>
    public static SiteCapabilityFacts CapabilityOf(ReadyExecution ready)
    {
        var observed = ready.Observed;
        var selected = SelectedDevice(observed);
        return new SiteCapabilityFacts(
            Device: ready.Profile.Device,
            Hostname: observed.Platform.Hostname,
            CudaAvailable: observed.CudaAvailable,
            CudaDeviceName: selected?.Name,
            CudaComputeCapability: selected is null ? null : $"{selected.Capability[0]}.{selected.Capability[1]}",
            CudaTotalMemoryBytes: selected is null || selected.TotalMemoryBytes > long.MaxValue
                ? null
                : (long)selected.TotalMemoryBytes,
            PythonVersion: observed.Python.Version,
            PythonExecutable: observed.Python.SysExecutable,
            TorchVersion: observed.Torch.Version,
            WorkerBinarySha256: null,
            SourceRoot: ready.Profile.SourceRoot,
            ObservedEnvironmentDigest: ObservedExecutionProbe.EnvironmentContentDigest(observed),
            ObservedAtMs: (long)observed.ObservedAtUnixMs,
            ObservedFingerprintJson: ObservedExecutionProbe.Render(observed));
    }

    /// <summary>Rust's <c>{bool}</c> renders lowercase; C#'s renders capitalised.</summary>
    static string Lowercase(bool value) => value ? "true" : "false";
}
