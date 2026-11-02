using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// A Site's files checked against its declarations (slice F1–F2 of
/// how-a-site-is-reached-and-started), with the real Gleam program, on
/// temporary directories shaped like the Beijing RTX 5090's on 2026-09-25,
/// and with systemd's state given rather than read.
/// </summary>
public sealed class SiteFilesTests
{
    const string Site = "autodl-bj-5090";
    const string Unit = "fern-study-supervisor-autodl-bj-5090.service";

    [Fact]
    public void SystemdsShowOutputIsReadIntoLoadedUnits()
    {
        const string shown =
            """
            ExecStart={ path=/home/user/study/scripts/connect_runpod_ssh.sh ; argv[]=/home/user/study/scripts/connect_runpod_ssh.sh exec -- env OMP_NUM_THREADS=1 /root/bin/fern-run.8e41 run-study-cells --serve --home http://127.0.0.1:8622/ --site autodl-bj-5090 ; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }
            Environment=RUNPOD_SSH_TARGET=fern-autodl-bj-5090:50300 RUNPOD_COMMAND_TIMEOUT_SECONDS=1040000
            Id=fern-supervisor-bj5090-recovery.service
            ActiveState=active
            SubState=running
            FragmentPath=/run/user/1000/systemd/transient/fern-supervisor-bj5090-recovery.service
            NeedDaemonReload=no

            ExecStart={ path=/x ; argv[]=/x stdio -- run-study-cells --site autodl-bj-5090 ; ignore_errors=no }
            Environment=RUNPOD_SSH_TARGET=fern-autodl-bj-5090:27464 RUNPOD_CONTROL_PATH=/tmp/fern-beijing-compressed-%C
            Id=fern-study-supervisor-autodl-bj-5090.service
            ActiveState=activating
            SubState=auto-restart
            FragmentPath=/home/user/.config/systemd/user/fern-study-supervisor-autodl-bj-5090.service
            NeedDaemonReload=yes
            NRestarts=1187
            DropInPaths=/home/user/.config/systemd/user/fern-study-supervisor-autodl-bj-5090.service.d/zzzzz-head-splines.conf
            """;

        var units = SiteFiles.ParseShow(shown);
        Assert.Equal(1187, units.Single(unit => unit.Name == Unit).Restarts);
        Assert.Equal(["/home/user/.config/systemd/user/fern-study-supervisor-autodl-bj-5090.service.d/zzzzz-head-splines.conf"],
            units.Single(unit => unit.Name == Unit).DropInPaths);

        Assert.Equal(2, units.Count);
        Assert.True(units[0].Temporary);
        Assert.Equal("active", units[0].ActiveState);
        Assert.Contains("--site", units[0].ExecStart[0]);
        Assert.Equal("/root/bin/fern-run.8e41", units[0].ExecStart[0][5]);
        Assert.False(units[1].Temporary);
        Assert.True(units[1].NeedDaemonReload);
        Assert.Equal("fern-autodl-bj-5090:27464", units[1].Environment["RUNPOD_SSH_TARGET"]);
        Assert.Equal("/tmp/fern-beijing-compressed-%C", units[1].Environment["RUNPOD_CONTROL_PATH"]);
    }

    /// <summary>systemd prints one ExecStart= line per start command; every one is kept.</summary>
    [Fact]
    public void EveryStartCommandOfAUnitIsKept()
    {
        const string shown =
            """
            ExecStart={ path=/a ; argv[]=/a run-study-cells --site one ; ignore_errors=no }
            ExecStart={ path=/b ; argv[]=/b run-study-cells --site autodl-bj-5090 ; ignore_errors=yes }
            Id=fern-study-supervisor-local-4070.service
            ActiveState=active
            SubState=running
            FragmentPath=/home/user/.config/systemd/user/fern-study-supervisor-local-4070.service
            NeedDaemonReload=no
            """;

        var unit = Assert.Single(SiteFiles.ParseShow(shown));

        Assert.Equal(2, unit.ExecStart.Count);
        Assert.Equal("/a", unit.ExecStart[0][0]);
        Assert.Equal("autodl-bj-5090", unit.ExecStart[1][^1]);
    }

    [Fact]
    public void TheBeijingSituationIsFoundFactByFact()
    {
        var root = Directory.CreateTempSubdirectory("site-files-").FullName;
        try
        {
            var paths = Layout(root);
            File.WriteAllText(Path.Combine(paths.SshDirectory, "config"),
                "Host fern-autodl-bj-5090\n    HostName site-b2.example.invalid\n    User root\n    Port 27464\n"
                + "    Compression yes\n    RemoteForward 127.0.0.1:8622 127.0.0.1:8621\n    ExitOnForwardFailure yes\n");
            File.WriteAllText(Path.Combine(paths.UnitDirectory, Unit),
                "[Service]\nType=simple\nEnvironment=RUNPOD_SSH_TARGET=fern-autodl-bj-5090:50300\n"
                + $"ExecStart={Start("8622")}\nRestart=always\nRestartSec=15s\n");
            var dropIns = Directory.CreateDirectory(Path.Combine(paths.UnitDirectory, Unit + ".d")).FullName;
            File.WriteAllText(Path.Combine(dropIns, "zzzz-beta-nll.conf"),
                "[Service]\nStandardError=append:/home/user/study/tmp/beijing.log\n");
            var loaded = new[]
            {
                new SiteFiles.LoadedUnit(Unit, false, "activating", "auto-restart", true,
                    [Start("8622").Split(' ')], new Dictionary<string, string> { ["RUNPOD_SSH_TARGET"] = "fern-autodl-bj-5090:27464" }),
                new SiteFiles.LoadedUnit("fern-supervisor-bj5090-recovery.service", true, "active", "running", false,
                    [Start("8622").Split(' ')], new Dictionary<string, string>()),
            };

            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var findings = SiteFiles.Check(program, Site, paths, loaded);

            var kinds = findings.Select(finding => finding.Kind).ToList();
            Assert.Contains("kept_by_hand", kinds);
            Assert.Contains("missing", kinds);
            Assert.Contains("extra", kinds);
            Assert.Contains("loaded_older", kinds);
            Assert.Contains("also_serves", kinds);
            Assert.Contains(findings, finding =>
                finding.Sentence == $"{Path.Combine(paths.SshDirectory, "config")}: ssh Port is declared as 50300 and found as 27464");
            Assert.Contains(findings, finding =>
                finding.Sentence == $"{Unit} with its drop-ins: output is declared as journal and found as append:/home/user/study/tmp/beijing.log");
            Assert.DoesNotContain(findings, finding => finding.Matches);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ASiteWithoutASupervisorDeclarationIsNotCheckedAndSaysWhy()
    {
        var root = Directory.CreateTempSubdirectory("site-files-").FullName;
        try
        {
            var paths = Layout(root);
            File.Delete(Path.Combine(paths.Profiles, SiteFiles.SupervisorDeclarationName(Site)));
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());

            var findings = SiteFiles.Check(program, Site, paths, []);

            Assert.Equal([new SiteFiles.Finding("undeclared",
                "not checked: no supervisor declaration config/profiles/autodl-bj-5090.supervisor.json")], findings);
            Assert.Empty(SiteFiles.DeclaredSites(paths.Profiles));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── Slice F2, the unit half ──────────────────────────────────────────

    sealed class CountingReload : SiteUnitFiles.IReload
    {
        public int Reloads { get; private set; }
        public void DaemonReload() => Reloads++;
    }

    static readonly SiteUnitFiles.Inputs UnitInputs = new(
        "/home/user/.local/bin:/usr/bin:/bin", "/home/user/study/scripts/connect_runpod_ssh.sh", "/home/user/study",
        [["/home/user/bin/fern-run", "check-site-files", "--site", Site, "--before-start"]],
        "Generated for a test");

    static SiteFiles.LoadedUnit Recovery(string state) => new(
        "fern-supervisor-bj5090-recovery.service", true, state, "running", false,
        [Start("8622").Split(' ')], new Dictionary<string, string>());

    [Fact]
    public void TheUnitIsWrittenWithItsChecksDropInsMovedAsideAndSystemdReloadedOnce()
    {
        var root = Directory.CreateTempSubdirectory("site-unit-").FullName;
        try
        {
            var paths = Layout(root);
            var dropIns = Directory.CreateDirectory(Path.Combine(paths.UnitDirectory, Unit + ".d")).FullName;
            File.WriteAllText(Path.Combine(dropIns, "zzzzz-head-splines.conf"), "[Service]\nExecStart=\nExecStart=/x run-study-cells --site autodl-bj-5090\n");
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var reload = new CountingReload();

            // Without --remove-extra the drop-in refuses the update, and nothing is written.
            var refused = SiteUnitFiles.Update(program, Site, paths, [], UnitInputs, removeExtra: false, reload);
            Assert.Contains("--remove-extra", refused.Refused, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(paths.UnitDirectory, Unit)));
            Assert.Equal(0, reload.Reloads);

            // With it: written, the drop-in moved where systemd does not read it, one reload.
            var done = SiteUnitFiles.Update(program, Site, paths, [], UnitInputs, removeExtra: true, reload);
            Assert.Null(done.Refused);
            Assert.True(done.Written);
            var text = File.ReadAllText(Path.Combine(paths.UnitDirectory, Unit));
            Assert.Contains("ExecStartPre=/home/user/bin/fern-run check-site-files --site autodl-bj-5090 --before-start\n", text, StringComparison.Ordinal);
            Assert.Contains("Environment=RUNPOD_SSH_TARGET=fern-autodl-bj-5090:50300\n", text, StringComparison.Ordinal);
            Assert.Contains(" /root/bin/fern-run.8e41 run-study-cells ", text, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(dropIns));
            Assert.True(File.Exists(Path.Combine(done.MovedTo!, "zzzzz-head-splines.conf")));
            Assert.Equal(1, reload.Reloads);

            // A second update changes nothing and does not reload.
            var again = SiteUnitFiles.Update(program, Site, paths, [], UnitInputs, removeExtra: true, reload);
            Assert.False(again.Written);
            Assert.Empty(again.Moved);
            Assert.Equal(1, reload.Reloads);

            // And what was written passes the check a unit runs before it starts, but for the SSH entry this test did not write.
            var findings = SiteFiles.Check(program, Site, paths, [], beforeStart: true);
            Assert.DoesNotContain(findings, finding => finding.Kind is "differs" or "extra" && finding.Sentence.Contains(Unit, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The reader's second review: a Site's own unit in its restart loop would
    /// run a new configuration at its next restart, by itself, so the update
    /// waits until a person has stopped it; and what the update replaces is kept.
    /// </summary>
    [Fact]
    public void ALoopingUnitRefusesTheUpdateAndAReplacedUnitFileIsKept()
    {
        var root = Directory.CreateTempSubdirectory("site-unit-").FullName;
        try
        {
            var paths = Layout(root);
            File.WriteAllText(Path.Combine(paths.UnitDirectory, Unit), "# written by hand\n[Service]\nExecStart=/x\n");
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var reload = new CountingReload();
            var looping = new SiteFiles.LoadedUnit(Unit, false, "activating", "auto-restart", false, [],
                new Dictionary<string, string>(), Restarts: 1187);

            var refused = SiteUnitFiles.Update(program, Site, paths, [looping], UnitInputs, removeExtra: true, reload);
            Assert.Contains("is activating/auto-restart, restarted 1187 time(s)", refused.Refused, StringComparison.Ordinal);
            Assert.Equal("# written by hand\n[Service]\nExecStart=/x\n", File.ReadAllText(Path.Combine(paths.UnitDirectory, Unit)));
            Assert.Equal(0, reload.Reloads);

            var stopped = looping with { ActiveState = "inactive", SubState = "dead" };
            var done = SiteUnitFiles.Update(program, Site, paths, [stopped], UnitInputs, removeExtra: true, reload);
            Assert.True(done.Written);
            Assert.Equal("# written by hand\n[Service]\nExecStart=/x\n", File.ReadAllText(Path.Combine(done.MovedTo!, Unit)));
            Assert.Contains(done.Changes, change => change.Fact == "RUNPOD_SSH_TARGET");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// systemd starts a unit with its own PATH, which lacks the folder erl is
    /// in; the checks before start run the Gleam program, which runs erl. With
    /// systemd's PATH erl is not found; with the PATH the unit is given, it runs.
    /// </summary>
    [Fact]
    public void ErlRunsWithTheUnitsPathAndNotWithSystemdsOwn()
    {
        const string systemd = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        var erl = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, "erl")).First(File.Exists);
        static int Run(string path)
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("erl -noshell -eval 'halt().'");
            start.Environment["PATH"] = path;
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(60));
            return process.ExitCode;
        }
        if (Path.GetDirectoryName(erl) is not ("/usr/local/bin" or "/usr/bin" or "/bin" or "/usr/local/sbin" or "/usr/sbin" or "/sbin"))
        {
            Assert.NotEqual(0, Run(systemd));
        }
        Assert.Equal(0, Run(Path.GetDirectoryName(erl) + ":" + systemd));
    }

    [Fact]
    public void AnActiveSecondSupervisorRefusesTheUnitUpdateAndNothingIsWritten()
    {
        var root = Directory.CreateTempSubdirectory("site-unit-").FullName;
        try
        {
            var paths = Layout(root);
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var reload = new CountingReload();

            var refused = SiteUnitFiles.Update(program, Site, paths, [Recovery("active")], UnitInputs, removeExtra: true, reload);

            Assert.Contains("fern-supervisor-bj5090-recovery.service (created at run time, active/running)", refused.Refused, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(paths.UnitDirectory, Unit)));
            Assert.Equal(0, reload.Reloads);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Slice F6: the commands a unit runs before its start are read, since a
    /// rendered unit's checks name this machine's <c>fern-run</c> there.
    /// </summary>
    [Fact]
    public void TheCommandsBeforeTheStartAreReadApartFromTheStart()
    {
        const string shown =
            """
            ExecStartPre={ path=/d/run-operator-binaries/fern-run.aa ; argv[]=/d/run-operator-binaries/fern-run.aa check-site-files --site one ; ignore_errors=no }
            ExecStart={ path=/a ; argv[]=/a run-study-cells --site one ; ignore_errors=no }
            Id=fern-study-supervisor-one.service
            ActiveState=active
            SubState=running
            FragmentPath=/home/user/.config/systemd/user/fern-study-supervisor-one.service
            NeedDaemonReload=no
            """;

        var unit = Assert.Single(SiteFiles.ParseShow(shown));

        Assert.Equal("/a", Assert.Single(unit.ExecStart)[0]);
        Assert.Equal("check-site-files", Assert.Single(unit.ExecStartPre!)[1]);
    }

    /// <summary>
    /// Slice F6: on 2026-09-26 the installer's cleanup removed
    /// <c>fern-compile.888e83de…</c>, which the local supervisor named in
    /// <c>--fern-compile</c> and <c>FERN_COMPILE_BIN</c>. Every digest a loaded
    /// unit or a Site declaration names is read, however the path is spelled.
    /// </summary>
    [Fact]
    public void EveryPublishedDigestAUnitOrADeclarationNamesIsRead()
    {
        var compiler = new string('8', 64);
        var check = new string('c', 64);
        var python = new string('d', 64);
        var onSite = new string('f', 64);
        var root = Directory.CreateTempSubdirectory("site-named-").FullName;
        try
        {
            var paths = Layout(root);
            File.WriteAllText(Path.Combine(paths.Profiles, SiteFiles.SupervisorDeclarationName(Site)),
                $$"""
                {"schema":"fern.site_supervisor.v1","site":"autodl-bj-5090","remote_forward":{"site_port":8622,"home_port":8621},
                 "ssh_compression":true,"fern_run":"/root/bin/fern-run.8e41","fern_compile":"/root/fern-runtime/bin/fern-compile.{{onSite}}",
                 "process_environment":{"OMP_NUM_THREADS":"1"},
                 "lease_seconds":600,"restart":"always","restart_seconds":15}
                """);
            var local = new SiteFiles.LoadedUnit(
                "fern-supervisor-local-newcompiler.service", true, "active", "running", false,
                [["/home/user/study/deploy/run-operator-binaries/current", "run-study-cells",
                  "--fern-compile", $"/home/user/study/deploy/compile-binaries/fern-compile.{compiler}"]],
                new Dictionary<string, string>
                {
                    ["FERN_COMPILE_BIN"] = $"/home/user/study/runtime/deploy/compile-binaries/fern-compile.{compiler}",
                    ["PYTHONPATH"] = $"/home/user/study/runtime/deploy/study-source-roots/{python}/python:/tmp/x",
                    ["OMP_NUM_THREADS"] = "1",
                },
                ExecStartPre: [[$"/home/user/study/runtime/deploy/run-operator-binaries/fern-run.{check}", "check-site-files"]]);

            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var named = SiteFiles.NamedDigests(program, paths.Profiles, [local]);

            // A study source root carries its digest in a directory's name; the
            // cleanup never touches study sources, so only file names count.
            Assert.Equal(new HashSet<string> { compiler, check, onSite }, named.ToHashSet());
            Assert.DoesNotContain(python, named);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A declaration the flow cannot read refuses: nothing is cleaned without it.</summary>
    [Fact]
    public void AnUnreadableDeclarationRefusesRatherThanCleanWithoutIt()
    {
        var root = Directory.CreateTempSubdirectory("site-named-").FullName;
        try
        {
            var paths = Layout(root);
            File.WriteAllText(Path.Combine(paths.Profiles, SiteFiles.SupervisorDeclarationName(Site)), """{"schema":"nope"}""");
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var refused = Assert.Throws<GleamProgramException>(() => SiteFiles.NamedDigests(program, paths.Profiles, []));
            Assert.Contains("could not be read", refused.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static string Start(string homePort) =>
        "/home/user/study/scripts/connect_runpod_ssh.sh stdio -- env OMP_NUM_THREADS=1 /root/bin/fern-run.8e41 run-study-cells "
        + $"--serve --home http://127.0.0.1:{homePort}/ --site autodl-bj-5090 --python /root/python --fern-run /root/bin/fern-run.8e41 "
        + "--work-root /root/work --lease-seconds 600";

    /// <summary>A profile and a supervisor declaration for the Site, and empty SSH and unit directories.</summary>
    static SiteFiles.Paths Layout(string root)
    {
        var paths = new SiteFiles.Paths(
            Directory.CreateDirectory(Path.Combine(root, "profiles")).FullName,
            Directory.CreateDirectory(Path.Combine(root, "ssh")).FullName,
            Directory.CreateDirectory(Path.Combine(root, "units")).FullName);
        File.WriteAllText(Path.Combine(paths.Profiles, Site + ".json"),
            """
            {"schema":"fern.execution_profile.v5","profile_id":"autodl-bj-5090","executor":"ssh",
             "ssh_destination":"root@site-b2.example.invalid:50300","python":"/root/python","work_root":"/root/work"}
            """);
        File.WriteAllText(Path.Combine(paths.Profiles, SiteFiles.SupervisorDeclarationName(Site)),
            """
            {"schema":"fern.site_supervisor.v1","site":"autodl-bj-5090","remote_forward":{"site_port":8622,"home_port":8621},
             "ssh_compression":true,"fern_run":"/root/bin/fern-run.8e41","process_environment":{"OMP_NUM_THREADS":"1"},
             "lease_seconds":600,"restart":"always","restart_seconds":15}
            """);
        return paths;
    }
}
