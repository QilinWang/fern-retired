using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// The complete operational projection of one v5 execution profile.  This is
/// the single C# admission owner for both local legacy Matrix preparation and
/// SSH Stage dispatch; callers project only the fields their boundary needs.
/// </summary>
public sealed record ExecutionProfileV5(
    string ProfileId,
    string Revision,
    string Executor,
    string? SshDestination,
    string? RegistryHomeProfileId,
    string Device,
    string SourceRoot,
    string Python,
    string WorkerModule,
    ulong WorkerSlots,
    ulong MaxRestarts,
    ulong RestartDelaySeconds,
    ulong MaxTargetSeconds,
    ulong MaxSweepSeconds,
    string CampaignRoot,
    string ArtifactRoot,
    string WorkRoot,
    // The declared accelerator requirement, projected because a Worker Site
    // registration must compare it against what the machine actually reports:
    // Rust's ObservedExecutionFingerprint::drift_from reads exactly these three
    // and refuses a profile whose GPU is smaller, older, or a different card
    // than the one declared. Null on a cpu profile, where an accelerator may
    // not be declared at all.
    ulong? AcceleratorMinMemoryGib,
    IReadOnlyList<uint>? AcceleratorMinComputeCapability,
    string? AcceleratorRequiredDeviceUuid,
    // Where recorded datasets live. Projected because two callers need it and
    // neither can be correct without it: a Worker Site preflight refuses when
    // an exported SERIE_KNOWN_ROOT disagrees with this declaration, and the
    // remote probe asks the Site whether this directory is actually there. The
    // field existed and nothing read it once before, and a whole afternoon of
    // recorded cells looked in the wrong place.
    string? KnownDataRoot)
{
    public string? RegistryPath { get; init; }

    const string Schema = "fern.execution_profile.v5";
    static readonly HashSet<string> Allowed =
    [
        "schema", "profile_id", "executor", "ssh_destination", "registry_home_profile_id",
        "device", "source_root", "python", "worker", "admitted_models",
        "supervisor", "accelerator", "known_data_root", "registry_path", "campaign_root", "artifact_root", "work_root",
        "warn_free_gib", "warn_free_percent",
        // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
        // slice 2): where the Site's cells take their code from, and the repository
        // for working-tree and commit. register-site reads and checks them; a
        // profile that declares them is not refused here as unknown.
        "code_from", "repository",
    ];
    static readonly string[] Required =
    [
        "schema", "profile_id", "executor", "device", "source_root", "python",
        "worker", "supervisor", "campaign_root", "artifact_root", "work_root", "warn_free_gib",
        "warn_free_percent",
    ];

    /// <summary>
    /// One execution profile by id, out of the directory that holds them.
    ///
    /// CHG 2026-08-29: the parameter was a workspace root that this method
    /// appended "profiles" to. An installed binary set is bound to content and
    /// may be served from a machine whose worktree is gone, so the directory
    /// is named by the installation receipt and passed in whole. A caller that
    /// genuinely has a worktree passes its profiles/ subdirectory.
    /// </summary>
    public static SiteResult<ExecutionProfileV5> Read(
        string profilesRoot,
        string profileId,
        string? expectedRevision)
    {
        if (!ValidProfileId(profileId))
        {
            return SiteResult<ExecutionProfileV5>.Permanent(
                $"execution profile id {profileId} is not a safe config/profiles/<id>.json name");
        }
        var path = Path.Combine(Path.GetFullPath(profilesRoot), profileId + ".json");
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null)
        {
            return SiteResult<ExecutionProfileV5>.Permanent($"execution profile {path} is not a regular file");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("execution profile must be an object");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Allowed.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new InvalidDataException($"execution profile has unknown or duplicate property {property.Name}");
                }
            }
            foreach (var required in Required)
            {
                if (!root.TryGetProperty(required, out _))
                {
                    throw new InvalidDataException($"execution profile is missing {required}");
                }
            }

            // CHG: Site registration and placement already consume this list.
            // Preserve the existing execution identity, owned separately below.
            if (root.TryGetProperty("admitted_models", out var models))
            {
                var admitted = new HashSet<string>(StringComparer.Ordinal);
                if (models.ValueKind != JsonValueKind.Array || models.EnumerateArray().Any(model =>
                        model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString())
                        || !admitted.Add(model.GetString()!)))
                {
                    throw new InvalidDataException("admitted_models must be an array of distinct non-empty strings");
                }
            }

            // The RULES live once, in ExecutionProfileDeclaration.Issues, with
            // Rust's own field and explanation wording, so an operator reads the
            // same sentence whether it reached them through `profile validate`
            // or through this admission gate refusing to hand out a profile at
            // all. The structural checks above and the typed reads below stay
            // here: a declaration missing a property, or
            // carrying the wrong JSON kind, is not a rule violation the issue
            // list can describe — it is a file that cannot be read as a
            // declaration in the first place, which is the same split Rust keeps
            // between `read` and `declaration_issues`.
            if (ExecutionProfileDeclaration.Issues(root) is { Count: > 0 } declared)
            {
                throw new InvalidDataException(
                    $"profile field {declared[0].Field}: {declared[0].Explanation}");
            }
            if (Text(root, "schema") != Schema) throw new InvalidDataException($"execution profile schema must be {Schema}");
            if (Text(root, "profile_id") != profileId) throw new InvalidDataException("execution profile id does not match its file name");

            var executor = Text(root, "executor");
            if (executor is not ("local" or "ssh")) throw new InvalidDataException("execution profile executor must be local or ssh");
            var destination = OptionalText(root, "ssh_destination");
            var registryHome = OptionalText(root, "registry_home_profile_id");
            if (executor == "ssh")
            {
                destination = NonBlank(destination, "ssh_destination");
                registryHome = NonBlank(registryHome, "registry_home_profile_id");
            }

            var device = Text(root, "device");
            if (device is not ("cpu" or "cuda")) throw new InvalidDataException("execution profile device is neither cpu nor cuda");
            var sourceRoot = Absolute(Text(root, "source_root"), "source_root");
            var python = Absolute(Text(root, "python"), "python");
            var campaignRoot = Absolute(Text(root, "campaign_root"), "campaign_root");
            var artifactRoot = Absolute(Text(root, "artifact_root"), "artifact_root");
            var workRoot = Absolute(Text(root, "work_root"), "work_root");

            var worker = Object(root, "worker");
            RequireExactly(worker, ["module", "slots"], ["module", "slots"], "worker");
            var workerModule = NonBlank(Text(worker, "module"), "worker.module");
            var workerSlots = Unsigned(worker, "slots");
            if (workerSlots != 1) throw new InvalidDataException("worker.slots must be exactly one");

            var supervisor = Object(root, "supervisor");
            RequireExactly(
                supervisor,
                ["max_restarts", "restart_delay_seconds", "max_target_seconds", "max_sweep_seconds"],
                ["max_restarts", "restart_delay_seconds", "max_target_seconds", "max_sweep_seconds"],
                "supervisor");
            var maxRestarts = Unsigned(supervisor, "max_restarts");
            var restartDelay = Unsigned(supervisor, "restart_delay_seconds");
            var maxTarget = Unsigned(supervisor, "max_target_seconds");
            var maxSweep = Unsigned(supervisor, "max_sweep_seconds");
            if (maxTarget == 0 || maxSweep < maxTarget || (maxRestarts > 0 && restartDelay == 0))
            {
                throw new InvalidDataException("execution profile supervisor limits are inconsistent");
            }
            SshExecutionProfile.ValidateAccelerator(root, device);
            var accelerator = SshExecutionProfile.ReadAcceleratorRequirement(root);
            _ = Unsigned(root, "warn_free_gib");
            if (Unsigned(root, "warn_free_percent") > 100) throw new InvalidDataException("warn_free_percent must be at most 100");

            var revision = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(SshExecutionProfile.CanonicalBytes(root)));
            if (expectedRevision is not null && revision != expectedRevision)
            {
                throw new InvalidDataException($"execution profile changed after admission: expected {expectedRevision}, found {revision}");
            }
            return SiteResult<ExecutionProfileV5>.Ok(new ExecutionProfileV5(
                profileId, revision, executor, destination, registryHome, device,
                sourceRoot, python, workerModule, workerSlots, maxRestarts, restartDelay, maxTarget, maxSweep,
                campaignRoot, artifactRoot, workRoot,
                accelerator.MinMemoryGib, accelerator.MinComputeCapability, accelerator.RequiredDeviceUuid,
                OptionalText(root, "known_data_root") is { } known ? Absolute(known, "known_data_root") : null)
            {
                RegistryPath = OptionalText(root, "registry_path") is { } registry ? Absolute(registry, "registry_path") : null,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return SiteResult<ExecutionProfileV5>.Permanent($"execution profile {path} is invalid: {error.Message}");
        }
    }

    static void RequireExactly(JsonElement value, string[] allowed, string[] required, string name)
    {
        var remaining = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) throw new InvalidDataException($"{name} has unknown or duplicate property {property.Name}");
        }
        foreach (var property in required)
        {
            if (!value.TryGetProperty(property, out _)) throw new InvalidDataException($"{name} is missing {property}");
        }
    }

    static JsonElement Object(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{name} must be an object");
    static string Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new InvalidDataException($"{name} must be a string");
    static string? OptionalText(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException($"{name} must be a string")
            : null;
    static ulong Unsigned(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetUInt64(out var number)
            ? number
            : throw new InvalidDataException($"{name} must be a non-negative integer");
    static string NonBlank(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"{name} must be non-empty");
    static string Absolute(string value, string name) =>
        Path.IsPathFullyQualified(value) ? value : throw new InvalidDataException($"{name} must be absolute");
    /// <summary>
    /// Whether a string may name a profile at <c>profiles/&lt;id&gt;.json</c>.
    /// Public because the preflight report has to tell an unusable ID apart
    /// from an absent file, and a second copy of this rule at that boundary is
    /// how the two would come to disagree.
    /// </summary>
    public static bool ValidProfileId(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>What <see cref="ValidProfileId"/> requires, for a report to quote.</summary>
    public const string ProfileIdRule =
        "must be 1-64 ASCII letters, digits, hyphens and underscores, beginning with a letter or digit; "
        + "this keeps its file path predictable";
    static bool ValidLowerSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// The SSH-only operational projection of one immutable execution profile.
/// It contains no scientific configuration and no credentials. The revision
/// is computed from the same ordered JSON shape as the Rust v5 owner, so an
/// edit after Registry admission is a refusal rather than a changed launch.
/// </summary>
public sealed record SshExecutionProfile(
    string ProfileId,
    string Revision,
    string SshDestination,
    string Python,
    string RemoteWorkRoot,
    TimeSpan CommandTimeout,
    // CHG 2026-08-29 (Program 18, Site-C): the Site name of the Registry Home
    // machine, under which retained bytes pulled home at a cell's end are recorded.
    string? RegistryHomeProfileId)
{
    const string Schema = "fern.execution_profile.v5";
    const ulong TransportMarginSeconds = 300;

    static readonly string[] TopLevelProperties =
    [
        "schema", "profile_id", "executor", "ssh_destination", "registry_home_profile_id",
        "device", "source_root", "python", "worker",
        "supervisor", "accelerator", "known_data_root", "registry_path", "campaign_root", "artifact_root", "work_root",
        "warn_free_gib", "warn_free_percent",
    ];

    public static SiteResult<SshExecutionProfile> Read(
        string profilesRoot,
        string profileId,
        string expectedRevision)
    {
        var loaded = ExecutionProfileV5.Read(profilesRoot, profileId, expectedRevision);
        if (!loaded.IsOk) return loaded.RefusalAs<SshExecutionProfile>();
        var profile = loaded.Value;
        if (profile.Executor != "ssh")
        {
            return SiteResult<SshExecutionProfile>.Permanent("remote Stage dispatch requires an ssh execution profile");
        }
        if (profile.MaxTargetSeconds > (ulong)TimeSpan.MaxValue.TotalSeconds - TransportMarginSeconds)
        {
            return SiteResult<SshExecutionProfile>.Permanent("execution profile target timeout does not fit a C# duration");
        }
        return SiteResult<SshExecutionProfile>.Ok(new SshExecutionProfile(
            profile.ProfileId,
            profile.Revision,
            profile.SshDestination!,
            profile.Python,
            profile.WorkRoot,
            TimeSpan.FromSeconds(profile.MaxTargetSeconds + TransportMarginSeconds),
            profile.RegistryHomeProfileId));
    }

    internal static void ValidateAccelerator(JsonElement root, string device)
    {
        if (!root.TryGetProperty("accelerator", out var value))
        {
            if (device == "cuda")
            {
                throw new InvalidDataException("cuda execution profile requires accelerator facts");
            }
            return;
        }
        if (device != "cuda")
        {
            throw new InvalidDataException("cpu execution profile cannot declare an accelerator");
        }
        var accelerator = RequireObject(value, "accelerator");
        RequireExactly(
            accelerator,
            ["min_memory_gib", "min_compute_capability", "required_device_uuid"],
            "accelerator");
        if (Unsigned(accelerator, "min_memory_gib") == 0)
        {
            throw new InvalidDataException("accelerator.min_memory_gib must be positive");
        }
        if (accelerator.TryGetProperty("min_compute_capability", out var capability))
        {
            if (capability.ValueKind != JsonValueKind.Array
                || capability.GetArrayLength() != 2
                || !capability[0].TryGetUInt32(out var major)
                || !capability[1].TryGetUInt32(out _)
                || major == 0)
            {
                throw new InvalidDataException("accelerator.min_compute_capability must be two non-negative integers with a positive major");
            }
        }
        if (accelerator.TryGetProperty("required_device_uuid", out var uuid))
        {
            _ = NonBlank(String(uuid, "accelerator.required_device_uuid"), "accelerator.required_device_uuid");
        }
    }

    /// <summary>
    /// The declared accelerator requirement, after
    /// <see cref="ValidateAccelerator"/> has already refused every malformed
    /// shape. Read here rather than in the caller so one function owns what
    /// `accelerator` means.
    /// </summary>
    internal static (ulong? MinMemoryGib, IReadOnlyList<uint>? MinComputeCapability, string? RequiredDeviceUuid)
        ReadAcceleratorRequirement(JsonElement root)
    {
        if (!root.TryGetProperty("accelerator", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }
        IReadOnlyList<uint>? capability = value.TryGetProperty("min_compute_capability", out var declared)
            ? [declared[0].GetUInt32(), declared[1].GetUInt32()]
            : null;
        var uuid = value.TryGetProperty("required_device_uuid", out var required) ? required.GetString() : null;
        return (Unsigned(value, "min_memory_gib"), capability, uuid);
    }

    internal static byte[] CanonicalBytes(JsonElement root)
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(
            bytes,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            WriteString(json, root, "schema");
            WriteString(json, root, "profile_id");
            WriteString(json, root, "executor");
            WriteOptionalString(json, root, "ssh_destination");
            WriteOptionalString(json, root, "registry_home_profile_id");
            WriteString(json, root, "device");
            WriteString(json, root, "source_root");
            WriteString(json, root, "python");
            WriteObject(json, root, "worker", ["module", "slots"]);
            WriteObject(
                json,
                root,
                "supervisor",
                ["max_restarts", "restart_delay_seconds", "max_target_seconds", "max_sweep_seconds"]);
            WriteOptionalObject(
                json,
                root,
                "accelerator",
                ["min_memory_gib", "min_compute_capability", "required_device_uuid"]);
            WriteOptionalString(json, root, "known_data_root");
            WriteOptionalString(json, root, "registry_path");
            WriteString(json, root, "campaign_root");
            WriteString(json, root, "artifact_root");
            WriteString(json, root, "work_root");
            WriteUnsigned(json, root, "warn_free_gib");
            WriteUnsigned(json, root, "warn_free_percent");
            json.WriteEndObject();
        }
        return bytes.ToArray();
    }

    static void WriteObject(Utf8JsonWriter json, JsonElement owner, string name, string[] order)
    {
        json.WritePropertyName(name);
        json.WriteStartObject();
        var value = owner.GetProperty(name);
        foreach (var property in order)
        {
            if (value.TryGetProperty(property, out var found))
            {
                json.WritePropertyName(property);
                found.WriteTo(json);
            }
        }
        json.WriteEndObject();
    }

    static void WriteOptionalObject(Utf8JsonWriter json, JsonElement owner, string name, string[] order)
    {
        if (owner.TryGetProperty(name, out _))
        {
            WriteObject(json, owner, name, order);
        }
    }

    static void WriteString(Utf8JsonWriter json, JsonElement owner, string name) =>
        json.WriteString(name, Text(owner, name));

    static void WriteOptionalString(Utf8JsonWriter json, JsonElement owner, string name)
    {
        if (owner.TryGetProperty(name, out var value))
        {
            json.WriteString(name, String(value, name));
        }
    }

    static void WriteUnsigned(Utf8JsonWriter json, JsonElement owner, string name) =>
        json.WriteNumber(name, Unsigned(owner, name));

    static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be an object");
        }
        return value;
    }

    static void RequireExactly(JsonElement value, IReadOnlyCollection<string> allowed, string name)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedSet.Remove(property.Name))
            {
                throw new InvalidDataException($"{name} has unknown or duplicate property {property.Name}");
            }
        }
        foreach (var required in RequiredProperties(name))
        {
            if (!value.TryGetProperty(required, out _))
            {
                throw new InvalidDataException($"{name} is missing {required}");
            }
        }
    }

    static IReadOnlyList<string> RequiredProperties(string name) => name switch
    {
        "execution profile" =>
        [
            "schema", "profile_id", "executor", "ssh_destination", "registry_home_profile_id", "device",
            "source_root", "python", "worker", "supervisor",
            "campaign_root", "artifact_root", "work_root", "warn_free_gib", "warn_free_percent",
        ],
        "worker" => ["module", "slots"],
        "supervisor" => ["max_restarts", "restart_delay_seconds", "max_target_seconds", "max_sweep_seconds"],
        "accelerator" => ["min_memory_gib"],
        _ => throw new InvalidDataException($"no schema is declared for {name}"),
    };

    static string Text(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"missing {name}");
        }
        return String(value, name);
    }

    static string String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new InvalidDataException($"{name} must be a string");

    static ulong Unsigned(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetUInt64(out var number)
            ? number
            : throw new InvalidDataException($"{name} must be a non-negative integer");

    static string NonBlank(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"{name} must be non-empty");

    static string Absolute(string value, string name) =>
        Path.IsPathFullyQualified(value)
            ? value
            : throw new InvalidDataException($"{name} must be absolute");

    static bool ValidProfileId(string value) =>
        value.Length is > 0 and <= 64
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    static bool ValidLowerSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
