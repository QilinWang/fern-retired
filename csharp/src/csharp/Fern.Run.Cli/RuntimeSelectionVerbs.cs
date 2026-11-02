using System.CommandLine;
using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Fern.Install;

namespace Fern.Run.Cli;

internal static class RuntimeSelectionVerbs
{
    internal static void Add(Command parent)
    {
        var command = new Command("select-runtime", "Select verified installed programs and their Registry for local operational consumers.");
        var receipt = new Option<string>("--receipt") { Required = true };
        var registry = new Option<string>("--registry") { Required = true };
        var binary = new Option<string>("--registry-binary") { Required = true };
        var deployment = new Option<string>("--deployment-root") { Required = true };
        command.Options.Add(receipt); command.Options.Add(registry);
        command.Options.Add(binary); command.Options.Add(deployment);
        command.SetAction(result =>
        {
            try
            {
                var selected = RuntimeSelectionPublication.Select(result.GetRequiredValue(receipt),
                    result.GetRequiredValue(registry), result.GetRequiredValue(binary), result.GetRequiredValue(deployment));
                Console.WriteLine(JsonSerializer.Serialize(selected));
                return 0;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or RegistryFault or KeyNotFoundException)
            { Console.Error.WriteLine($"select-runtime refused: {error.Message}"); return 2; }
        });
        parent.Subcommands.Add(command);
    }
}
