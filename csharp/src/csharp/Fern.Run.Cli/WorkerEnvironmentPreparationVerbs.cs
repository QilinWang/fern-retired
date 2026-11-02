using System.CommandLine;
using System.Text.Json;
using Fern.Scheduler;
using Fern.Install;

namespace Fern.Run.Cli;

public static class WorkerEnvironmentPreparationVerbs
{
    public static void Add(Command parent)
    {
        var source = new Option<string>("--source") { Required = true, Description = "Verified source root on this Linux execution host." };
        var runtime = new Option<string>("--runtime-root") { Required = true, Description = "Worker-owned environment and preparation directory, outside source." };
        var study = new Option<string>("--study") { Required = true, Description = "Study source to compile with the prepared CUDA environment." };
        // CHG 2026-09-11: the two executables come from the installation
        // receipt on this machine, not from exported variables. A worker whose
        // FERN_RUN_BIN was exported by hand can name a binary from another
        // commit; the receipt names the set that was installed. The path to the
        // receipt may still arrive in the environment -- that is a path, not an
        // authority.
        var receipt = new Option<string?>("--installation-receipt")
        {
            Description = "Control-plane installation receipt on this machine. Defaults to FERN_INSTALLATION_RECEIPT.",
        };
        var command = new Command("prepare-worker-environment", "Reuse or install locked worker dependencies, verify CUDA/imports, and compile a Study; starts no training.");
        command.Options.Add(source);
        command.Options.Add(runtime);
        command.Options.Add(study);
        command.Options.Add(receipt);
        command.SetAction(parsed =>
        {
            try
            {
                var receiptPath = parsed.GetValue(receipt)
                    ?? Environment.GetEnvironmentVariable("FERN_INSTALLATION_RECEIPT");
                if (string.IsNullOrWhiteSpace(receiptPath))
                {
                    Console.Error.WriteLine(
                        "prepare-worker-environment failed: no installation receipt; pass --installation-receipt "
                        + "or set FERN_INSTALLATION_RECEIPT. The receipt names fern-run and fern-compile; "
                        + "an exported path names whatever was exported.");
                    return 1;
                }
                var members = InstalledControlReader.Members(receiptPath);
                if (!members.IsOk)
                {
                    Console.Error.WriteLine($"prepare-worker-environment failed: {members.RefusalDetail}");
                    return 1;
                }
                var result = WorkerEnvironmentPreparation.Execute(new(parsed.GetValue(source)!, parsed.GetValue(runtime)!, parsed.GetValue(study)!,
                    members.Value.RunBinaryPath,
                    members.Value.CompileBinaryPath), Console.Error, new WorkerEnvironmentPreparation.ExecutionHost());
                Console.Out.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"prepare-worker-environment failed: {error.Message}");
                return 1;
            }
        });
        parent.Subcommands.Add(command);
    }
}
