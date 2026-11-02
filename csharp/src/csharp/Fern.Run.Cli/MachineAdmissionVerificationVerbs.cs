using System.CommandLine;
using System.Text;
using Fern.Registry;
using Fern.Scheduler;

namespace Fern.Run.Cli;

/// <summary>Public MachineAdmission verification command.</summary>
public static class MachineAdmissionVerificationVerbs
{
    static string DefaultRegistry => Fern.Registry.RegistryHome.DefaultRegistryPath;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(VerifyMachineAdmission());
    }

    static Command VerifyMachineAdmission()
    {
        var command = new Command(
            "verify-machine-admission",
            "Observe one Site machine/runtime boundary and write MachineAdmission; no install, model-link, smoke, Plan, or Cell runs.");
        var on = new Option<string>("--on")
        {
            Description = "Execution profile/Site id whose machine boundary is observed.",
            Required = true,
        };
        var registry = new Option<string>("--registry")
        {
            Description = "Registry Home that stores the current MachineAdmission.",
            DefaultValueFactory = _ => string.Empty,
        };
        var actor = new Option<string>("--actor")
        {
            Description = "Stable actor identity recorded on the machine admission.",
            Required = true,
        };
        var output = new Option<string?>("--out")
        {
            Description = "Also write a copy of the machine-admission receipt here.",
        };
        foreach (var option in new Option[] { on, registry, actor, output })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            var registryPath = parsed.GetValue(registry)!;
            if (!File.Exists(registryPath))
            {
                Console.Error.WriteLine($"verify-machine-admission failed: Registry Home {registryPath} does not exist");
                return 1;
            }

            try
            {
                using var writer = RegistryHome.OpenWriter(registryPath);
                var recorded = MachineAdmissionVerification.ObserveAndRecord(
                    writer,
                    Fern.Scheduler.WorktreeControlPaths.RepoRoot(),
                    parsed.GetValue(on)!,
                    BinaryVersionReport.FromThisBinary().CrateVersion,
                    parsed.GetValue(actor)!);
                if (recorded.TryGetRefusal(out var refusal))
                {
                    Console.Error.WriteLine("verify-machine-admission failed: " + refusal.Detail);
                    return 1;
                }
                if (!recorded.TryGetValue(out var outcome))
                {
                    Console.Error.WriteLine("verify-machine-admission failed: admission returned neither value nor refusal");
                    return 1;
                }

                Console.Error.WriteLine(
                    $"machine admission {outcome.Status} {outcome.Admission.SiteId} "
                    + $"{outcome.Admission.MachineAdmissionIdentity} in {registryPath}");
                return WriteOutput(parsed.GetValue(output), outcome.Admission.ReceiptJson);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or RegistryFault)
            {
                Console.Error.WriteLine("verify-machine-admission failed: " + error.Message);
                return 1;
            }
        });
        return command;
    }

    static int WriteOutput(string? path, string receipt)
    {
        if (string.IsNullOrEmpty(path))
        {
            Console.Out.WriteLine(receipt);
            return 0;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var bytes = StrictUtf8.GetBytes(receipt);
            var staging = path + ".tmp-" + Environment.ProcessId;
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("verify-machine-admission failed: " + error.Message);
            return 1;
        }
    }
}
