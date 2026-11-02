using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;
using Fern.Scheduler;
using Microsoft.Data.Sqlite;
using Fern.Install;

namespace Fern.Run.Cli;

/// <summary>
/// The commands that read and write the one-store schema: a registered study,
/// its cells and their attempts.
///
/// They are added beside the old Plan verbs, not in place of them, because
/// slice 3 is what removes the old layer. Until then a Home holds both sets of
/// tables and each command says plainly which it reads, so nobody has to guess
/// from a flag name which era they are in.
///
/// The word Plan does not appear on this surface. The registered object is the
/// study, and `--study` names it by the name it was registered under.
/// </summary>
public static class StudyStoreVerbs
{
    public static void Add(Command parent)
    {
        parent.Subcommands.Add(RegisterStudy());
        parent.Subcommands.Add(CheckCompiledStudy());
        parent.Subcommands.Add(CheckStudyPlacement());
        parent.Subcommands.Add(MonitorStudy());
        parent.Subcommands.Add(ListStudies());
        parent.Subcommands.Add(MonitorLine());
        parent.Subcommands.Add(ListAcceptedCells());
        parent.Subcommands.Add(CancelStudy());
        parent.Subcommands.Add(RetryCell());
        parent.Subcommands.Add(CollectStudy());
        parent.Subcommands.Add(NameCheckpoint());
        parent.Subcommands.Add(RegisterStudySource());
        parent.Subcommands.Add(RegisterSite());
        // CHG 2026-09-11 night (slice 1 of the cell port): the two verbs that
        // address a Site's resident supervisor.
        parent.Subcommands.Add(StopSupervisor());
        parent.Subcommands.Add(InstallSupervisorService());
        // CHG 2026-09-11 night (slice 2 of the cell port): pause, hold and release.
        parent.Subcommands.Add(PauseCells());
        parent.Subcommands.Add(HoldCell());
        parent.Subcommands.Add(ReleaseRequest());
        // CHG 2026-09-11 night (slice 3 of the cell port): a Site a person takes out of service, and back.
        parent.Subcommands.Add(DisableSite());
        parent.Subcommands.Add(EnableSite());
        // CHG 2026-09-11 night (slice 7 of the cell port): the order of the offer, and what a Site is doing.
        parent.Subcommands.Add(SetPriority());
        // CHG 2026-09-25 (slice P2 of the scheduling loop): the one move, to the back of a rank.
        parent.Subcommands.Add(MoveLast());
        // CHG 2026-09-25 (slice M2 of the stop design): which compiler compiled a study.
        parent.Subcommands.Add(SetStudyCompiler());
        parent.Subcommands.Add(MonitorSite());
    }

    // ── set-priority ─────────────────────────────────────────────────────

    /// <summary>
    /// Where in the offer a study or a cell goes (slice 7, row 12). The
    /// retired queue edit moved a Plan to a position; a priority says the
    /// same without renumbering the rest, and a running cell is never moved
    /// because it is not in the offer.
    ///
    /// CHG 2026-09-25 (slice P2 of the scheduling loop): the priority is one
    /// of four ranks, highest first immediate, urgent, normal, slow; a cell's
    /// own overrides its study's. Setting the same rank again moves nothing.
    /// </summary>
    static Command SetPriority()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var cell = new Option<string?>("--cell") { Description = "One cell of the study by name; without it the study itself." };
        var priority = new Option<string>("--priority")
        {
            Description = "immediate, urgent, normal or slow, highest first; normal is what everything has without a setting.",
            Required = true,
        };
        var reason = new Option<string>("--reason") { Description = "Why, in words a person reads on the monitor.", Required = true };
        var by = new Option<string>("--by") { Description = "Who sets it.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("set-priority",
            "Set the priority of a study or one of its cells: immediate, urgent, normal or slow; a running cell is never moved.");
        foreach (var option in new Option[] { registry, study, cell, priority, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            if (WorkScope(writer, "set-priority", parsed.GetValue(study)!, parsed.GetValue(cell)) is not { } scope) return 3;
            var set = RegistryOperations.SetPriority(writer, scope, parsed.GetValue(priority)!, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!set.TryGetValue(out var recorded))
            {
                set.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"set-priority refused: {refusal.Detail}");
                return 3;
            }
            Console.Out.WriteLine($"priority {recorded.Priority} over {recorded.Scope} set by {recorded.SetBy}: {recorded.Reason}");
            return 0;
        });
        return command;
    }

    // ── move-last ────────────────────────────────────────────────────────

    /// <summary>
    /// Move a study or a cell to the back of its rank, once (slice P2 of the
    /// scheduling loop): behind every study registered so far, ahead of every
    /// study registered afterwards. Its rank does not change; to put work
    /// behind even the slow cells, make it slow and then move it last.
    /// </summary>
    static Command MoveLast()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var cell = new Option<string?>("--cell") { Description = "One cell of the study by name; without it the study itself." };
        var reason = new Option<string>("--reason") { Description = "Why, in words a person reads on the monitor.", Required = true };
        var by = new Option<string>("--by") { Description = "Who moves it.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("move-last",
            "Move a study or one of its cells behind every cell of its rank waiting now, once; later studies still queue behind it.");
        foreach (var option in new Option[] { registry, study, cell, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            if (WorkScope(writer, "move-last", parsed.GetValue(study)!, parsed.GetValue(cell)) is not { } scope) return 3;
            var moved = RegistryOperations.MoveLast(writer, scope, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!moved.TryGetValue(out var recorded))
            {
                moved.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"move-last refused: {refusal.Detail}");
                return 3;
            }
            Console.Out.WriteLine(
                $"{recorded.Scope} moved behind study {recorded.BehindStudyId} by {recorded.SetBy}: {recorded.Reason}");
            return 0;
        });
        return command;
    }

    // ── set-study-compiler ───────────────────────────────────────────────

    /// <summary>
    /// Record which compiler compiled a study, by the sha256 of its bytes
    /// (slice M2). register-study records it from the compiled study itself; this
    /// is for a study registered before compiled studies named it. A supervisor
    /// whose compiler for the study's source is another then claims none of its
    /// cells, and says which compiler to install.
    /// </summary>
    static Command SetStudyCompiler()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var compiler = new Option<string>("--compiler") { Description = "The fern-compile the study was compiled with.", Required = true };
        var by = new Option<string>("--by") { Description = "Who records it.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("set-study-compiler",
            "Record which compiler compiled a study, by the sha256 of its bytes; a supervisor with another for its source claims none of its cells.");
        foreach (var option in new Option[] { registry, study, compiler, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            var path = Path.GetFullPath(parsed.GetValue(compiler)!);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"set-study-compiler: no compiler at {path}");
                return 3;
            }
            string sha256;
            using (var stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            }
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var name = parsed.GetValue(study)!;
            if (FindStudy(writer.Connection, name) is not { } studyId)
            {
                Console.Error.WriteLine($"set-study-compiler: no study named {name} is registered");
                return 3;
            }
            var set = RegistryOperations.SetStudyCompiler(writer, studyId, sha256, path, parsed.GetValue(by)!);
            if (!set.TryGetValue(out var recorded))
            {
                set.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"set-study-compiler refused: {refusal.Detail}");
                return 3;
            }
            Console.Out.WriteLine($"study {name} was compiled with {recorded.Path} (sha256 {recorded.Sha256})");
            return 0;
        });
        return command;
    }

    /// <summary>The scope a study name and an optional cell name point to, or null after saying why not.</summary>
    static string? WorkScope(RegistryWriter writer, string verb, string name, string? cellName)
    {
        if (FindStudy(writer.Connection, name) is not { } studyId)
        {
            Console.Error.WriteLine($"{verb}: no study named {name} is registered");
            return null;
        }
        if (cellName is null) return RegistryOperations.StudyScope(studyId);
        // CHG 2026-09-26 (slice 5 of the command-line design): the cell is found by
        // RegistryChanges.CellId, which the Home's change routes use too.
        if (RegistryChanges.CellId(writer.Connection, studyId, cellName) is not { } cellId)
        {
            Console.Error.WriteLine($"{verb}: study {name} has no cell named {cellName}");
            return null;
        }
        return RegistryOperations.CellScope(cellId);
    }

    // ── monitor-site ─────────────────────────────────────────────────────

    /// <summary>What a Site is doing (slice 7, row 37): its office, history, requests, sources, cards and open attempts.</summary>
    static Command MonitorSite()
    {
        var registry = RegistryOption();
        var site = new Option<string?>("--site") { Description = "The registered Site by name. One of --site and --all-sites." };
        var allSites = new Option<bool>("--all-sites") { Description = "Every registered Site." };
        // CHG 2026-09-25 (how-a-site-is-reached-and-started, F2): the same
        // check as check-site-files, one line group per Site, when asked for.
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "Also check each Site's files and loaded units against its declarations (check-site-files), "
                        + "with this exported Gleam scheduler program. Reads this machine's files and systemd; changes nothing.",
        };
        var profiles = new Option<string?>("--profiles")
        {
            Description = "With --scheduler-program: the directory of execution profiles and supervisor declarations. Default: config/profiles/ of this repository.",
        };
        var command = new Command("monitor-site",
            "What a registered Site is doing, from durable facts only: who supervises it or that nobody confirmed does, "
            + "its last supervisors, the requests standing over it, the sources it holds, its cards, and the attempts open on it.");
        foreach (var option in new Option[] { registry, site, allSites, schedulerProgram, profiles }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            var name = parsed.GetValue(site);
            var every = parsed.GetValue(allSites);
            if ((name is null) == !every)
            {
                Console.Error.WriteLine("monitor-site: name the Site once, --site <name> or --all-sites");
                return 3;
            }
            using var reader = RegistryHome.OpenReader(parsed.GetValue(registry)!);
            var sites = RegistryOperations.ListSites(reader.Connection).Where(candidate => every || candidate.Name == name).ToList();
            if (sites.Count == 0)
            {
                Console.Error.WriteLine(every ? "monitor-site: no Site is registered" : $"monitor-site: no Site named {name} is registered");
                return 3;
            }
            foreach (var one in sites)
            {
                Console.Out.Write(SiteProjection.Render(reader.Connection, one));
            }
            if (parsed.GetValue(schedulerProgram) is { } shipment)
            {
                return SiteFilesVerbs.PrintChecks(shipment, parsed.GetValue(profiles), sites.Select(one => one.Name).ToList(),
                    "monitor-site", onlyDeclared: true);
            }
            return 0;
        });
        return command;
    }

    static Option<string> RegistryOption() =>
        new("--registry") { Description = "Registry Home holding the study.", Required = true };

    static Option<string> StudyOption() =>
        new("--study") { Description = "The registered study's name.", Required = true };

    static long? FindSite(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM sites WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        var found = command.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
    }

    // ── the resident supervisor of a Site ─────────────────────────────────────

    /// <summary>
    /// `stop-supervisor`: ask the supervisor of one Site, or of every Site, to
    /// exit once it runs nothing. The request is a row; the supervisor consumes
    /// it between cells and exits 0, which its unit does not restart. Recorded
    /// is not stopped: the verb prints the supervisor it addressed, and
    /// `monitor-study` shows the office ending.
    /// </summary>
    static Command StopSupervisor()
    {
        var registry = RegistryOption();
        var site = new Option<string?>("--site") { Description = "The Site whose supervisor should exit; omit with --all-sites." };
        var all = new Option<bool>("--all-sites") { Description = "Address every Site's supervisor." };
        var reason = new Option<string>("--reason") { Description = "Why, in one sentence; monitor prints it.", Required = true };
        var by = new Option<string>("--by") { Description = "Who asks.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("stop-supervisor",
            "Ask a Site's resident supervisor to exit once nothing runs on it; the request is recorded and consumed by the office holder.");
        foreach (var option in new Option[] { registry, site, all, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            var siteName = parsed.GetValue(site);
            var everySite = parsed.GetValue(all);
            if (everySite == (siteName is not null))
            {
                Console.Error.WriteLine("stop-supervisor: name one Site with --site, or every Site with --all-sites, not both and not neither");
                return 2;
            }
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var scope = RegistryOperations.ScopeAll;
            if (siteName is not null)
            {
                if (FindSite(writer.Connection, siteName) is not { } siteId)
                {
                    Console.Error.WriteLine($"stop-supervisor: no Site named {siteName} is registered");
                    return 3;
                }
                scope = RegistryOperations.SiteScope(siteId);
                var holder = RegistryOperations.ReadSupervisor(writer.Connection, siteId);
                var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Console.WriteLine(holder switch
                {
                    null => $"Site {siteName} has never had a supervisor; the request stands for the first one",
                    _ when holder.HoldsOfficeAt(now) => $"Site {siteName} is supervised by {holder.Host} pid {holder.Pid} at epoch {holder.Epoch}; it consumes the request between cells",
                    _ when holder.IsUnconfirmedAt(now) => $"Site {siteName}'s last supervisor ({holder.Host} pid {holder.Pid}) went silent without ending; the request stands for the next one",
                    _ => $"Site {siteName}'s last supervisor ended ({holder.Outcome}); the request stands for the next one",
                });
            }
            var asked = RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, scope, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!asked.TryGetValue(out var id))
            {
                asked.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"stop-supervisor refused: {refusal.Detail}");
                return 3;
            }
            Console.WriteLine($"stop request {id} recorded for {scope}: {parsed.GetValue(reason)}");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// `install-supervisor-service`: write the systemd user unit that keeps this
    /// Site's supervisor resident, from the machine's execution profile. It
    /// enables and starts nothing; the next steps are printed.
    /// </summary>
    static Command InstallSupervisorService()
    {
        var registry = new Option<string?>("--registry") { Description = "The local Registry file; choose this or --home." };
        var home = new Option<string?>("--home") { Description = "The HTTP Home this pull worker answers to; choose this or --registry." };
        var site = new Option<string>("--site") { Description = "The registered Site this machine is.", Required = true };
        var profile = new Option<string>("--profile") { Description = "The execution profile JSON naming this machine's python and work_root.", Required = true };
        var fernRun = new Option<string?>("--fern-run") { Description = "The installed fern-run the unit starts. Default: the selected runtime's." };
        var resultsRoot = new Option<string?>("--results-root") { Description = "The selected results directory cells save into; omitted means not sent." };
        var unitDirectory = new Option<string?>("--unit-directory") { Description = "Where the unit is written. Default: ~/.config/systemd/user." };
        var leaseSeconds = new Option<int>("--lease-seconds") { Description = "The office lease the unit's supervisor holds.", DefaultValueFactory = _ => (int)RegistryOperations.DefaultSupervisorLeaseSeconds };
        var idleSeconds = new Option<int>("--idle-seconds") { Description = "The wait after an idle cycle.", DefaultValueFactory = _ => 30 };
        // CHG 2026-09-26 (slice F7 of how-a-site-is-reached-and-started): the unit carries the Gleam
        // program, the compiler and the environment the supervisor needs, and a PATH with erl on it.
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "The exported Gleam scheduler program the supervisor runs with; the unit's PATH then includes the folder erl is in, found on this shell's PATH.",
        };
        var fernCompile = new Option<string?>("--fern-compile") { Description = "The fern-compile the supervisor compiles cards with." };
        var environmentPairs = new Option<string[]>("--environment")
        {
            Description = "NAME=VALUE the supervisor starts with, such as OMP_NUM_THREADS=1; may repeat.",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("install-supervisor-service",
            "Write the systemd user unit that keeps this Site's supervisor resident; it enables and starts nothing.");
        foreach (var option in new Option[] { registry, home, site, profile, fernRun, resultsRoot, unitDirectory, leaseSeconds, idleSeconds,
                     schedulerProgram, fernCompile, environmentPairs }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            if ((parsed.GetValue(registry) is null) == (parsed.GetValue(home) is null))
            {
                Console.Error.WriteLine("install-supervisor-service: choose exactly one of --registry or --home");
                return 3;
            }
            var registryPath = parsed.GetValue(home) ?? Path.GetFullPath(parsed.GetValue(registry)!);
            if (parsed.GetValue(home) is { } selectedHome)
            {
                if (!Uri.TryCreate(selectedHome, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                {
                    Console.Error.WriteLine("install-supervisor-service: --home must be an HTTP or HTTPS URL");
                    return 3;
                }
                using var client = new HomeClient(new Uri(selectedHome));
                if (!client.ListSites().Any(candidate => candidate.Name == parsed.GetValue(site)))
                {
                    Console.Error.WriteLine($"install-supervisor-service: no Site named {parsed.GetValue(site)} is registered at {selectedHome}");
                    return 3;
                }
            }
            else
            {
                using var reader = RegistryHome.OpenReader(registryPath);
                if (FindSite(reader.Connection, parsed.GetValue(site)!) is null)
                {
                    Console.Error.WriteLine($"install-supervisor-service: no Site named {parsed.GetValue(site)} is registered in {registryPath}");
                    return 3;
                }
            }
            string python, workRoot;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(parsed.GetValue(profile)!));
                var root = document.RootElement;
                python = Required(root, "python");
                workRoot = Required(root, "work_root");
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"install-supervisor-service: the profile cannot be read: {error.Message}");
                return 3;
            }
            var binary = parsed.GetValue(fernRun) ?? Fern.Registry.RuntimeSelection.Read().RunBinary;
            string? program = parsed.GetValue(schedulerProgram) is { } shipment ? Path.GetFullPath(shipment) : null;
            string? erlang = null;
            if (program is not null)
            {
                erlang = (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "erl")));
                if (erlang is null)
                {
                    Console.Error.WriteLine("install-supervisor-service: --scheduler-program needs erl, and no folder on this shell's PATH holds it");
                    return 3;
                }
            }
            var pairs = new List<(string, string)>();
            foreach (var pair in parsed.GetValue(environmentPairs) ?? [])
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    Console.Error.WriteLine($"install-supervisor-service: --environment takes NAME=VALUE, got {pair}");
                    return 3;
                }
                pairs.Add((pair[..equals], pair[(equals + 1)..]));
            }
            var rendered = StudySupervisorUnit.Render(new StudySupervisorUnitConfig(
                Path.GetFullPath(binary), registryPath, parsed.GetValue(site)!, python, workRoot,
                parsed.GetValue(resultsRoot) is { } results ? Path.GetFullPath(results) : null,
                parsed.GetValue(leaseSeconds), parsed.GetValue(idleSeconds),
                program, erlang,
                parsed.GetValue(fernCompile) is { } compiler ? Path.GetFullPath(compiler) : null,
                pairs));
            byte[] unitBytes;
            switch (rendered)
            {
                case Refused refused:
                    Console.Error.WriteLine($"install-supervisor-service refused: {refused.Detail}");
                    return 3;
                case Accepted<byte[]> accepted:
                    unitBytes = accepted.Value;
                    break;
                default:
                    throw new InvalidOperationException($"unit render was {rendered}");
            }
            var directory = parsed.GetValue(unitDirectory)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");
            Directory.CreateDirectory(directory);
            var unitPath = Path.Combine(directory, StudySupervisorUnit.UnitName(parsed.GetValue(site)!));
            File.WriteAllBytes(unitPath, unitBytes);
            Console.WriteLine($"unit written: {unitPath}");
            Console.WriteLine("not enabled or started; the Site's office is unchanged");
            Console.WriteLine("next: systemctl --user daemon-reload");
            Console.WriteLine($"then, when ready: systemctl --user enable --now {StudySupervisorUnit.UnitName(parsed.GetValue(site)!)}");
            Console.WriteLine("note: a user unit does not survive logout or WSL shutdown until the host enables user lingering.");
            return 0;
        });
        return command;

        static string Required(System.Text.Json.JsonElement root, string field) =>
            root.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"the profile declares no {field}");
    }

    // ── pause, hold, release ──────────────────────────────────────────────────
    //
    // The retired `pause-scheduling` / `release-scheduling-pause` (CycleOffice)
    // and the dispatch queue's holds, as operator requests the claim reads.
    // Their names differ from the retired verbs because both sets are in the
    // build until slice 8.

    /// <summary>`pause-cells`: no new cell is claimed over the scope until the pause is released; running cells finish.</summary>
    static Command PauseCells()
    {
        var registry = RegistryOption();
        var all = new Option<bool>("--all-sites") { Description = "Pause every Site." };
        var site = new Option<string?>("--site") { Description = "Pause one Site." };
        var study = new Option<string?>("--study") { Description = "Pause one study on every Site." };
        var reason = new Option<string>("--reason") { Description = "Why, in one sentence.", Required = true };
        // CHG 2026-09-26 (slice R1; owner: "agent hold is agent hold, agent needs to name
        // itself; me is me if i order it says me"): who placed a pause or hold is named,
        // never defaulted to this machine's user; the reminder two hours later goes to them.
        var by = new Option<string>("--by") { Description = "Who asks: an agent names itself; a pause the owner orders names him.", Required = true };
        var command = new Command("pause-cells",
            "Stop new cells being claimed over every Site, one Site or one study; cells already running finish. Released with release-request.");
        foreach (var option in new Option[] { registry, all, site, study, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            var chosen = (parsed.GetValue(all) ? 1 : 0) + (parsed.GetValue(site) is null ? 0 : 1) + (parsed.GetValue(study) is null ? 0 : 1);
            if (chosen != 1)
            {
                Console.Error.WriteLine("pause-cells: choose exactly one of --all-sites, --site or --study");
                return 2;
            }
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            string scope;
            if (parsed.GetValue(all))
            {
                scope = RegistryOperations.ScopeAll;
            }
            else if (parsed.GetValue(site) is { } siteName)
            {
                if (FindSite(writer.Connection, siteName) is not { } siteId)
                {
                    Console.Error.WriteLine($"pause-cells: no Site named {siteName} is registered");
                    return 3;
                }
                scope = RegistryOperations.SiteScope(siteId);
            }
            else
            {
                if (FindStudy(writer.Connection, parsed.GetValue(study)!) is not { } studyId)
                {
                    Console.Error.WriteLine($"pause-cells: no study named {parsed.GetValue(study)} is registered");
                    return 3;
                }
                scope = RegistryOperations.StudyScope(studyId);
            }
            var asked = RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, scope, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!asked.TryGetValue(out var id))
            {
                asked.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"pause-cells refused: {refusal.Detail}");
                return 3;
            }
            Console.WriteLine($"pause {id} recorded over {scope}: {parsed.GetValue(reason)}; release it with release-request --request {id}");
            return 0;
        });
        return command;
    }

    /// <summary>`hold-cell`: one cell, or every cell of one study, is not claimed until the hold is released.</summary>
    static Command HoldCell()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var cell = new Option<string?>("--cell") { Description = "The cell to hold; omit to hold every cell of the study." };
        var reason = new Option<string>("--reason") { Description = "Why, in one sentence.", Required = true };
        // CHG 2026-09-26 (slice R1; owner: "agent hold is agent hold, agent needs to name
        // itself; me is me if i order it says me"): who placed a pause or hold is named,
        // never defaulted to this machine's user; the reminder two hours later goes to them.
        var by = new Option<string>("--by") { Description = "Who asks: an agent names itself; a pause the owner orders names him.", Required = true };
        var command = new Command("hold-cell",
            "Keep one cell, or every cell of a study, from being claimed until the hold is released; a cell already running finishes.");
        foreach (var option in new Option[] { registry, study, cell, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            if (FindStudy(writer.Connection, parsed.GetValue(study)!) is not { } studyId)
            {
                Console.Error.WriteLine($"hold-cell: no study named {parsed.GetValue(study)} is registered");
                return 3;
            }
            var scope = RegistryOperations.StudyScope(studyId);
            if (parsed.GetValue(cell) is { } cellName)
            {
                // CHG 2026-09-26 (slice 5 of the command-line design): found by
                // RegistryChanges.CellId, which the Home's change routes use too.
                if (RegistryChanges.CellId(writer.Connection, studyId, cellName) is not { } cellId)
                {
                    Console.Error.WriteLine($"hold-cell: study {parsed.GetValue(study)} has no cell named {cellName}");
                    return 3;
                }
                scope = RegistryOperations.CellScope(cellId);
            }
            var asked = RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, scope, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!asked.TryGetValue(out var id))
            {
                asked.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"hold-cell refused: {refusal.Detail}");
                return 3;
            }
            Console.WriteLine($"hold {id} recorded over {scope}: {parsed.GetValue(reason)}; release it with release-request --request {id}");
            return 0;
        });
        return command;
    }

    /// <summary>`release-request`: lift a standing pause or hold by the id its verb printed.</summary>
    static Command ReleaseRequest()
    {
        var registry = RegistryOption();
        var request = new Option<long>("--request") { Description = "The request id pause-cells or hold-cell printed.", Required = true };
        var reason = new Option<string>("--reason") { Description = "Why it is lifted.", Required = true };
        var by = new Option<string>("--by") { Description = "Who lifts it.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("release-request", "Lift a standing pause or hold; a consumed stop cannot be lifted, it already happened.");
        foreach (var option in new Option[] { registry, request, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var released = RegistryOperations.ReleaseRequest(writer, parsed.GetValue(request), parsed.GetValue(by)!, parsed.GetValue(reason)!);
            if (!released.IsOk)
            {
                released.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"release-request refused: {refusal.Detail}");
                return 3;
            }
            Console.WriteLine($"request {parsed.GetValue(request)} released: {parsed.GetValue(reason)}");
            return 0;
        });
        return command;
    }

    // ── a Site out of service, and back ──────────────────────────────────────
    //
    // The retired `site disable` / `site enable` (WorkerSiteAuthority.SetEnabled),
    // as a standing hold over the Site's scope: the claim refuses it and the loop
    // skips it until a person enables it again. Cells already running finish.

    static Command DisableSite()
    {
        var registry = RegistryOption();
        var site = new Option<string>("--site") { Description = "The Site to take out of service.", Required = true };
        var reason = new Option<string>("--reason") { Description = "Why, in one sentence.", Required = true };
        // CHG 2026-09-26 (slice R1, review): disabling a Site places a hold, and whoever places
        // a hold names itself.
        var by = new Option<string>("--by") { Description = "Who asks: an agent names itself; a hold the owner orders names him.", Required = true };
        var command = new Command("disable-site", "Take one Site out of service: no cell is claimed on it until enable-site; cells already running finish.");
        foreach (var option in new Option[] { registry, site, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            if (FindSite(writer.Connection, parsed.GetValue(site)!) is not { } siteId)
            {
                Console.Error.WriteLine($"disable-site: no Site named {parsed.GetValue(site)} is registered");
                return 3;
            }
            // CHG 2026-09-26 (slice 5 of the command-line design): the decision is
            // RegistryChanges.DisableSite, which POST /v1/sites/<name>/stop calls too.
            var disabled = RegistryChanges.DisableSite(writer, siteId, parsed.GetValue(site)!, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!disabled.IsOk)
            {
                Console.Error.WriteLine(disabled.Refusal);
                return 3;
            }
            Console.WriteLine(disabled.Value);
            return 0;
        });
        return command;
    }

    static Command EnableSite()
    {
        var registry = RegistryOption();
        var site = new Option<string>("--site") { Description = "The Site to put back in service.", Required = true };
        var reason = new Option<string>("--reason") { Description = "Why, in one sentence.", Required = true };
        var by = new Option<string>("--by") { Description = "Who asks.", DefaultValueFactory = _ => Environment.UserName };
        var command = new Command("enable-site", "Put one Site back in service by releasing every standing hold over it.");
        foreach (var option in new Option[] { registry, site, reason, by }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            if (FindSite(writer.Connection, parsed.GetValue(site)!) is not { } siteId)
            {
                Console.Error.WriteLine($"enable-site: no Site named {parsed.GetValue(site)} is registered");
                return 3;
            }
            // CHG 2026-09-26 (slice 5 of the command-line design): the decision is
            // RegistryChanges.EnableSite, which POST /v1/sites/<name>/start calls too.
            var enabled = RegistryChanges.EnableSite(writer, siteId, parsed.GetValue(site)!, parsed.GetValue(reason)!, parsed.GetValue(by)!);
            if (!enabled.IsOk)
            {
                Console.Error.WriteLine(enabled.Refusal);
                return 3;
            }
            Console.WriteLine(enabled.Value);
            return 0;
        });
        return command;
    }

    // ── mint the rows a study is registered against ──────────────────────────

    /// <summary>
    /// Freeze the working tree at HEAD and register it as this store's source.
    /// `register-study --source <id>` takes the id this prints.
    /// </summary>
    /// <remarks>
    /// The freeze is `SourceFreeze.FreezeAt`, unchanged: it reads the small
    /// identity views — base commit, tracked path set, tracked patch, each
    /// declared untracked input — on both sides of the byte copy and refuses
    /// the whole operation if any of them moved.
    /// </remarks>
    static Command RegisterStudySource()
    {
        var registry = RegistryOption();
        var study = new Option<string?>("--study")
        {
            Description = "A compiled study whose declared data inputs join the source. Omit for the code alone.",
            HelpName = "COMPILED.JSON",
        };
        var fromWorktree = new Option<bool>("--from-worktree")
        {
            Description = "Declare a file from the working tree even when it differs from the commit. Without it a dirty "
                        + "declared file is refused by name, because a run registered at a commit that does not carry its "
                        + "bytes cannot be reproduced from that commit.",
        };
        var sourceFiles = new Option<string[]>("--source-file")
        {
            Description = "Explicit additional runtime source files, including untracked modules; requires --from-worktree. Unrelated files are ignored.",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command(
            "register-study-source",
            "Register the declared source at HEAD -- the commit and every file of it, with a digest each -- and print the id "
            + "register-study takes. Copies no bytes: git holds them.");
        command.Options.Add(registry);
        command.Options.Add(study);
        command.Options.Add(fromWorktree);
        command.Options.Add(sourceFiles);
        command.SetAction(parsed =>
        {
            try
            {
                var workspaceRoot = StudySourceRegistration.WorkspaceRoot(Directory.GetCurrentDirectory());
                using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
                var registered = StudySourceRegistration.Register(
                    writer, workspaceRoot, parsed.GetValue(study), parsed.GetValue(fromWorktree), parsed.GetValue(sourceFiles));
                Console.Out.WriteLine(registered.SourceId.ToString(CultureInfo.InvariantCulture));
                Console.Error.WriteLine(
                    $"{(registered.AlreadyRegistered ? "already registered" : "registered")} source {registered.SourceId}: "
                    + $"{registered.FileCount} declared file(s) at {registered.CommitSha}");
                foreach (var path in registered.ChangedFiles)
                {
                    Console.Error.WriteLine($"changed since the previous source: {path}");
                }
                // CHG 2026-09-27: an uncommitted edit the study's cells do not load is left at the commit.
                foreach (var path in registered.LeftAtCommit)
                {
                    Console.Error.WriteLine($"left at the commit (uncommitted, not loaded by this study's cells): {path}");
                }
                return 0;
            }
            catch (StudySourceRegistration.Refusal refusal)
            {
                Console.Error.WriteLine($"register-study-source refused: {refusal.Message}");
                return 3;
            }
            catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"register-study-source failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    /// <summary>
    /// Register one Worker Site from its execution profile, which is where a
    /// machine's executor, address, roots and interpreter are already written.
    /// </summary>
    static Command RegisterSite()
    {
        var registry = RegistryOption();
        var site = new Option<string>("--site") { Description = "The name to register the Site under.", Required = true };
        var profile = new Option<string>("--profile") { Description = "Execution profile JSON for that machine.", Required = true };
        // CHG 2026-09-11 night (slice 3 of the cell port, rows 4 and 29): the
        // machine declares its cards, by hand or by asking nvidia-smi on itself;
        // registering the same name again is the refresh.
        var devices = new Option<string[]>("--device")
        {
            Description = "A card this machine holds, by GPU UUID; repeatable. One cell runs per declared card.",
            AllowMultipleArgumentsPerToken = false,
        };
        var observe = new Option<bool>("--observe-cards")
        {
            Description = "Ask nvidia-smi on this machine for its cards and declare every one; run on the machine being registered.",
        };
        var command = new Command(
            "register-site",
            "Register one Worker Site from its execution profile; reads the profile and writes one row, nothing else. Run again to refresh.");
        foreach (var option in new Option[] { registry, site, profile, devices, observe }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            try
            {
                var declared = SiteRegistration.Read(parsed.GetValue(profile)!);
                if (!declared.IsOk)
                {
                    Console.Error.WriteLine($"register-site refused: {declared.RefusalDetail}");
                    return 3;
                }
                var cards = new List<string>(declared.Value.Devices);
                foreach (var card in parsed.GetValue(devices) ?? [])
                {
                    if (!cards.Contains(card, StringComparer.Ordinal)) cards.Add(card);
                }
                if (parsed.GetValue(observe))
                {
                    var observed = SiteRegistration.ObserveCards(new WorkerEnvironmentPreparation.ExecutionHost());
                    if (!observed.IsOk)
                    {
                        Console.Error.WriteLine($"register-site refused: {observed.RefusalDetail}");
                        return 3;
                    }
                    foreach (var card in observed.Value)
                    {
                        if (!cards.Contains(card, StringComparer.Ordinal)) cards.Add(card);
                    }
                }
                var declaring = declared.Value with { Devices = cards };
                using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
                var registered = SiteRegistration.Register(writer, parsed.GetValue(site)!, declaring);
                if (!registered.TryGetValue(out var row))
                {
                    registered.TryGetRefusal(out var refusal);
                    Console.Error.WriteLine($"register-site refused: {refusal.Detail}");
                    return 3;
                }
                Console.Out.WriteLine(row.SiteId.ToString(CultureInfo.InvariantCulture));
                Console.Error.WriteLine(
                    $"{(row.AlreadyRegistered ? "updated" : "registered")} Site {parsed.GetValue(site)} "
                    + $"as {declared.Value.Executor} at {declared.Value.SourceRoot}"
                    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2).
                    + (declared.Value.CodeFrom is { } codeFrom
                        ? $", cells take their code from {codeFrom}" + (declared.Value.Repository is { } repository ? $" of {repository}" : "")
                        : "")
                    + (cards.Count == 0 ? ", counted by slots" : $", {cards.Count} card(s): {string.Join(", ", cards)}"));
                // CHG 2026-09-25 (slice S1): say what a re-registration left
                // alone and what it took away, by key.
                if (row.KeptKeys is { Count: > 0 } kept)
                {
                    Console.Error.WriteLine($"kept, recorded by other commands: {string.Join(", ", kept)}");
                }
                if (row.RemovedKeys is { Count: > 0 } removed)
                {
                    Console.Error.WriteLine($"removed, no longer declared by the profile: {string.Join(", ", removed)}");
                }
                return 0;
            }
            catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException
                or InvalidOperationException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"register-site failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4; owner: "旧的做法全是垃圾…赶紧淘汰"): install-study-source is retired.
    // A Site takes each cell's code from its repository (code_from), a remote
    // Site asks the Home for it per attempt, and no compiler is carried.

    /// <summary>One recorded fact about a Site, from the capabilities register-site and the installers write.</summary>
    static string? Capability(RegistryOperations.Site site, string key)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(site.CapabilitiesJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ── register ─────────────────────────────────────────────────────────────

    static Command CheckCompiledStudy()
    {
        var from = new Option<string>("--from") { Description = "Compiled study JSON to check.", Required = true };
        var device = new Option<string>("--device") { Description = "Device required by its cells.", DefaultValueFactory = _ => "cuda" };
        var command = new Command("check-compiled-study", "Check registration's compiled-cell boundary without opening a Home.");
        command.Options.Add(from);
        command.Options.Add(device);
        command.SetAction(parsed =>
        {
            try
            {
                var result = StudyRegistration.CheckCompiledStudy(File.ReadAllText(parsed.GetValue(from)!), parsed.GetValue(device)!);
                if (!result.TryGetValue(out var cells))
                {
                    result.TryGetRefusal(out var refusal);
                    Console.Error.WriteLine($"check-compiled-study refused: {refusal.Detail}");
                    return 3;
                }
                Console.Out.WriteLine($"checked {cells.Count} compiled cell(s)");
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"check-compiled-study: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    static Command CheckStudyPlacement()
    {
        var from = new Option<string>("--from") { Description = "JSON placement declarations: cells (cell_id, model, stage_handler) and sites (site_id, admitted_models, allowed_handlers).", Required = true };
        var program = new Option<string>("--scheduler-program") { Description = "Explicit exported Gleam scheduler shipment directory.", Required = true };
        var command = new Command("check-study-placement", "Check all declared cells against all declared Sites in one request, without registering, reserving capacity or training.");
        command.Options.Add(from);
        command.Options.Add(program);
        command.SetAction(parsed =>
        {
            try
            {
                using var scheduler = GleamProgram.FromShipment(parsed.GetValue(program)!);
                using var admission = new GleamCellAdmission(scheduler);
                var result = StudyRegistration.CheckPlacement(File.ReadAllText(parsed.GetValue(from)!), admission);
                if (!result.TryGetValue(out var placement))
                {
                    result.TryGetRefusal(out var refusal);
                    Console.Error.WriteLine($"check-study-placement: {refusal.Detail}");
                    return 3;
                }
                Console.Out.WriteLine(JsonSerializer.Serialize(placement,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                Console.Error.WriteLine($"check-study-placement: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    static Command RegisterStudy()
    {
        var from = new Option<string>("--from") { Description = "A compiled study, as fern_analysis study compile writes it.", Required = true };
        var registry = RegistryOption();
        var name = new Option<string>("--name") { Description = "The name to register it under." };
        var revision = new Option<long>("--revision") { Description = "The revision of that name.", DefaultValueFactory = _ => 1L };
        var source = new Option<long>("--source") { Description = "The registered source id the study runs from.", Required = true };
        var device = new Option<string>("--device") { Description = "The device its cells require.", DefaultValueFactory = _ => "cuda" };

        var command = new Command("register-study",
            "Register a compiled study and its cells on the one-store schema. No lowering: a cell is a row, not a graph.");
        command.Options.Add(from);
        command.Options.Add(registry);
        command.Options.Add(name);
        command.Options.Add(revision);
        command.Options.Add(source);
        command.Options.Add(device);
        command.SetAction(parsed =>
        {
            var path = parsed.GetValue(registry)!;
            var compiledPath = parsed.GetValue(from)!;
            if (!File.Exists(compiledPath))
            {
                Console.Error.WriteLine($"register-study: no compiled study at {compiledPath}");
                return 2;
            }
            var compiled = File.ReadAllText(compiledPath);
            var studyName = parsed.GetValue(name) ?? Path.GetFileNameWithoutExtension(compiledPath);

            using var writer = RegistryHome.OpenWriter(path);
            var registered = StudyRegistration.Register(
                writer, compiled, studyName, parsed.GetValue(revision), parsed.GetValue(source), parsed.GetValue(device)!);
            if (!registered.TryGetValue(out var value))
            {
                registered.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"register-study refused: {refusal.Detail}");
                return 3;
            }
            Console.WriteLine($"study {studyName} revision {parsed.GetValue(revision)} registered as {value.StudyId} with {value.CellIds.Count} cell(s)");
            return 0;
        });
        return command;
    }

    // ── monitor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// CHG 2026-09-11 night (slice 11): `list-accepted-cells`, the read a
    /// statistic's evidence block is filled in from (design note
    /// 2026-09-11-statistics-admission-over-accepted-cells.md): each accepted
    /// cell by name, which attempt was accepted, and the sha256 of that
    /// attempt's result bytes, from the store that owns them.
    /// </summary>
    static Command ListAcceptedCells()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var command = new Command("list-accepted-cells",
            "The accepted cells of a registered study: cell, accepted attempt ordinal, and the sha256 of the accepted result's bytes.");
        command.Options.Add(registry);
        command.Options.Add(study);
        command.SetAction(parsed =>
        {
            using var reader = RegistryHome.OpenReader(parsed.GetValue(registry)!);
            var name = parsed.GetValue(study)!;
            if (FindStudy(reader.Connection, name) is not { } studyId)
            {
                Console.Error.WriteLine($"list-accepted-cells: no study named {name} is registered");
                return 3;
            }
            var accepted = RegistryOperations.AcceptedCells(reader.Connection, studyId);
            var total = RegistryOperations.CellCount(reader.Connection, studyId);
            var width = Math.Max(4, accepted.Count == 0 ? 4 : accepted.Max(cell => cell.Cell.Length));
            Console.Out.WriteLine($"{"cell".PadRight(width)}  attempt  result sha256");
            foreach (var cell in accepted)
            {
                Console.Out.WriteLine($"{cell.Cell.PadRight(width)}  {cell.AttemptOrdinal,7}  {cell.ResultSha256}");
            }
            Console.Out.WriteLine($"{accepted.Count} cell(s) accepted of {total}");
            return 0;
        });
        return command;
    }

    static Command MonitorStudy()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var follow = new Option<bool>("--follow")
        {
            Description = "keep printing while the study changes, until every cell is accepted or it is paused or cancelled",
        };
        // CHG 2026-09-11 night (slice 7, row 36): the attempt's recorded events under each cell.
        var events = new Option<bool>("--events")
        {
            Description = "under each cell, the recorded events of its current attempt: when, what kind, which phase, and the sentence it carried",
        };
        var command = new Command("monitor-study",
            "What a registered study is doing, from durable facts only: the study, its cells, their current attempts, "
            + "why a cell is not running, and who holds the one that is.");
        command.Options.Add(registry);
        command.Options.Add(study);
        // CHG 2026-09-27: the placement rule the "waiting" reasons follow, so a
        // person reads the reason of the engine that is deciding.
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "The exported Gleam scheduler program whose placement rule the waiting reasons follow; omit to read them from the C# rule.",
        };
        command.Options.Add(follow);
        command.Options.Add(events);
        command.Options.Add(schedulerProgram);
        command.SetAction((parsed, cancellation) =>
        {
            using var reader = RegistryHome.OpenReader(parsed.GetValue(registry)!);
            var name = parsed.GetValue(study)!;
            var found = FindStudy(reader.Connection, name);
            if (found is not { } studyId)
            {
                Console.Error.WriteLine($"monitor-study: no study named {name} is registered");
                return Task.FromResult(3);
            }
            using var program = parsed.GetValue(schedulerProgram) is { } shipment ? GleamProgram.FromShipment(shipment) : null;
            ISchedulerDecision? placement = program is null ? null : new GleamSchedulerDecision(program);
            return Task.FromResult(StudyProjection.Follow(
                reader.Connection, studyId, parsed.GetValue(follow), StudyProjection.FollowIntervalMs,
                Console.Out, Console.Error, cancellation, parsed.GetValue(events), placement));
        });
        return command;
    }

    // ── list ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every study a Registry Home holds, with how its cells stand.
    ///
    /// Before this, every way of reading a Home needed a study name: monitor-study answers about one
    /// study, list-accepted-cells likewise. Nothing answered "what is in here", so a person or a
    /// dashboard could not find out what had been registered without already knowing. Read-only: it
    /// opens the reader, claims nothing and writes nothing.
    /// </summary>
    static Command ListStudies()
    {
        var registry = RegistryOption();
        var state = new Option<string>("--state") { Description = "only studies whose requested state is this: run, pause or cancel." };
        var json = new Option<bool>("--json") { Description = "one record per study, for a reader." };
        var command = new Command("list-studies",
            "Every study this Registry Home holds, newest first: its name, the state it was asked for, and how its cells stand.");
        command.Options.Add(registry);
        command.Options.Add(state);
        command.Options.Add(json);
        command.SetAction(parsed =>
        {
            var home = parsed.GetValue(registry)!;
            List<RegisteredStudy> rows;
            try
            {
                using var reader = RegistryHome.OpenReader(home);
                rows = ReadStudies(reader.Connection, parsed.GetValue(state));
            }
            // A Home that is absent, unreadable or not a Home is a situation to read, not a stack
            // trace: the command says which database it expected, as the database rule requires.
            catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException
                or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                Console.Error.WriteLine($"list-studies: cannot read the Registry Home {home}: {error.Message}");
                return 3;
            }
            if (parsed.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new { schema = "fern.registered_studies.v1", studies = rows },
                    new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            foreach (var row in rows)
            {
                Console.Out.WriteLine(
                    $"{row.Name}  revision {row.Revision}  {row.RequestedState}  "
                    + $"{row.AcceptedCells}/{row.Cells} cell(s) accepted  registered {row.CreatedAt}");
            }
            Console.Out.WriteLine($"{rows.Count} stud{(rows.Count == 1 ? "y" : "ies")} in {parsed.GetValue(registry)}");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// The line: what this Home would run next, across every study, in its own order.
    ///
    /// Every other read of a Home needs a study name or a Site name, so nothing answered the
    /// question a queue exists to answer: what is the order across everything, and what is stuck.
    /// The order is not computed here — it is the offer's own, as the claim would make it — and
    /// nothing is written.
    /// </summary>
    static Command MonitorLine()
    {
        var registry = RegistryOption();
        var json = new Option<bool>("--json") { Description = "the whole line as one record, for a reader." };
        var command = new Command("monitor-line",
            "What this Registry Home would run next, across every study: the offer in its own order, what is "
            + "running, what is blocked and why, the pauses and holds standing now, and each Site's load.");
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "The exported Gleam scheduler program whose placement rule the blocked cells' reasons follow; omit to read them from the C# rule.",
        };
        command.Options.Add(registry);
        command.Options.Add(json);
        command.Options.Add(schedulerProgram);
        command.SetAction(parsed =>
        {
            var home = parsed.GetValue(registry)!;
            Fern.Scheduler.LineProjection.Line line;
            try
            {
                using var reader = RegistryHome.OpenReader(home);
                using var program = parsed.GetValue(schedulerProgram) is { } shipment ? GleamProgram.FromShipment(shipment) : null;
                ISchedulerDecision? placement = program is null ? null : new GleamSchedulerDecision(program);
                line = Fern.Scheduler.LineProjection.Read(reader.Connection, placement: placement);
            }
            // A Home that is absent, unreadable or not a Home is a situation to read, not a stack
            // trace, and it must never render as an empty line: nothing waiting and nothing readable
            // are opposite facts.
            catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException
                or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                Console.Error.WriteLine($"monitor-line: cannot read the Registry Home {home}: {error.Message}");
                return 3;
            }
            if (parsed.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(line, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            foreach (var request in line.Standing)
            {
                Console.Out.WriteLine(
                    $"{request.Kind} by {request.RequestedBy} over {request.Scope}: {request.Reason} ({request.Lift})");
            }
            foreach (var site in line.Sites)
            {
                // Full and forbidden are different: a Site can be free and stopped, or busy and well.
                Console.Out.WriteLine(
                    $"site {site.Site,-16} {site.Device,-6} {site.Busy}/{site.Slots} busy"
                    + (site.Full ? ", full" : "")
                    + (site.Available ? "" : ", STOPPED: a request stands over it")
                    + $"  {site.Supervisor}");
            }
            foreach (var running in line.Running)
            {
                Console.Out.WriteLine(
                    $"running  {running.Study}  {running.Cell}  on {running.Site}  attempt {running.Ordinal}"
                    + (running.Remote.Length == 0 ? "" : $"  remote={running.Remote}")
                    + (running.Silent ? "  SILENT" : ""));
            }
            foreach (var done in line.Accepted)
            {
                Console.Out.WriteLine($"accepted {done.Study}  {done.Cell}  on {done.Site}");
            }
            foreach (var queued in line.Queued)
            {
                Console.Out.WriteLine(
                    $"{queued.Position,4}.  {queued.Study}  {queued.Cell}  {queued.Dataset}"
                    + (queued.Priority == "normal" ? "" : $"  {queued.Priority}")
                    + (queued.MovedLast ? "  moved last" : ""));
                if (queued.Waiting is { Length: > 0 } why)
                {
                    Console.Out.WriteLine($"        {why}");
                }
            }
            foreach (var blocked in line.Blocked)
            {
                Console.Out.WriteLine($"blocked  {blocked.Study}  {blocked.Cell}  {blocked.State}: {blocked.Waiting}");
            }
            // A study asked to stop is a decision, not an obstacle; the two read differently on
            // purpose, and a set-aside study keeps every result it had already accepted.
            foreach (var aside in line.SetAside)
            {
                Console.Out.WriteLine($"set aside  {aside.Study}  {aside.Cell}  the study is {aside.StudyState}");
            }
            Console.Out.WriteLine(
                $"{line.Queued.Count} in the offer, {line.Running.Count} running, {line.Accepted.Count} accepted, "
                + $"{line.Blocked.Count} blocked, {line.SetAside.Count} set aside, "
                + $"{line.Standing.Count} request(s) standing, over {line.Sites.Count} Site(s) in {home}");
            return 0;
        });
        return command;
    }

    /// <summary>One row per registered study, newest first, with its cell counts from the same Home.</summary>
    /// <remarks>
    /// CHG 2026-09-26 (slice 3 of the command-line design): the query moved to
    /// StudyProjection.ListStudies, which GET /v1/studies reads too. This record
    /// stays as the shape of list-studies --json (fern.registered_studies.v1),
    /// which python/fern_analysis reads by these names.
    /// </remarks>
    sealed record RegisteredStudy(long Id, string Name, long Revision, string RequestedState, string CreatedAt,
                                  long Cells, long AcceptedCells);

    static List<RegisteredStudy> ReadStudies(Microsoft.Data.Sqlite.SqliteConnection connection, string? state) =>
        [.. StudyProjection.ListStudies(connection, state).Select(study => new RegisteredStudy(
            study.Id, study.Name, study.Revision, study.RequestedState, study.CreatedAt, study.Cells, study.AcceptedCells))];

    // ── cancel ───────────────────────────────────────────────────────────────

    static Command CancelStudy()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var revision = new Option<long?>("--revision")
        {
            Description = "The exact registered revision to change; omit for the latest revision.",
        };
        var state = new Option<string>("--state")
        {
            Description = "run, pause or cancel.",
            DefaultValueFactory = _ => RegistryOperations.StateCancel,
        };
        var command = new Command("cancel-study",
            "Ask a registered study to run, pause or cancel. No new cell is claimed while it is paused or cancelled.");
        command.Options.Add(registry);
        command.Options.Add(study);
        command.Options.Add(state);
        command.Options.Add(revision);
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var selectedRevision = parsed.GetValue(revision);
            var found = FindStudy(writer.Connection, parsed.GetValue(study)!, selectedRevision);
            if (found is not { } studyId)
            {
                Console.Error.WriteLine($"cancel-study: no study named {parsed.GetValue(study)}"
                    + (selectedRevision is null ? "" : $" revision {selectedRevision}") + " is registered");
                return 3;
            }
            var asked = RegistryOperations.RequestStudyState(writer, studyId, parsed.GetValue(state)!);
            if (!asked.TryGetValue(out var value))
            {
                asked.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"cancel-study refused: {refusal.Detail}");
                return 3;
            }
            // What happens to an attempt already running is the owner's policy
            // and is deliberately not decided here; this sets the field a claim
            // reads before it starts a NEW attempt.
            Console.WriteLine($"study {parsed.GetValue(study)}"
                + (selectedRevision is null ? "" : $" revision {selectedRevision}")
                + $" is {value}; no new cell will be claimed while it is not run");
            return 0;
        });
        return command;
    }

    // ── retry one cell ───────────────────────────────────────────────────────

    /// <summary>
    /// `retry-cell`: make one cell claimable again, and say who decided that and
    /// why.
    ///
    /// The two cases it exists for are an attempt left open by a supervisor that
    /// was killed — which excludes its cell from every later claim — and a cell
    /// that failed for a reason a person has since fixed. Both are decisions,
    /// not accidents, so the reason and the person are recorded against the
    /// attempt rather than inferred later from a rerun.
    /// </summary>
    static Command RetryCell()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var cell = new Option<string>("--cell") { Description = "The cell's name, as monitor-study prints it.", Required = true };
        var reason = new Option<string>("--reason") { Description = "Why this cell is being asked for again, in words a later reader can act on.", Required = true };
        var by = new Option<string>("--by")
        {
            Description = "Who is asking. Recorded with the reason.",
            DefaultValueFactory = _ => Environment.UserName,
        };
        var staleAfter = new Option<int>("--stale-after-seconds")
        {
            Description = "How long an open attempt must have been SILENT before it is declared lost. Silent means "
                        + "no supervisor heartbeat: the supervisor renews its attempt every 60 seconds while the "
                        + "trainer runs, so the default is ten missed beats (600 s). It is not the cell timeout — a "
                        + "cell may legitimately take hours, but its supervisor may not go quiet for them.",
            DefaultValueFactory = _ => (int)StudyCellRetry.DefaultStaleAfter.TotalSeconds,
        };
        var force = new Option<bool>("--force")
        {
            Description = "Declare an open attempt lost even though its supervisor beat inside the bound. "
                        + "Read the attempt's facts in the refusal first: a supervisor that is still beating is "
                        + "still training the cell, and taking it away lets a second machine claim the same cell. "
                        + "Also overrides an unreadable heartbeat time, recording that its silence was unknown.",
        };
        var evidence = new Option<string?>("--evidence")
        {
            Description = "What was checked before asking: a path, a command's output, a sentence. Recorded beside the reason, "
                        + "the way the retired abandonment kept its evidence receipt.",
        };
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "Explicit Erlang shipment for the operator retry decision. Failure refuses without recording a retry; omit to use C#.",
        };
        var command = new Command("retry-cell",
            "Make one cell of a registered study claimable again after a person fixed what stopped it; records who and why, and touches no earlier result.");
        foreach (var option in new Option[] { registry, study, cell, reason, by, staleAfter, force, evidence, schedulerProgram })
        {
            command.Options.Add(option);
        }
        command.SetAction(parsed =>
        {
            using var writer = RegistryHome.OpenWriter(parsed.GetValue(registry)!);
            var studyName = parsed.GetValue(study)!;
            var found = FindStudy(writer.Connection, studyName);
            if (found is not { } studyId)
            {
                Console.Error.WriteLine($"retry-cell: no study named {studyName} is registered");
                return 3;
            }
            try
            {
                if (parsed.GetValue(schedulerProgram) is { } selectedShipment
                    && !File.Exists(Path.Combine(selectedShipment, "entrypoint.sh")))
                    throw new GleamProgramException($"no Gleam shipment entrypoint at {selectedShipment}");
                using var program = parsed.GetValue(schedulerProgram) is { } shipment
                    ? GleamProgram.FromShipment(shipment) : null;
                IStudyCellRetryDecision? decision = program is null ? null : new GleamStudyCellRetryDecision(program);
                var requested = StudyCellRetry.Request(
                    writer, studyId, parsed.GetValue(cell)!, parsed.GetValue(by)!, parsed.GetValue(reason)!,
                    TimeSpan.FromSeconds(parsed.GetValue(staleAfter)), parsed.GetValue(force), parsed.GetValue(evidence),
                    implementedBy: decision);
                if (!requested.TryGetValue(out var retried))
                {
                    requested.TryGetRefusal(out var refusal);
                    Console.Error.WriteLine($"retry-cell: {refusal.Detail}");
                    return 3;
                }
                Console.WriteLine(StudyCellRetry.Sentence(retried, studyName));
                return 0;
            }
            catch (GleamProgramException error)
            {
                Console.Error.WriteLine($"retry-cell: selected Gleam retry decision failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    // ── collect ──────────────────────────────────────────────────────────────

    // CHG: accepted producer weights become an immutable name through the typed owners.
    static Command NameCheckpoint()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var revision = new Option<long>("--revision") { Description = "Exact registered Study revision.", Required = true };
        var cell = new Option<string>("--cell") { Description = "Accepted training cell by name.", Required = true };
        var name = new Option<string>("--name") { Description = "New immutable checkpoint name.", Required = true };
        var result = new Option<string>("--producer-results") { Description = "Original accepted results.json beside its retained weights, readable on this host.", Required = true };
        var store = new Option<string>("--checkpoint-store") { Description = "Local store root whose checkpoints directory owns the name.", Required = true };
        var command = new Command("name-checkpoint", "Name an accepted training checkpoint for evaluation without retraining; existing names are immutable.");
        foreach (var option in new Option[] { registry, study, revision, cell, name, result, store }) command.Options.Add(option);
        command.SetAction(parsed =>
        {
            var named = StudyCheckpointNaming.Execute(parsed.GetValue(registry)!, parsed.GetValue(study)!,
                parsed.GetValue(revision), parsed.GetValue(cell)!, parsed.GetValue(result)!, parsed.GetValue(store)!, parsed.GetValue(name)!);
            if (!named.IsOk)
            {
                Console.Error.WriteLine($"name-checkpoint: {named.Refusal}");
                return 3;
            }
            Console.WriteLine($"named checkpoint {parsed.GetValue(name)} at {named.Value}");
            return 0;
        });
        return command;
    }

    static Command CollectStudy()
    {
        var registry = RegistryOption();
        var study = StudyOption();
        var asDocuments = new Option<bool>("--as-documents")
        {
            Description = "Export every accepted result as a named JSON file in the shared folder, including saved references and "
                        + "results whose store save failed. Python study collect resolves them without retraining.",
        };
        var into = new Option<string>("--into")
        {
            Description = "Where to write. A FILE when the cells saved their own results — it receives one "
                        + "fern.accepted_cell_results.v1 document of references — and a DIRECTORY when the "
                        + "results are documents, which become named JSON files in that folder.",
            Required = true,
        };
        var command = new Command("collect-study-results",
            "Collect a registered study's accepted results: the references when the cells saved themselves, the documents when they did not.");
        command.Options.Add(registry);
        command.Options.Add(study);
        command.Options.Add(into);
        command.Options.Add(asDocuments);
        command.SetAction(parsed =>
        {
            using var reader = RegistryHome.OpenReader(parsed.GetValue(registry)!);
            var found = FindStudy(reader.Connection, parsed.GetValue(study)!);
            if (found is not { } studyId)
            {
                Console.Error.WriteLine($"collect-study-results: no study named {parsed.GetValue(study)} is registered");
                return 3;
            }

            // Every cell must have an accepted attempt. The old collector
            // refused "one stage node per compiled study cell", which was this
            // check wearing the stage layer's words.
            // CHG 2026-09-26 (slice 2 of
            // docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
            // no longer. A study whose other cells still wait collects its
            // accepted cells, names each of the others on standard error with the
            // state monitor-study prints for it, and exits 0; only a study with no
            // accepted cell is refused, with 3, as before (StudyCollect.Decide).
            // No caller read the 3 a partial study used to get: the Python side
            // only reads the file this verb writes (python/fern_analysis/__main__.py:390),
            // and its collection refuses a file that lacks declared cells unless
            // it is given --allow-incomplete (python/fern_train/unit_summary.py).
            // CHG 2026-09-26 (slice 3 of the command-line design): the query and the
            // state of a cell left out moved to StudyCollect.ReadCells, which
            // GET /v1/studies/<name>/results reads too; what is collected is unchanged.
            var unaccepted = new List<StudyCollect.Unaccepted>();
            var units = new List<StudyCollect.Accepted>();
            foreach (var cell in StudyCollect.ReadCells(reader.Connection, studyId))
            {
                if (cell.Result is not { } result)
                {
                    unaccepted.Add(new StudyCollect.Unaccepted(cell.Cell, cell.State));
                    continue;
                }
                units.Add(new StudyCollect.Accepted(cell.Cell, result, cell.StoreRefusedBecause));
            }

            var into_ = parsed.GetValue(into)!;
            var decided = StudyCollect.Decide(parsed.GetValue(study)!, units, unaccepted, parsed.GetValue(asDocuments));
            if (!decided.IsOk)
            {
                Console.Error.WriteLine($"collect-study-results: {decided.Refusal}");
                return 3;
            }
            // CHG 2026-09-26: each cell left out, one line apiece, before the
            // collection's own line; what is collected is unchanged.
            foreach (var left in decided.Value.NotCollected)
            {
                Console.Error.WriteLine($"collect-study-results: {left.Sentence}");
            }
            switch (decided.Value)
            {
                case StudyCollect.Plan.References references:
                    var parent = Path.GetDirectoryName(Path.GetFullPath(into_));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    File.WriteAllText(into_, references.Document);
                    Console.WriteLine($"collected {references.CellCount} cell reference(s) into {into_}");
                    return 0;
                case StudyCollect.Plan.Documents documents:
                    Directory.CreateDirectory(into_);
                    foreach (var unit in documents.Units)
                    {
                        File.WriteAllText(Path.Combine(into_, unit.Cell + ".results.json"), unit.Result);
                    }
                    Console.WriteLine($"collected {documents.Units.Count} cell(s) into {into_}");
                    return 0;
                default:
                    Console.Error.WriteLine("collect-study-results: unreadable collection plan");
                    return 1;
            }
        });
        return command;
    }

    static long? FindStudy(Microsoft.Data.Sqlite.SqliteConnection connection, string name, long? revision = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM studies WHERE name = $name AND ($revision IS NULL OR revision = $revision) ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        var found = command.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
    }
}
