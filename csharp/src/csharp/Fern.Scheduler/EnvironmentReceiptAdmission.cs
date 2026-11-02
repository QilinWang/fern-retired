using System.Text.Json;
using Fern.Registry;
using Fern.Site;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// Compile consumes the Registry environment admission for a Site. A matching
/// row is the runtime-admission fact; a missing or mismatched row is a
/// refusal, not a reason to start <c>fern_stage_runtime probe</c>. A JSON
/// file is an optional pin that must equal that row. SSH endpoint and
/// profile revision are not admit keys; interpreter, GPU UUID, and machine
/// generation are.
/// </summary>
public sealed record AdmittedEnvironment(
    string SiteId,
    string EnvironmentIdentity,
    string EnvironmentKind,
    string Outcome,
    string Interpreter,
    string ProfileRevision,
    string MachineAdmissionIdentity,
    string ReceiptLocator);

public sealed record ConsumedMachineAdmission(
    string MachineAdmissionIdentity,
    string MachineGeneration,
    string? EndpointSessionId,
    string PythonAbi,
    string PythonVersion,
    string? DeviceId,
    string ObservedEnvironmentDigest,
    long RecordedAtMs);

public static class EnvironmentInstallationAdmission
{
    public static SiteResult<ConsumedMachineAdmission> ConsumeCurrentMachineAdmission(
        RegistryWriter writer,
        ExecutionProfileV5 profile)
    {
        var parent = MachineAdmissionAuthority.Current(writer.Connection, profile.ProfileId);
        if (parent is null)
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} has no current MachineAdmission; run verify-machine-admission --on "
                + $"{profile.ProfileId} before site install-model-environment. A missing machine admission is "
                + "not permission to install packages, repair assets, or start a model-link probe");
        }

        if (parent.Outcome != "machine_admitted")
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission outcome {parent.Outcome} is not machine_admitted");
        }

        if (!string.Equals(parent.Executor, profile.Executor, StringComparison.Ordinal))
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission executor {parent.Executor} is not profile executor {profile.Executor}");
        }

        if (!string.Equals(parent.PythonExecutable, profile.Python, StringComparison.Ordinal))
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission interpreter {parent.PythonExecutable} is not profile python {profile.Python}");
        }

        if (!string.Equals(parent.Device, profile.Device, StringComparison.Ordinal))
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission device {parent.Device} is not profile device {profile.Device}");
        }

        if (profile.Device == "cuda")
        {
            if (!parent.CudaAvailable || parent.DeviceId is not { Length: > 0 })
            {
                return SiteResult<ConsumedMachineAdmission>.Permanent(
                    $"Site {profile.ProfileId} MachineAdmission does not prove an available CUDA GPU UUID");
            }

            if (profile.AcceleratorRequiredDeviceUuid is { Length: > 0 } required
                && !string.Equals(required, parent.DeviceId, StringComparison.Ordinal))
            {
                return SiteResult<ConsumedMachineAdmission>.Permanent(
                    $"Site {profile.ProfileId} MachineAdmission GPU {parent.DeviceId} is not required profile GPU {required}");
            }
        }

        if (parent.PythonAbi != ModelEnvironmentInstallation.RequiredPythonAbi)
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission Python ABI {parent.PythonAbi} is not "
                + ModelEnvironmentInstallation.RequiredPythonAbi);
        }

        string digest;
        try
        {
            digest = ObservedExecutionProbe.EnvironmentContentDigestFromRenderedJson(parent.ObservedFingerprintJson);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return SiteResult<ConsumedMachineAdmission>.Permanent(
                $"Site {profile.ProfileId} MachineAdmission observed_fingerprint_json is not a usable environment-content proof: "
                + error.Message);
        }

        return SiteResult<ConsumedMachineAdmission>.Ok(new ConsumedMachineAdmission(
            parent.MachineAdmissionIdentity,
            parent.MachineGeneration,
            parent.EndpointSessionId,
            parent.PythonAbi,
            parent.PythonVersion,
            parent.DeviceId,
            digest,
            parent.RecordedAtMs));
    }
}

public static class EnvironmentReceiptAdmission
{
    public const string Schema = "fern.model_environment_installation.v1";

    public static SiteResult<AdmittedEnvironment> AdmitFromRegistry(
        SqliteConnection connection,
        ExecutionProfileV5 profile,
        IReadOnlyList<string> requiredCatalogModels,
        IReadOnlyList<string> explicitReceiptPaths)
    {
        var current = SiteEnvironmentAdmissionAuthority.Current(connection, profile.ProfileId);
        if (current is null)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"Site {profile.ProfileId} has no environment admission in this Registry Home; "
                + "run fern-run site install-model-environment --on "
                + profile.ProfileId
                + ". A missing admission is not a reason to start fern_stage_runtime probe");
        }

        if (explicitReceiptPaths.Count > 0)
        {
            var pinned = Admit(profile, explicitReceiptPaths);
            if (!pinned.IsOk)
            {
                return pinned;
            }

            if (!string.Equals(
                    pinned.Value.EnvironmentIdentity,
                    current.EnvironmentIdentity,
                    StringComparison.Ordinal))
            {
                return SiteResult<AdmittedEnvironment>.Permanent(
                    $"--environment-receipt identity {pinned.Value.EnvironmentIdentity} is not "
                    + $"the Registry admission {current.EnvironmentIdentity} for Site {profile.ProfileId}");
            }

            if (!string.Equals(
                    pinned.Value.MachineAdmissionIdentity,
                    current.MachineAdmissionIdentity,
                    StringComparison.Ordinal))
            {
                return SiteResult<AdmittedEnvironment>.Permanent(
                    $"--environment-receipt MachineAdmission parent {pinned.Value.MachineAdmissionIdentity} is not "
                    + $"the Registry admission parent {current.MachineAdmissionIdentity} for Site {profile.ProfileId}");
            }
        }

        var machineAdmission = MachineAdmissionAuthority.Current(connection, profile.ProfileId);
        if (machineAdmission is null)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"Site {profile.ProfileId} has no current MachineAdmission; run verify-machine-admission --on "
                + $"{profile.ProfileId} before consuming environment admission");
        }

        var placement = SiteEnvironmentAdmissionAuthority.RegisteredPlacement(connection, profile.ProfileId);
        return AdmitAdmission(
            profile,
            current,
            requiredCatalogModels,
            placement?.DeviceId,
            placement?.MachineGeneration,
            machineAdmission);
    }

    public static SiteResult<AdmittedEnvironment> AdmitAdmission(
        ExecutionProfileV5 profile,
        SiteEnvironmentAdmission admission,
        IReadOnlyList<string> requiredCatalogModels,
        string? registeredDeviceId,
        string? registeredMachineGeneration = null,
        MachineAdmission? currentMachineAdmission = null)
    {
        if (!string.Equals(admission.SiteId, profile.ProfileId, StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission names Site {admission.SiteId}, not {profile.ProfileId}");
        }

        if (!string.Equals(admission.Interpreter, profile.Python, StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission interpreter {admission.Interpreter} "
                + $"is not profile python {profile.Python}");
        }

        if (admission.Outcome is not ("installed" or "already_ready" or "repaired_environment"))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission outcome {admission.Outcome} is not a ready admission");
        }

        if (string.IsNullOrWhiteSpace(admission.EnvironmentIdentity)
            || !admission.EnvironmentIdentity.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} does not name an environment identity");
        }

        if (string.IsNullOrWhiteSpace(admission.MachineAdmissionIdentity)
            || !admission.MachineAdmissionIdentity.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} does not name the MachineAdmission it consumed");
        }

        if (currentMachineAdmission is not null
            && !string.Equals(
                admission.MachineAdmissionIdentity,
                currentMachineAdmission.MachineAdmissionIdentity,
                StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} consumed MachineAdmission "
                + $"{admission.MachineAdmissionIdentity}, but the current MachineAdmission is "
                + $"{currentMachineAdmission.MachineAdmissionIdentity}; re-run site install-model-environment");
        }

        if (currentMachineAdmission is not null
            && !string.Equals(admission.DeviceId, currentMachineAdmission.DeviceId, StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission GPU {admission.DeviceId ?? "(none)"} is not the current "
                + $"MachineAdmission GPU {currentMachineAdmission.DeviceId ?? "(none)"}; "
                + "re-run site install-model-environment");
        }

        var missing = requiredCatalogModels
            .Where(model => !admission.Models.Contains(model, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} does not cover "
                + string.Join(", ", missing)
                + "; re-run site install-model-environment with those models");
        }

        if (!string.IsNullOrWhiteSpace(registeredDeviceId)
            && !string.Equals(admission.DeviceId, registeredDeviceId, StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission GPU {admission.DeviceId ?? "(none)"} is not the registered "
                + $"Site device {registeredDeviceId}; re-run site install-model-environment");
        }

        if (currentMachineAdmission is not null
            && !string.Equals(
                admission.MachineGeneration,
                currentMachineAdmission.MachineGeneration,
                StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} was recorded on machine generation "
                + $"{admission.MachineGeneration ?? "(none)"}, current MachineAdmission generation is "
                + $"{currentMachineAdmission.MachineGeneration}; a rebuilt machine does not inherit the old environment. "
                + "re-run site install-model-environment");
        }

        if (!string.IsNullOrWhiteSpace(admission.MachineGeneration)
            && !string.IsNullOrWhiteSpace(registeredMachineGeneration)
            && !string.Equals(
                admission.MachineGeneration,
                registeredMachineGeneration,
                StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment admission for Site {profile.ProfileId} was recorded on machine generation "
                + $"{admission.MachineGeneration}, current registered generation is "
                + $"{registeredMachineGeneration}; a rebuilt machine does not inherit the old environment. "
                + "re-run site install-model-environment");
        }

        return SiteResult<AdmittedEnvironment>.Ok(
            new AdmittedEnvironment(
                admission.SiteId,
                admission.EnvironmentIdentity,
                admission.EnvironmentKind,
                admission.Outcome,
                admission.Interpreter,
                admission.ExecutionProfileRevision,
                admission.MachineAdmissionIdentity,
                "registry:" + admission.SiteId));
    }

    public static SiteResult<AdmittedEnvironment> Admit(
        ExecutionProfileV5 profile,
        IReadOnlyList<string> receiptPaths)
    {
        if (receiptPaths.Count == 0)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"plan-experiment requires --environment-receipt for Site {profile.ProfileId}; "
                + "run fern-run site install-model-environment and pass that receipt. "
                + "A missing receipt is not a reason to start fern_stage_runtime probe");
        }

        AdmittedEnvironment? match = null;
        foreach (var path in receiptPaths)
        {
            var loaded = Load(path);
            if (!loaded.IsOk)
            {
                return loaded;
            }

            if (!string.Equals(loaded.Value.SiteId, profile.ProfileId, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return SiteResult<AdmittedEnvironment>.Permanent(
                    $"two environment receipts name Site {profile.ProfileId}: {match.ReceiptLocator} and {path}");
            }

            match = loaded.Value;
        }

        if (match is null)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"no --environment-receipt names Site {profile.ProfileId}");
        }

        if (!string.Equals(match.Interpreter, profile.Python, StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment receipt {match.ReceiptLocator} interpreter {match.Interpreter} "
                + $"is not profile python {profile.Python}");
        }

        if (match.Outcome is not ("installed" or "already_ready" or "repaired_environment"))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment receipt {match.ReceiptLocator} outcome {match.Outcome} is not a ready admission");
        }

        if (string.IsNullOrWhiteSpace(match.MachineAdmissionIdentity)
            || !match.MachineAdmissionIdentity.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment receipt {match.ReceiptLocator} does not name the MachineAdmission it consumed");
        }

        if (string.IsNullOrWhiteSpace(match.EnvironmentIdentity)
            || !match.EnvironmentIdentity.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment receipt {match.ReceiptLocator} does not name an environment identity");
        }

        return SiteResult<AdmittedEnvironment>.Ok(match);
    }

    static SiteResult<AdmittedEnvironment> Load(string path)
    {
        string bytes;
        try
        {
            bytes = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"cannot read environment receipt {path}: {error.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException error)
        {
            return SiteResult<AdmittedEnvironment>.Permanent(
                $"environment receipt {path} is not JSON: {error.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || schema.GetString() != Schema)
            {
                return SiteResult<AdmittedEnvironment>.Permanent(
                    $"environment receipt {path} is not {Schema}");
            }

            if (!TryField(root, "site_id", out var siteId)
                || !TryField(root, "environment_identity", out var identity)
                || !TryField(root, "environment_kind", out var environmentKind)
                || !TryField(root, "outcome", out var outcome)
                || !TryField(root, "interpreter", out var interpreter)
                || !TryField(root, "execution_profile_revision", out var revision)
                || !TryField(root, "machine_admission_identity", out var machineAdmissionIdentity))
            {
                return SiteResult<AdmittedEnvironment>.Permanent(
                    $"environment receipt {path} is missing a required admission field");
            }

            return SiteResult<AdmittedEnvironment>.Ok(new AdmittedEnvironment(
                siteId, identity, environmentKind, outcome, interpreter, revision, machineAdmissionIdentity, path));
        }
    }

    static bool TryField(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var field) || field.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        value = text;
        return true;
    }
}
