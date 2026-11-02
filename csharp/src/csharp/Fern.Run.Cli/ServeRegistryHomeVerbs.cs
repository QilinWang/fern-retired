using System.CommandLine;
using System.Runtime.InteropServices;
using Fern.Registry;
using Fern.Scheduler;

namespace Fern.Run.Cli;

/// <summary>
/// <c>serve-registry-home</c>: the machine that holds the Registry file
/// answers the store operations a supervisor on another machine makes, as
/// JSON over HTTP on a loopback address. CHG 2026-09-11 night (slice 5 of the
/// cell port, pull): the pod runs <c>run-study-cells --serve --home</c>
/// against this, through the SSH tunnel's remote forward, and claims, beats
/// for and finishes its own cells the way the local supervisor does against
/// the file.
/// </summary>
public static class ServeRegistryHomeVerbs
{
    /// <summary>How often the Home reads what needs a person.</summary>
    public static readonly TimeSpan HomeNoticesEvery = TimeSpan.FromMinutes(1);

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(ServeRegistryHome());
        parent.Subcommands.Add(WhatStopsWork());
        parent.Subcommands.Add(LetItRun());
    }

    /// <summary>
    /// CHG 2026-09-26: what stops work and what running trainings a person
    /// should know about, read from the Registry now, in the flow's words.
    /// </summary>
    static Command WhatStopsWork()
    {
        var registry = new Option<string>("--registry") { Description = "The Registry Home to read.", Required = true };
        var program = new Option<string>("--scheduler-program") { Description = "Exported Gleam scheduler program.", Required = true };
        var json = new Option<bool>("--json") { Description = "The flow's whole answer, for a page to read." };
        var command = new Command("what-stops-work",
            "Print what waits for a person, what a person asked to stop, idle Sites, and running trainings a person should know about; changes nothing.");
        foreach (var option in new Option[] { registry, program, json }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var reader = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var store = new SqliteStudyStore(reader);
            using var gleam = GleamProgram.FromShipment(parsed.GetValue(program)!);
            var summary = HomeNotices.Ask(gleam, HomeNotices.ReadingOfEverySite(store, DateTimeOffset.UtcNow),
                new Dictionary<string, HashSet<string>>(), [], new Dictionary<string, long>());
            if (parsed.GetValue(json))
            {
                Console.Out.WriteLine(summary.Answer.ToJsonString());
                return 0;
            }
            if (summary.Items.Count == 0) Console.Out.WriteLine("nothing waits for a person");
            foreach (var item in summary.Items)
            {
                var who = item.WaitsFor == "who_asked" ? $"asked by {item.By}" : "a person must act";
                Console.Out.WriteLine($"[{who}] {item.Sentence}");
                Console.Out.WriteLine($"    key {item.Key}");
            }
            return 0;
        });
        return command;
    }

    /// <summary>CHG 2026-09-26 (slice W3): a person's answer to a warning about a training: let it run.</summary>
    static Command LetItRun()
    {
        var key = new Option<string>("--key") { Description = "The item's key, as what-stops-work prints it.", Required = true };
        var by = new Option<string>("--by") { Description = "Who answers.", Required = true };
        var directory = new Option<string>("--notices-directory")
        {
            Description = "The Home's notices folder: the Registry's folder unless the Home was given another.",
            Required = true,
        };
        var command = new Command("let-it-run",
            "Answer a warning about a running training (no progress, silent) with: let it run. That warning is not told again; a new one is.");
        foreach (var option in new Option[] { key, by, directory }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            HomeNotices.Notifier.LetRun(parsed.GetValue(directory)!, parsed.GetValue(key)!, parsed.GetValue(by)!, DateTimeOffset.UtcNow);
            Console.Out.WriteLine($"let-it-run: {parsed.GetValue(key)} is not told again");
            return 0;
        });
        return command;
    }

    static Command ServeRegistryHome()
    {
        var registry = new Option<string>("--registry")
        {
            Description = "The Registry Home to serve: the file on this machine.",
            Required = true,
        };
        var listen = new Option<string>("--listen")
        {
            Description = "The loopback address and port to answer on. The pod reaches it through the SSH tunnel's "
                        + "remote forward (ssh -R 8621:127.0.0.1:8621 <pod>), which is what keeps the Home off the network.",
            DefaultValueFactory = _ => HomeService.DefaultListen,
        };
        // CHG 2026-09-26 (slices R1, R3, W2, W3): with the scheduler program the
        // Home also reads its Registry every minute and shows what needs a
        // person as one notification on this machine's desktop.
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "Exported Gleam scheduler program. With it, every minute the Home reads what stops work and what running "
                        + "trainings a person should know about, and shows what is due on this machine's desktop.",
        };
        var noticesDirectory = new Option<string?>("--notices-directory")
        {
            Description = "Where what was told and the let-it-run answers are kept; the Registry's folder when not given.",
        };
        var command = new Command("serve-registry-home",
            "Answer the store operations a supervisor on another machine makes (run-study-cells --home), as JSON over HTTP, "
            + "from this machine's Registry Home. Stays until Ctrl-C or SIGTERM.");
        command.Options.Add(registry);
        command.Options.Add(listen);
        command.Options.Add(schedulerProgram);
        command.Options.Add(noticesDirectory);
        command.SetAction(parsed =>
        {
            var path = parsed.GetValue(registry)!;
            var address = parsed.GetValue(listen)!;
            using var writer = RegistryHome.OpenWriter(path);
            using var program = parsed.GetValue(schedulerProgram) is { } shipment ? GleamProgram.FromShipment(shipment) : null;
            using var service = new HomeService(writer, address, Console.Error);
            try
            {
                service.Start();
            }
            catch (System.Net.HttpListenerException error)
            {
                Console.Error.WriteLine($"serve-registry-home: cannot listen at {address}: {error.Message}");
                return 2;
            }
            Console.Out.WriteLine($"serve-registry-home: the Home at {path} answers at {service.Prefix}; Ctrl-C stops it");
            using var stopping = new ManualResetEventSlim(false);
            Thread? notices = null;
            if (program is not null)
            {
                var directory = parsed.GetValue(noticesDirectory) ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
                HomeNotices.IDesktop? desktop = File.Exists(HomeNotices.WindowsToast.DefaultPowershell)
                    ? new HomeNotices.WindowsToast(HomeNotices.WindowsToast.DefaultPowershell)
                    : null;
                if (desktop is null) Console.Error.WriteLine("serve-registry-home: no Windows desktop here; notices go to this log only");
                var notifier = new HomeNotices.Notifier(directory, desktop, Console.Error,
                    which => service.WithStore(store => HomeNotices.GiveUpSilent(store, which)));
                notices = new Thread(() =>
                {
                    while (!stopping.Wait(TimeSpan.Zero))
                    {
                        try
                        {
                            var reading = service.WithStore(store => HomeNotices.ReadingOfEverySite(store, DateTimeOffset.UtcNow));
                            notifier.Tick(reading, program);
                        }
                        // CHG 2026-09-26 (review): nothing the notices meet ends the Home.
                        catch (Exception error)
                        {
                            Console.Error.WriteLine($"serve-registry-home: the notices could not be read this minute: {error.Message}");
                        }
                        stopping.Wait(HomeNoticesEvery);
                    }
                }) { IsBackground = true, Name = "fern-home-notices" };
                notices.Start();
                Console.Out.WriteLine($"serve-registry-home: notices every {HomeNoticesEvery.TotalSeconds:F0} s, kept in {directory}");
            }
            Console.CancelKeyPress += (_, key) =>
            {
                key.Cancel = true;
                stopping.Set();
            };
            using var terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stopping.Set();
            });
            stopping.Wait();
            notices?.Join(TimeSpan.FromSeconds(70));
            service.Stop();
            Console.Out.WriteLine("serve-registry-home: stopped as asked");
            return 0;
        });
        return command;
    }
}
