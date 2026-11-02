using Fern.Install;
using System.CommandLine;
using Spectre.Console;

namespace Fern.Run.Cli;

/// <summary>
/// `prune-deploy`: what the deployment root stops keeping. The decision lives in
/// <see cref="DeployRetention"/>; this file is the wiring and the table.
/// </summary>
internal static class DeployRetentionVerbs
{
    public static void Add(Command parent)
    {
        var command = new Command(
            "prune-deploy",
            "Remove published copies no selection or receipt names, keeping the newest of each member; prints the plan and changes nothing without --apply");

        var deploymentRoot = new Option<string>("--deployment-root")
        {
            Description = "Root holding the published members and their receipts.",
            // CHG 2026-09-26: deploy/ moved to runtime/deploy/ (Fern.Registry.RepositoryLayout).
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.DeployDirectory,
        };
        var keep = new Option<int>("--keep")
        {
            Description = "How many of the newest copies of each member to keep beyond what is named by a selection or receipt.",
            DefaultValueFactory = _ => 2,
        };
        var apply = new Option<bool>("--apply") { Description = "Delete what the plan names. Without it nothing is removed." };
        var links = new Option<bool>("--current-links")
        {
            Description = "Also point <member-directory>/current at the newest copy, so commands can name a readable path.",
        };
        // CHG 2026-09-26 (slice F6): what loaded units and Site declarations name
        // is kept too, read through the Gleam flow; --apply needs it.
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "Exported Gleam scheduler program; says which binaries loaded units and Site declarations name. --apply requires it.",
        };
        var profilesRoot = new Option<string>("--profiles-root")
        {
            Description = "Directory of the Site declarations.",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.ProfilesDirectory,
        };
        var registry = new Option<string?>("--registry")
        {
            Description = "Registry Home whose registered studies' compilers are kept too.",
        };
        foreach (var option in new Option[] { deploymentRoot, keep, apply, links, schedulerProgram, profilesRoot, registry }) command.Options.Add(option);

        command.SetAction(parsed =>
        {
            var root = Path.GetFullPath(parsed.GetValue(deploymentRoot)!);
            IReadOnlySet<string>? named = null;
            if (parsed.GetValue(schedulerProgram) is { } shipment)
            {
                try
                {
                    using var program = Fern.Scheduler.GleamProgram.FromShipment(shipment);
                    var kept = new HashSet<string>(Fern.Scheduler.SiteFiles.NamedDigests(
                        program, parsed.GetValue(profilesRoot)!, new Fern.Scheduler.SiteFiles.SystemctlUser().LoadedServices()), StringComparer.Ordinal);
                    if (parsed.GetValue(registry) is { } home)
                    {
                        using var reader = Fern.Registry.RegistryHome.OpenReader(home);
                        kept.UnionWith(Fern.Registry.RegistryOperations.ListStudyCompilerDigests(reader.Connection));
                    }
                    kept.UnionWith(DeployRetention.RunningDigests());
                    named = kept;
                }
                catch (Exception error) when (error is Fern.Scheduler.GleamProgramException or Fern.Registry.RegistryFault or InvalidOperationException or IOException)
                {
                    Console.Error.WriteLine($"prune-deploy: what units, Site declarations and registered studies name could not be read, so nothing is planned: {error.Message}");
                    return 1;
                }
            }
            else if (parsed.GetValue(apply))
            {
                Console.Error.WriteLine("prune-deploy: --apply needs --scheduler-program, so that what loaded units and Site declarations name is kept");
                return 2;
            }
            var plan = DeployRetention.Compute(root, parsed.GetValue(keep), named);
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("reason");
            table.AddColumn("files");
            table.AddColumn("size");
            foreach (var group in plan.Removals.GroupBy(removal => removal.Reason).OrderByDescending(group => group.Sum(removal => removal.Bytes)))
            {
                table.AddRow(group.Key, group.Count().ToString(), Gibibytes(group.Sum(removal => removal.Bytes)));
            }
            table.AddRow("[bold]total[/]", plan.Removals.Count.ToString(), $"[bold]{Gibibytes(plan.Bytes)}[/]");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"kept: {plan.Kept.Count} file(s) a selection, a receipt, a loaded unit, a Site declaration or recency protects" + (named is null ? " (units and declarations not read: plan only)" : ""));

            if (!parsed.GetValue(apply))
            {
                AnsiConsole.MarkupLine("[yellow]plan only[/]; pass --apply to remove them");
                return 0;
            }
            var freed = DeployRetention.Apply(plan);
            AnsiConsole.MarkupLine($"removed {plan.Removals.Count} file(s), {Gibibytes(freed)} freed");
            foreach (var removal in plan.Removals) Console.WriteLine($"  removed {removal.Path} ({removal.Reason})");
            foreach (var link in parsed.GetValue(links) ? DeployRetention.RefreshCurrentLinks(root) : [])
            {
                AnsiConsole.MarkupLine($"  {link}");
            }
            return 0;
        });
        parent.Subcommands.Add(command);
    }

    static string Gibibytes(long bytes) => $"{bytes / 1024d / 1024 / 1024:F2} GiB";
}
