using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fern.Registry;
using Fern.Site;

namespace Fern.Scheduler;

/// <summary>The two interpreter environments required by today's foundation-model adapters.</summary>
public enum ModelInterpreterEnvironment
{
    DefaultChronos,
    ZeusFlashAttention,
}

public sealed record PinnedModelAsset(string Model, string Repository, string Revision);

public sealed record ModelEnvironmentInstallationPlan(
    ModelInterpreterEnvironment Environment,
    string Interpreter,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> InterpreterBootstrapArgv,
    IReadOnlyList<string> UvArgv,
    IReadOnlyList<string> RequiredImports,
    IReadOnlyList<string> RequiredPackages,
    IReadOnlyList<PinnedModelAsset> Assets);

/// <summary>
/// Resolve the one package mutation a Site model environment needs.
///
/// This is deliberately a closed catalog. The command boundary accepts model
/// strings, validates them immediately, and produces one complete argv whose
/// first three words are always <c>uv pip install</c>. There is no sync route.
/// </summary>
public static class ModelEnvironmentInstallation
{
    public const string RequiredPythonAbi = "cpython-3.12";
    public const string InstallAttemptProcessMarker = "FERN_ENVIRONMENT_INSTALL_ATTEMPT_ID";
    public const string ProcessObservationSchema = "fern.environment_install_lease_process_observation.v1";
    public const string ProcessObserver = "linux_proc_environ_v1";

    const string ChronosPackage = "chronos-forecasting==2.3.1";

    // CHG 2026-08-31: torch is pinned as one immutable wheel URL, the same
    // shape as the flash-attn wheel below, instead of adding the whole
    // PyTorch cu130 index with `--index-strategy unsafe-best-match`. That
    // strategy relaxed dependency-confusion protection for every package in
    // the resolution, not just torch. With the exact wheel, the CUDA build
    // comes from its one trusted URL, and everything else -- including
    // torch's nvidia-*-cu13 dependencies, which PyPI carries -- resolves from
    // PyPI under uv's default strict index strategy.
    const string TorchCudaWheel =
        "https://download.pytorch.org/whl/cu130/"
        + "torch-2.9.1%2Bcu130-cp312-cp312-manylinux_2_28_x86_64.whl";
    const string ZeusFlashAttentionWheel =
        "https://github.com/Dao-AILab/flash-attention/releases/download/v2.8.3.post1/"
        + "flash_attn-2.8.3%2Bcu13torch2.9cxx11abiTRUE-cp312-cp312-linux_x86_64.whl";

    static readonly IReadOnlyDictionary<string, (ModelInterpreterEnvironment Environment, PinnedModelAsset Asset)>
        Catalog = new Dictionary<string, (ModelInterpreterEnvironment, PinnedModelAsset)>(StringComparer.Ordinal)
        {
            ["chronos_bolt_small"] = (
                ModelInterpreterEnvironment.DefaultChronos,
                new("chronos_bolt_small", "amazon/chronos-bolt-small", "772f3d25d38aec6d914c8949dab4462e2d46f5d8")),
            ["chronos_bolt_base"] = (
                ModelInterpreterEnvironment.DefaultChronos,
                new("chronos_bolt_base", "amazon/chronos-bolt-base", "5d9f166d69f47aef3401367a7b842e78fe97b121")),
            ["chronos2"] = (
                ModelInterpreterEnvironment.DefaultChronos,
                new("chronos2", "amazon/chronos-2", "29ec3766d36d6f73f0696f85560a422f50e8498c")),
            ["zeus"] = (
                ModelInterpreterEnvironment.ZeusFlashAttention,
                new("zeus", "GestaltCog/zeus", "4b458a2ec89b30c114da99d0837e33e4343070f3")),
        };

    public static SiteResult<ModelEnvironmentInstallationPlan> Resolve(
        IEnumerable<string> requestedModels,
        string interpreter)
    {
        if (string.IsNullOrWhiteSpace(interpreter))
        {
            return SiteResult<ModelEnvironmentInstallationPlan>.Permanent(
                "model-environment installation requires the profile's exact interpreter path");
        }

        var models = requestedModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.Ordinal)
            .ToArray();
        if (models.Length == 0)
        {
            return SiteResult<ModelEnvironmentInstallationPlan>.Permanent(
                "model-environment installation requires at least one --model");
        }

        var unknown = models.Where(model => !Catalog.ContainsKey(model)).ToArray();
        if (unknown.Length != 0)
        {
            return SiteResult<ModelEnvironmentInstallationPlan>.Permanent(
                "unknown installable model(s): " + string.Join(", ", unknown)
                + "; expected chronos_bolt_small, chronos_bolt_base, chronos2, or zeus");
        }

        var environments = models.Select(model => Catalog[model].Environment).Distinct().ToArray();
        if (environments.Length != 1)
        {
            return SiteResult<ModelEnvironmentInstallationPlan>.Permanent(
                "one installation request names models that require two interpreter environments; "
                + "install ordinary Chronos and zeus_flash_attention through separate profiles");
        }

        var environment = environments[0];
        var zeusEnvironmentRoot = environment == ModelInterpreterEnvironment.ZeusFlashAttention
            ? VirtualEnvironmentRoot(interpreter)
            : null;
        if (environment == ModelInterpreterEnvironment.ZeusFlashAttention && zeusEnvironmentRoot is null)
        {
            return SiteResult<ModelEnvironmentInstallationPlan>.Permanent(
                "zeus installation requires a profile interpreter shaped as an absolute <venv>/bin/python path");
        }
        var interpreterBootstrapArgv = environment switch
        {
            ModelInterpreterEnvironment.DefaultChronos => Array.Empty<string>(),
            ModelInterpreterEnvironment.ZeusFlashAttention => new[]
            {
                "uv", "venv", "--allow-existing", "--python", "3.12", "--no-progress", zeusEnvironmentRoot!,
            },
            _ => throw new InvalidOperationException($"unsupported model environment {environment}"),
        };
        var argv = environment switch
        {
            ModelInterpreterEnvironment.DefaultChronos => new[]
            {
                "uv", "pip", "install", "--python", interpreter, ChronosPackage,
            },
            ModelInterpreterEnvironment.ZeusFlashAttention => new[]
            {
                "uv", "pip", "install", "--python", interpreter,
                "--no-progress",
                TorchCudaWheel, "transformers==4.40.1", "basicts==1.1.0",
                "attrs==26.1.0", "einops==0.8.2", "numpy==2.5.1", "polars==1.43.2",
                "rich==15.0.0", "arrow==1.4.0", "beartype==0.22.9", "colored==2.3.2",
                "jaxtyping==0.3.11", "matplotlib==3.11.1", "pydantic==2.13.4",
                "safetensors==0.8.0", "scikit-learn==1.9.0", "scipy==1.18.0",
                "hf-transfer==0.1.9",
                ZeusFlashAttentionWheel,
            },
            _ => throw new InvalidOperationException($"unsupported model environment {environment}"),
        };
        var imports = environment switch
        {
            ModelInterpreterEnvironment.DefaultChronos => new[] { "chronos", "torch", "transformers" },
            ModelInterpreterEnvironment.ZeusFlashAttention => new[]
            {
                "torch", "flash_attn", "transformers", "basicts",
            },
            _ => throw new InvalidOperationException($"unsupported model environment {environment}"),
        };
        var packages = environment switch
        {
            ModelInterpreterEnvironment.DefaultChronos => new[]
            {
                "chronos-forecasting", "torch", "transformers", "huggingface-hub",
            },
            ModelInterpreterEnvironment.ZeusFlashAttention => new[]
            {
                "torch", "flash-attn", "transformers", "basicts", "attrs", "einops", "numpy", "polars",
                "rich", "arrow", "beartype", "colored", "jaxtyping", "matplotlib", "pydantic",
                "safetensors", "scikit-learn", "scipy", "huggingface-hub", "hf-transfer",
            },
            _ => throw new InvalidOperationException($"unsupported model environment {environment}"),
        };

        return SiteResult<ModelEnvironmentInstallationPlan>.Ok(new(
            environment,
            interpreter,
            models,
            interpreterBootstrapArgv,
            argv,
            imports,
            packages,
            models.Select(model => Catalog[model].Asset).ToArray()));
    }

    public static bool IsCatalogModel(string model) => Catalog.ContainsKey(model);

    static string? VirtualEnvironmentRoot(string interpreter)
    {
        var bin = Path.GetDirectoryName(interpreter);
        var root = bin is null ? null : Path.GetDirectoryName(bin);
        if (string.IsNullOrWhiteSpace(root)
            || Path.GetFileName(bin) != "bin"
            || Path.GetFileName(interpreter) is not ("python" or "python3"))
        {
            return null;
        }
        return root;
    }

    /// <summary>
    /// The SSH helper merges remote stderr into stdout. Probe phase lines
    /// therefore sit in front of the checked JSON object. Admission reads the
    /// last complete JSON object line, not the whole stream.
    /// </summary>
    public static string? LastJsonObjectLine(string output)
    {
        string? found = null;
        var remaining = output.AsSpan();
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            ReadOnlySpan<char> line;
            if (newline < 0)
            {
                line = remaining;
                remaining = [];
            }
            else
            {
                line = remaining[..newline];
                remaining = remaining[(newline + 1)..];
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            line = line.Trim();
            if (line.Length >= 2 && line[0] == '{' && line[^1] == '}')
            {
                found = line.ToString();
            }
        }

        return found;
    }

    /// <summary>
    /// The mutation scope of an asset-only repair (Receipt axis R15): when
    /// the interpreter and packages already satisfy policy and only the model
    /// snapshot is missing or incomplete, the authorized mutation is exactly
    /// the shared model cache -- narrower than an install, and the receipt
    /// says so.
    /// </summary>
    public static IReadOnlyList<string> AssetRepairMutationRoots() => ["model-cache:huggingface"];

    /// <summary>
    /// The last <c>model-environment phase=NAME</c> marker in a probe's
    /// output. The probe prints one marker per phase to stderr, so the last
    /// marker in a FAILED run names the phase that raised -- which is how the
    /// caller distinguishes "packages are broken" from "only the checkpoint
    /// snapshot is missing" without parsing Python tracebacks.
    /// </summary>
    public static string? LastReportedPhase(string output)
    {
        const string marker = "model-environment phase=";
        string? phase = null;
        var index = 0;
        while (true)
        {
            var found = output.IndexOf(marker, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return phase;
            }
            var start = found + marker.Length;
            var end = start;
            while (end < output.Length && !char.IsWhiteSpace(output[end]))
            {
                end++;
            }
            if (end > start)
            {
                phase = output[start..end];
            }
            index = end;
        }
    }

    /// <summary>
    /// Named mutation roots this Attempt may write. Hugging Face and uv caches
    /// are Site-shared, not Chronos-private. The Registry lease owns these
    /// names; <c>uv</c> does not honor a fencing token.
    /// </summary>
    public static IReadOnlyList<string> MutationRoots(string interpreter)
    {
        var environment = VirtualEnvironmentRoot(interpreter) ?? interpreter;
        return
        [
            "environment:" + environment,
            "model-cache:huggingface",
            "package-cache:uv",
        ];
    }

    public sealed record EnvironmentInstallProcessMatch(
        long Pid,
        long? ProcessStartTicks,
        string CommandLine);

    public sealed record EnvironmentInstallProcessScan(
        string Schema,
        string OwnerAttemptId,
        IReadOnlyList<EnvironmentInstallProcessMatch> LiveMatches);

    public static SiteResult<EnvironmentInstallProcessScan> ScanLocalInstallAttemptProcesses(
        string ownerAttemptId)
    {
        if (string.IsNullOrWhiteSpace(ownerAttemptId))
        {
            return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                "environment install process scan requires the owner attempt id");
        }

        var matches = new List<EnvironmentInstallProcessMatch>();
        if (!Directory.Exists("/proc"))
        {
            return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                "environment install process scan requires Linux /proc");
        }

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!long.TryParse(Path.GetFileName(processDirectory), out var pid))
            {
                continue;
            }

            byte[] environ;
            try
            {
                environ = File.ReadAllBytes(Path.Combine(processDirectory, "environ"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!EnvironContains(environ, InstallAttemptProcessMarker, ownerAttemptId))
            {
                continue;
            }

            matches.Add(new EnvironmentInstallProcessMatch(
                pid,
                ResidentPythonWorker.ReadProcessStartTicks(pid),
                ReadCommandLine(processDirectory)));
        }

        return SiteResult<EnvironmentInstallProcessScan>.Ok(new(
            ProcessObservationSchema,
            ownerAttemptId,
            matches.OrderBy(match => match.Pid).ToArray()));
    }

    public static SiteResult<EnvironmentInstallProcessScan> ParseInstallAttemptProcessScan(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                    "environment install process scan is not a JSON object");
            }

            var schema = RequiredText(root, "schema", "environment install process scan");
            if (schema != ProcessObservationSchema)
            {
                return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                    $"environment install process scan schema {schema} is not {ProcessObservationSchema}");
            }

            var ownerAttemptId = RequiredText(root, "owner_attempt_id", "environment install process scan");
            if (!root.TryGetProperty("live_matches", out var liveMatches)
                || liveMatches.ValueKind != JsonValueKind.Array)
            {
                return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                    "environment install process scan is missing live_matches array");
            }

            var matches = new List<EnvironmentInstallProcessMatch>();
            foreach (var match in liveMatches.EnumerateArray())
            {
                if (match.ValueKind != JsonValueKind.Object)
                {
                    return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                        "environment install process scan live_matches contains a non-object entry");
                }

                var pid = match.TryGetProperty("pid", out var pidElement)
                    && pidElement.TryGetInt64(out var parsedPid)
                        ? parsedPid
                        : throw new InvalidDataException("environment install process scan live match is missing pid");
                long? ticks = null;
                if (match.TryGetProperty("process_start_ticks", out var tickElement)
                    && tickElement.ValueKind != JsonValueKind.Null)
                {
                    ticks = tickElement.TryGetInt64(out var parsedTicks)
                        ? parsedTicks
                        : throw new InvalidDataException("environment install process scan live match has invalid process_start_ticks");
                }
                var commandLine = match.TryGetProperty("command_line", out var command)
                    && command.ValueKind == JsonValueKind.String
                        ? command.GetString() ?? ""
                        : "";
                matches.Add(new EnvironmentInstallProcessMatch(pid, ticks, commandLine));
            }

            return SiteResult<EnvironmentInstallProcessScan>.Ok(new(schema, ownerAttemptId, matches));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidDataException)
        {
            return SiteResult<EnvironmentInstallProcessScan>.Permanent(
                "environment install process scan is unreadable: " + error.Message);
        }
    }

    public static SiteResult<string> BuildInstallLeaseProcessAbsenceReceipt(
        EnvironmentInstallLease lease,
        ExecutionProfileV5 profile,
        string observerActor,
        EnvironmentInstallProcessScan scan)
    {
        if (string.IsNullOrWhiteSpace(observerActor))
        {
            return SiteResult<string>.Permanent(
                "environment install lease reconciliation requires an observer actor");
        }
        if (scan.Schema != ProcessObservationSchema)
        {
            return SiteResult<string>.Permanent(
                $"environment install process scan schema {scan.Schema} is not {ProcessObservationSchema}");
        }
        if (scan.OwnerAttemptId != lease.OwnerAttemptId)
        {
            return SiteResult<string>.Permanent(
                $"environment install process scan observed {scan.OwnerAttemptId}, not lease owner {lease.OwnerAttemptId}");
        }
        if (scan.LiveMatches.Count != 0)
        {
            return SiteResult<string>.Permanent(
                $"environment install lease {lease.LeaseId} owner {lease.OwnerAttemptId} still has live process(es): "
                + string.Join(", ", scan.LiveMatches.Select(match =>
                    $"pid {match.Pid} ticks {match.ProcessStartTicks?.ToString() ?? "unknown"} {match.CommandLine}")));
        }
        if (lease.SiteId != profile.ProfileId)
        {
            return SiteResult<string>.Permanent(
                $"environment install lease {lease.LeaseId} belongs to Site {lease.SiteId}, not profile {profile.ProfileId}");
        }

        var receipt = JsonSerializer.Serialize(new
        {
            schema = ProcessObservationSchema,
            lease_id = lease.LeaseId,
            site_id = lease.SiteId,
            machine_generation = lease.MachineGeneration,
            interpreter = lease.Interpreter,
            mutation_roots = lease.MutationRoots,
            owner_attempt_id = lease.OwnerAttemptId,
            observer_actor = observerActor,
            executor = profile.Executor,
            endpoint_session_id = ExecutionPlacementIdentity.EndpointSession(profile.Executor, profile.SshDestination),
            process_observer = ProcessObserver,
            process_marker_name = InstallAttemptProcessMarker,
            process_marker_value = lease.OwnerAttemptId,
            observed_outcome = "owner_process_absent",
            live_matches = Array.Empty<object>(),
        });
        return SiteResult<string>.Ok(receipt);
    }

    public static string InstallAttemptProcessScanPython =>
        $$"""
        import json
        import os
        import sys

        marker = "{{InstallAttemptProcessMarker}}=".encode("utf-8") + sys.argv[1].encode("utf-8")
        matches = []
        for name in os.listdir("/proc"):
            if not name.isdigit():
                continue
            root = "/proc/" + name
            try:
                with open(root + "/environ", "rb") as handle:
                    environ = handle.read()
            except OSError:
                continue
            if marker not in environ.split(b"\0"):
                continue
            start_ticks = None
            try:
                with open(root + "/stat", "r", encoding="utf-8", errors="replace") as handle:
                    stat = handle.read()
                close = stat.rfind(")")
                fields = stat[close + 2:].split()
                if len(fields) > 19:
                    start_ticks = int(fields[19])
            except Exception:
                start_ticks = None
            command_line = ""
            try:
                with open(root + "/cmdline", "rb") as handle:
                    command_line = handle.read().replace(b"\0", b" ").decode("utf-8", "replace")[:240]
            except OSError:
                command_line = ""
            matches.append({
                "pid": int(name),
                "process_start_ticks": start_ticks,
                "command_line": command_line,
            })
        print(json.dumps({
            "schema": "{{ProcessObservationSchema}}",
            "owner_attempt_id": sys.argv[1],
            "live_matches": sorted(matches, key=lambda item: item["pid"]),
        }, sort_keys=True, separators=(",", ":")))
        """;

    static bool EnvironContains(byte[] environ, string name, string value)
    {
        var expected = Encoding.UTF8.GetBytes(name + "=" + value);
        var start = 0;
        for (var index = 0; index <= environ.Length; index++)
        {
            if (index < environ.Length && environ[index] != 0)
            {
                continue;
            }

            var length = index - start;
            if (length == expected.Length
                && environ.AsSpan(start, length).SequenceEqual(expected))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    static string ReadCommandLine(string processDirectory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(processDirectory, "cmdline"));
            var value = Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
            return value.Length <= 240 ? value : value[..240];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static string RequiredText(JsonElement root, string propertyName, string context)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context} is missing {propertyName}");
        }
        return value.GetString()!;
    }

    /// <summary>
    /// Policy identity for one environment capability: the consumed
    /// MachineAdmission identity, machine generation, CPython ABI, interpreter,
    /// required catalog packages, and pinned assets. Site name, SSH endpoint,
    /// profile revision, and extra pip packages are not identity-bearing.
    /// </summary>
    public static string ReceiptIdentity(
        string machineAdmissionIdentity,
        string machineGeneration,
        string pythonAbi,
        ModelEnvironmentInstallationPlan plan,
        IReadOnlyDictionary<string, string> installedPackages)
    {
        if (string.IsNullOrWhiteSpace(machineAdmissionIdentity)
            || !machineAdmissionIdentity.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException("environment identity requires a consumed MachineAdmission identity");
        }

        var material = new StringBuilder("fern.model_environment_installation.identity.v3");
        foreach (var value in new[]
                 {
                     machineAdmissionIdentity,
                     machineGeneration,
                     pythonAbi,
                     plan.Environment.ToString(),
                     plan.Interpreter,
                 })
        {
            material.Append('\u001f').Append(value);
        }
        foreach (var model in plan.Models) material.Append('\u001f').Append("model=").Append(model);
        foreach (var name in plan.RequiredPackages.OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!installedPackages.TryGetValue(name, out var version))
            {
                continue;
            }

            material.Append('\u001f').Append("package=").Append(name).Append("==").Append(version);
        }
        foreach (var asset in plan.Assets.OrderBy(asset => asset.Model, StringComparer.Ordinal))
        {
            material.Append('\u001f').Append("asset=").Append(asset.Repository).Append('@').Append(asset.Revision);
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    /// <summary>
    /// Compare a live observation of a supposedly ready environment against
    /// the admission the Registry already holds for the Site. Every returned
    /// sentence is one named drift; an empty list means the environment is the
    /// admitted one. The receipt identity deliberately excludes the exact
    /// Python patch and the per-file asset manifest, so those are compared
    /// here as receipt data: a drifted-but-importable environment must be
    /// refused, never silently re-admitted as already_ready.
    /// </summary>
    /// <summary>
    /// The typed comparison verdict: every named drift sentence, the asset
    /// snapshot keys (repository@revision) whose files drifted, and whether
    /// any drift is NOT an asset-file drift. The split exists because the two
    /// halves have different remedies: package or interpreter drift needs a
    /// deliberate reinstall, while a drifted snapshot can be re-materialized
    /// under an asset-scoped lease when the operator explicitly asks.
    /// </summary>
    public sealed record EnvironmentDrift(
        IReadOnlyList<string> Sentences,
        IReadOnlyList<string> DriftedAssetKeys,
        bool HasNonAssetDrift);

    public static EnvironmentDrift DriftAgainstAdmission(
        string recordedReceiptJson,
        string observedEnvironmentIdentity,
        IReadOnlyDictionary<string, string> observedPackages,
        string observedPythonVersion,
        JsonElement? observedAssetManifest)
    {
        var drift = new List<string>();
        var driftedAssets = new List<string>();
        var nonAssetDrift = 0;
        JsonDocument recorded;
        try
        {
            recorded = JsonDocument.Parse(recordedReceiptJson);
        }
        catch (JsonException error)
        {
            return new EnvironmentDrift(
                [$"the recorded admission receipt is unreadable: {error.Message}"], [], true);
        }

        using (recorded)
        {
            var root = recorded.RootElement;
            if (root.TryGetProperty("packages", out var recordedPackages)
                && recordedPackages.ValueKind == JsonValueKind.Object)
            {
                foreach (var package in recordedPackages.EnumerateObject())
                {
                    var admittedVersion = package.Value.GetString() ?? "";
                    if (!observedPackages.TryGetValue(package.Name, out var observedVersion))
                    {
                        drift.Add($"package {package.Name} is admitted as {admittedVersion} but was not observed");
                        nonAssetDrift++;
                    }
                    else if (!string.Equals(admittedVersion, observedVersion, StringComparison.Ordinal))
                    {
                        drift.Add($"package {package.Name} drifted: admitted {admittedVersion}, observed {observedVersion}");
                        nonAssetDrift++;
                    }
                }
            }

            if (root.TryGetProperty("python_version", out var recordedPython)
                && recordedPython.GetString() is { Length: > 0 } admittedPython
                && !string.Equals(admittedPython, observedPythonVersion, StringComparison.Ordinal))
            {
                drift.Add(
                    $"interpreter version drifted: admitted {admittedPython}, observed {observedPythonVersion}; "
                    + "the admitted patch is the policy, not whatever is installed");
                nonAssetDrift++;
            }

            // Older receipts predate the manifest; only compare when both
            // sides carry one. A missing or truncated checkpoint shard shows
            // up here as a missing file or a changed size/blob.
            if (root.TryGetProperty("asset_manifest", out var recordedManifest)
                && recordedManifest.ValueKind == JsonValueKind.Object
                && observedAssetManifest is { ValueKind: JsonValueKind.Object } observedManifest)
            {
                foreach (var asset in recordedManifest.EnumerateObject())
                {
                    if (!observedManifest.TryGetProperty(asset.Name, out var observedFiles))
                    {
                        drift.Add($"asset {asset.Name} is admitted but its snapshot was not observed");
                        driftedAssets.Add(asset.Name);
                        continue;
                    }

                    var admitted = ManifestFilesByName(asset.Value);
                    var observed = ManifestFilesByName(observedFiles);
                    var assetSentencesBefore = drift.Count;
                    foreach (var (file, fact) in admitted)
                    {
                        if (!observed.TryGetValue(file, out var observedFact))
                        {
                            drift.Add($"asset {asset.Name} file {file} is admitted ({fact.Size} bytes) but missing");
                        }
                        else if (fact.Size != observedFact.Size
                                 || !string.Equals(fact.Blob, observedFact.Blob, StringComparison.Ordinal))
                        {
                            drift.Add(
                                $"asset {asset.Name} file {file} drifted: admitted {fact.Size} bytes blob {fact.Blob}, "
                                + $"observed {observedFact.Size} bytes blob {observedFact.Blob}");
                        }
                    }
                    foreach (var file in observed.Keys)
                    {
                        if (!admitted.ContainsKey(file))
                        {
                            drift.Add($"asset {asset.Name} file {file} was observed but is not part of the admitted snapshot");
                        }
                    }
                    if (drift.Count > assetSentencesBefore)
                    {
                        driftedAssets.Add(asset.Name);
                    }
                }
            }

            if (drift.Count == 0
                && root.TryGetProperty("environment_identity", out var recordedIdentity)
                && recordedIdentity.GetString() is { Length: > 0 } admittedIdentity
                && !string.Equals(admittedIdentity, observedEnvironmentIdentity, StringComparison.Ordinal))
            {
                drift.Add($"environment identity drifted: admitted {admittedIdentity}, observed {observedEnvironmentIdentity}");
                nonAssetDrift++;
            }
        }

        return new EnvironmentDrift(
            drift,
            driftedAssets.Distinct(StringComparer.Ordinal).ToArray(),
            nonAssetDrift > 0);
    }

    static Dictionary<string, (long Size, string? Blob)> ManifestFilesByName(JsonElement files)
    {
        var byName = new Dictionary<string, (long, string?)>(StringComparer.Ordinal);
        if (files.ValueKind != JsonValueKind.Array) return byName;
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("file", out var name)) continue;
            var size = entry.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : -1L;
            string? blob = entry.TryGetProperty("blob", out var blobElement) && blobElement.ValueKind == JsonValueKind.String
                ? blobElement.GetString()
                : null;
            byName[name.GetString() ?? ""] = (size, blob);
        }
        return byName;
    }
}
