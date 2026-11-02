using System.Text.Json;
using Fern.Registry;

namespace Fern.Registry.Cli;

static class ArtifactRetentionCli
{
    public static int Dispatch(string operation, string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        return operation switch
        {
            "acquire-artifact-hold" => Acquire(registry, stdin, stdout, stderr),
            "preview-artifact-gc" => Preview(registry, stdin, stdout),
            "prepare-artifact-gc" => Prepare(registry, stdin, stdout, stderr),
            "reconcile-artifact-gc-claims" => Reconcile(registry, stdin, stdout, stderr),
            "record-present-artifact-location" => RecordPresent(registry, stdin, stdout, stderr),
            "relocate-artifact-snapshot" => RelocateSnapshot(registry, stdin, stdout, stderr),
            _ => throw new RegistryFault($"unknown artifact-retention operation {operation}"),
        };
    }

    /// <summary>
    /// The operations this dispatch answers, for the root switch and the usage
    /// line: the storage verbs over sealed artifacts (holds, gc, a snapshot's
    /// relocation). CHG 2026-09-11 night (slice 11): their dispatch left at
    /// slice 8 with the evidence verbs while the authorities stayed; it is
    /// back. relocate-scratch-evidence is not among them: the retired-verbs
    /// note of 2026-09-11 reads it as ceremony (it moved Matrix-era scratch
    /// into a committed-attempts layout), and its authority
    /// (ArtifactSnapshotRelocationAuthority.RelocateScratchEvidence) stays on
    /// the tree without a verb until the owner rules on the note's unifying
    /// form, check-tensor-files --hold / --collect.
    /// </summary>
    public static readonly IReadOnlyList<string> Operations =
    [
        "acquire-artifact-hold", "preview-artifact-gc", "prepare-artifact-gc", "reconcile-artifact-gc-claims",
        "record-present-artifact-location", "relocate-artifact-snapshot",
    ];

    static int Acquire(string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var request = Deserialize<AcquireHoldWrite>(stdin.ReadToEnd());
        using var writer = RegistryHome.OpenWriter(registry);
        var result = ArtifactRetentionAuthority.AcquireHold(writer, request);
        return Write(result, stdout, stderr);
    }

    static int Preview(string registry, TextReader stdin, TextWriter stdout)
    {
        var request = Deserialize<LocationRequest>(stdin.ReadToEnd());
        using var reader = RegistryHome.OpenReader(registry);
        stdout.WriteLine(RegistryJson.Serialize(
            ArtifactRetentionAuthority.PreviewGc(reader.Connection, request.LocationId)));
        return RegistryCli.ExitOk;
    }

    static int Prepare(string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var request = Deserialize<PrepareGcWrite>(stdin.ReadToEnd());
        using var writer = RegistryHome.OpenWriter(registry);
        var result = ArtifactRetentionAuthority.PrepareGc(writer, request);
        return Write(result, stdout, stderr);
    }

    static int Reconcile(string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var request = Deserialize<ReconcileRequest>(stdin.ReadToEnd());
        using var writer = RegistryHome.OpenWriter(registry);
        var result = ArtifactRetentionAuthority.ReconcileGcClaims(writer, request.NowMs);
        return Write(result, stdout, stderr);
    }

    static int RecordPresent(string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var request = Deserialize<PresentLocationWrite>(stdin.ReadToEnd());
        using var writer = RegistryHome.OpenWriter(registry);
        var result = ArtifactRetentionAuthority.RecordPresentLocation(writer, request);
        if (!result.TryGetValue(out var locationId))
        {
            return Write(result, stdout, stderr);
        }

        stdout.WriteLine(RegistryJson.Serialize(locationId));
        return RegistryCli.ExitOk;
    }

    static int RelocateSnapshot(string registry, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var request = Deserialize<ArtifactSnapshotRelocationWrite>(stdin.ReadToEnd());
        using var writer = RegistryHome.OpenWriter(registry);
        return Write(ArtifactSnapshotRelocationAuthority.Relocate(writer, request), stdout, stderr);
    }

    static int Write<T>(RegistryResult<T> result, TextWriter stdout, TextWriter stderr)
    {
        if (!result.TryGetValue(out var value))
        {
            if (!result.TryGetRefusal(out var refusal))
            {
                stderr.WriteLine("fern-registry: artifact-retention operation returned neither a receipt nor a refusal");
                return RegistryCli.ExitFault;
            }

            stderr.WriteLine($"fern-registry: {refusal.Detail}");
            return RegistryCli.ExitRefused;
        }

        stdout.WriteLine(RegistryJson.Serialize(value));
        return RegistryCli.ExitOk;
    }

    static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, RegistryJson.Options)
        ?? throw new RegistryFault($"typed request for {typeof(T).Name} is empty");

    sealed record LocationRequest(string LocationId);


    sealed record ReconcileRequest(long NowMs);
}
