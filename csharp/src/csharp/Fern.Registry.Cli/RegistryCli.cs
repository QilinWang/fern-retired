using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Fern.Registry;

namespace Fern.Registry.Cli;

/// <summary>
/// Private Task subprocess contract, the same shape as fern-home:
/// operation plus --registry, typed JSON on stdin, receipt bytes on stdout,
/// exit 0 for a written receipt, 2 for a typed refusal, 1 for a fault.
/// CHG: Task execution is retired; this dispatcher retains the Registry operation protocols only.
/// </summary>
public static class RegistryCli
{
    public const int ExitOk = 0;
    public const int ExitFault = 1;
    public const int ExitRefused = 2;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0)
            {
                stderr.WriteLine("fern-registry: usage: fern-registry <operation> --registry <lifecycle.db>");
                stderr.WriteLine("fern-registry executable provenance: version --json (no Registry access)");
                stderr.WriteLine("fern-registry evidence-policy operation: register-evidence-policy-public");
                stderr.WriteLine("fern-registry artifact retention operations: " + string.Join(", ", ArtifactRetentionCli.Operations));
                stderr.WriteLine("fern-registry substrate operation: initialize-registry");
                stderr.WriteLine(
                    "fern-registry read-only report: work-facts --registry <lifecycle.db> --id <identity> --json");
                // These name a campaign root or a lane's own --db and locate
                // their Registry Home themselves, so they take no --registry.
                stderr.WriteLine(
                    "fern-registry without --registry: list-runs, "
                    + "export-accepted-matrix, check-accepted-export, resolve-artifact-keys");
                return ExitFault;
            }

            var operation = args[0];
            // CHG 2026-09-11 night (slice 8): list-runs stays with the run views
            // the statistics admission reads; it parses its own --registry.
            if (operation == "list-runs")
            {
                return RunListCli.Run(args[1..], stdout, stderr);
            }
            // CHG: executable provenance is independent of Registry access.
            if (operation == "version")
            {
                if (args is not ["version", "--json"])
                {
                    stderr.WriteLine("fern-registry: usage: fern-registry version --json");
                    return ExitFault;
                }
                var assembly = typeof(RegistryCli).Assembly;
                var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .ToDictionary(item => item.Key, item => item.Value ?? "", StringComparer.Ordinal);
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    schema = "fern.registry_binary_version.v1",
                    role = "registry-home",
                    crate_version = assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    built_from_commit = metadata.GetValueOrDefault("FernBuiltFromCommit", "unrecorded").Trim(),
                    built_from_dirty_tree = metadata.GetValueOrDefault("FernBuiltFromDirtyTree", "unrecorded").Trim() != "0",
                    source_archive_sha256 = metadata.GetValueOrDefault("FernCommittedSourceArchiveSha256"),
                    dotnet_runtime = RuntimeInformation.FrameworkDescription,
                    run_plan_protocol = "fern.run_plan_parent_child.v2",
                }));
                return ExitOk;
            }
            if (operation == "installation-identity" && args.Length == 1)
            {
                stdout.WriteLine("{\"schema\":\"fern.installation_identity.v4\",\"role\":\"registry-home\",\"run_plan_protocol\":\"fern.run_plan_parent_child.v2\"}");
                return ExitOk;
            }
            // Commands that take no --registry: they name a campaign root or
            // the lab notes' own --db, and locate the Registry Home themselves.
            var registry = ReadRegistryFlag(args);
            if (registry is null && operation == "work-facts" && !args.Contains("--registry"))
                registry = RegistryHome.DefaultRegistryPath;
            if (string.IsNullOrWhiteSpace(registry))
            {
                stderr.WriteLine($"fern-registry: {operation} requires --registry");
                return ExitFault;
            }

            // CHG 2026-09-26 (schema 20; owner: "Analysis names: remove, even this
            // analysis entering registry is suspicious slop"): registry-admit-statistic
            // left with the analyses table it wrote; an unknown verb now.
            if (operation == "work-facts")
            {
                return WorkFactsCli.Run(registry, args[1..], stdout, stderr);
            }

            if (operation == "registry-probe-readiness")
            {
                using var reader = RegistryHome.OpenReader(registry);
                stdout.WriteLine("{\"schema\":\"fern.registry_readiness.v1\",\"status\":\"readable\"}");
                return ExitOk;
            }

            return operation switch
            {
                // CHG 2026-09-26 (schema 19): audit-transition-receipt-hashes left
                // with the receipt hash chain it recomputed; an unknown verb now.
                // CHG 2026-09-28: resource slot declaration retired; historical
                // slot pointers still protect storage reclamation in Registry.
                "initialize-registry" => InitializeRegistry(registry, args[1..], stdout, stderr),
                // CHG 2026-09-11 night (slice 11): the storage verbs over sealed
                // artifacts (holds, gc, a snapshot's relocation) get their
                // entrance back; it left with the evidence verbs at slice 8
                // although the authorities stayed. The two matrix signing verbs
                // retired; see ArtifactRetentionCli.Operations for the one
                // left unwired.
                _ when ArtifactRetentionCli.Operations.Contains(operation)
                    => ArtifactRetentionCli.Dispatch(operation, registry, stdin, stdout, stderr),
                _ => Unknown(operation, stderr),
            };
        }
        catch (RegistryFault fault)
        {
            stderr.WriteLine($"fern-registry: {fault.Message}");
            return ExitFault;
        }
        catch (JsonException exception)
        {
            stderr.WriteLine($"fern-registry: typed request is not valid JSON: {exception.Message}");
            return ExitFault;
        }
    }

    /// <summary>Initialize the current embedded schema; existing old databases are refused.</summary>
    static int InitializeRegistry(
        string registry,
        IReadOnlyList<string> arguments,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (arguments.Count != 2 || arguments[0] != "--registry" || arguments[1] != registry)
        {
            stderr.WriteLine("fern-registry: usage: initialize-registry --registry <new-path>");
            return ExitFault;
        }
        switch (RegistrySchema.Initialize(registry))
        {
            case RegistryInitializationOutcome.Refused refused:
                stderr.WriteLine($"fern-registry: {refused.Detail}");
                return ExitRefused;
            case RegistryInitializationOutcome.Initialized initialized:
                stdout.WriteLine(RegistryJson.Serialize(initialized.Receipt));
                return ExitOk;
            default:
                stderr.WriteLine("fern-registry: initializer returned neither initialized nor refused");
                return ExitFault;
        }
    }

    static int Unknown(string operation, TextWriter stderr)
    {
        stderr.WriteLine($"fern-registry: unknown operation {operation}");
        return ExitFault;
    }

    static string? ReadRegistryFlag(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--registry" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

}
