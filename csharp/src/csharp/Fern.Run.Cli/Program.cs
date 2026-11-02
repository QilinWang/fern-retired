using System.CommandLine;
using Fern.Run.Cli;

// fern-run (C#): the operator command surface, verb by verb, replacing
// fern-cli-rs. Each verb parses its arguments here and calls one typed owner;
// no verb keeps logic of its own. The Rust `enum Command` reading order
// (docs/how-to/workflow.md#fern-run-cli-command-waterfall) is kept as the order the
// verbs are added below.

var root = new RootCommand("Fern experiments: plan, run, monitor and maintain registered work.");

// CLI Pass 1 — config and path discovery. The recipe catalog verbs retired on
// 2026-09-04 (retired/csharp/recipe-route-2026-09-04).
var version = new Command("version", "Print this binary's identity: version, built-from commit, dirty flag, protocol.");
version.SetAction(_ =>
{
    var report = BinaryVersionReport.FromThisBinary();
    Console.Error.WriteLine(report.OneLine());
    Console.Out.WriteLine(report.PrettyJson());
    return 0;
});
root.Subcommands.Add(version);
MachineAdmissionVerificationVerbs.Add(root);
ProfileVerbs.Add(root);
ControlPlaneInstallVerbs.Add(root);
DeployRetentionVerbs.Add(root);
StudyStoreVerbs.Add(root);
RunStudyCellsVerbs.Add(root);
// A layer over the governance verbs, registered after them so it can dispatch to them.
GovernWiring.Add(root);
ServeRegistryHomeVerbs.Add(root);
WorkerEnvironmentPreparationVerbs.Add(root);

// CLI Pass 2 — parent runners and lifecycle control.
ProviderLifecycleVerbs.Add(root);

// CLI Pass 4 — run registry views.
SiteAndAdmissionVerbs.Add(root);
// A Site's files against its declarations (how-a-site-is-reached-and-started, F2).
SiteFilesVerbs.Add(root);

// CLI Pass 6 — lifecycle and storage maintenance.
RegistryMaintenanceVerbs.Add(root);
StorageReclaimVerbs.Add(root);

// Moved off the retiring Go fern-card: the Corrections digest every session
// loads, and the content-addressed fern-registry publish that outlived the
// knowledge reconcile service it was installed for.
CorrectionsDigestVerbs.Add(root);
RegistryBinaryPublishVerbs.Add(root);
RuntimeSelectionVerbs.Add(root);
// CHG 2026-09-12: a machine downloads its runtime instead of being pushed one.
RuntimeReleaseVerbs.Add(root);

// install-scheduler-service is a pass-through to the C# installer, which owns
// its flags and its own --help (Rust: trailing_var_arg + disable_help_flag).
// It is routed before parsing, because the parser below would claim --help
// and refuse flags it does not know. See SchedulerLifecycleVerbs.

// clap exits 2 on a usage error and prints the errors before the usage line;
// System.CommandLine would exit 1, which scripts read as a generic failure.
// Resolve an omitted operational Registry once, after help/pass-through routing.
// Option construction and help never open an installation or a database.
var selectedCommand = args.Length == 0 ? null : root.Subcommands.FirstOrDefault(command => command.Name == args[0]);
var registryOption = selectedCommand?.Options.FirstOrDefault(option => option.Name == "--registry");
if (registryOption is { Required: false } && !args.Any(argument => argument is "--help" or "-h")
    // CHG: a remote Home is an explicit store choice; do not append a local one.
    && !(selectedCommand!.Options.Any(option => option.Name == "--home")
        && args.Any(argument => argument == "--home" || argument.StartsWith("--home=", StringComparison.Ordinal)))
    && !args.Any(argument => argument == "--registry" || argument.StartsWith("--registry=", StringComparison.Ordinal)))
{
    try { args = [.. args, "--registry", Fern.Registry.RegistryHome.DefaultRegistryPath]; }
    catch (Fern.Registry.RegistryFault error) { Console.Error.WriteLine(error.Message); return 2; }
}
var parsed = root.Parse(args);
if (parsed.Errors.Count > 0)
{
    foreach (var error in parsed.Errors)
    {
        Console.Error.WriteLine($"error: {error.Message}");
    }
    return 2;
}
// CHG: Async invocation owns signal cancellation for observation actions;
// synchronous command actions retain their existing dispatch and exit behavior.
return await parsed.InvokeAsync();
