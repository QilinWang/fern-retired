using System.Text;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice 1 of the cell port: the unit that keeps a Site's supervisor resident
/// binds the installed fern-run, the Home and the Site, and carries the retired
/// daemon's restart policy unchanged: on-failure, never after exit 2, five
/// seconds between tries, the whole control group killed on stop.
/// </summary>
public sealed class StudySupervisorUnitTests
{
    [Fact]
    public void APullServiceNamesItsHomeWithoutARegistryFileCondition()
    {
        var unit = Rendered(Config() with { Registry = "http://home.example:8621/" });
        Assert.Contains("--home http://home.example:8621/", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("--store", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("ConditionPathExists=http", unit, StringComparison.Ordinal);
    }
    [Fact]
    public async Task TheInstallerCommandUsesTheHttpHomeWithoutALocalRegistry()
    {
        var registry = TestRegistry.CreateRegistry();
        var root = Path.GetDirectoryName(registry)!;
        using var writer = Fern.Registry.RegistryHome.OpenWriter(registry);
        Assert.True(Fern.Registry.RegistryOperations.RegisterSite(writer, "pull-site", "{}", "{\"slots\":1}").IsOk);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = $"http://127.0.0.1:{port}/";
        using var service = new HomeService(writer, address);
        service.Start();
        var profile = Path.Combine(root, "profile.json");
        File.WriteAllText(profile, System.Text.Json.JsonSerializer.Serialize(new { python = "/usr/bin/python3", work_root = root }));
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Fern.Run.Cli/bin/Debug/net11.0/fern-run.dll"));
        Assert.True(File.Exists(binary), $"missing built CLI: {binary}");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { binary, "install-supervisor-service", "--home", address, "--site", "pull-site",
                     "--profile", profile, "--fern-run", "/opt/fern-run", "--unit-directory", root }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "service installer did not finish");
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Contains("unit written", await stdout, StringComparison.Ordinal);
        var unit = File.ReadAllText(Path.Combine(root, StudySupervisorUnit.UnitName("pull-site")));
        Assert.Contains("--home " + address, unit, StringComparison.Ordinal);
        Assert.DoesNotContain("--store", unit, StringComparison.Ordinal);
    }

    static StudySupervisorUnitConfig Config(string site = "local-4070", string? results = "/home/x/results") => new(
        FernRunBinary: "/home/x/deploy/run-operator-binaries/fern-run.abc",
        Registry: "/home/x/lifecycle.db",
        SiteName: site,
        Python: "/home/x/.venv/bin/python",
        WorkRoot: "/home/x/tmp/fern-run-worker",
        ResultsRoot: results,
        LeaseSeconds: 600,
        IdleSeconds: 30);

    static Refused RefusedOf(ServiceUnitRender render) => render switch
    {
        Refused refused => refused,
        _ => throw new InvalidOperationException($"expected a refusal, got {render}"),
    };

    static string Rendered(StudySupervisorUnitConfig config)
    {
        var render = StudySupervisorUnit.Render(config);
        return render switch
        {
            Accepted<byte[]> accepted => Encoding.UTF8.GetString(accepted.Value),
            Refused refused => throw new InvalidOperationException(refused.Detail),
            _ => throw new InvalidOperationException(render.ToString()),
        };
    }

    [Fact]
    public void TheUnitStartsTheSupervisorOfTheSiteAndKeepsTheDaemonsRestartPolicy()
    {
        var unit = Rendered(Config());

        Assert.Contains(
            "ExecStart=/home/x/deploy/run-operator-binaries/fern-run.abc run-study-cells --serve --store /home/x/lifecycle.db --site local-4070"
            + " --python /home/x/.venv/bin/python --work-root /home/x/tmp/fern-run-worker"
            + " --lease-seconds 600 --idle-seconds 30 --results-root /home/x/results\n",
            unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure\n", unit, StringComparison.Ordinal);
        Assert.Contains("RestartPreventExitStatus=2\n", unit, StringComparison.Ordinal);
        Assert.Contains("RestartSec=5s\n", unit, StringComparison.Ordinal);
        Assert.Contains("KillMode=control-group\n", unit, StringComparison.Ordinal);
        Assert.Contains("ConditionPathExists=/home/x/lifecycle.db\n", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("fern-scheduler-cs", unit, StringComparison.Ordinal);
        Assert.Equal("fern-study-supervisor-local-4070.service", StudySupervisorUnit.UnitName("local-4070"));
    }

    [Fact]
    public void WithoutAResultsRootTheCellHandsItsDocumentBack()
    {
        var unit = Rendered(Config(results: null));
        Assert.DoesNotContain("--results-root", unit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pod 5090")]
    [InlineData("pod/5090")]
    [InlineData("")]
    public void ASiteNameThatCannotNameAUnitIsRefused(string site)
    {
        var refused = RefusedOf(StudySupervisorUnit.Render(Config(site: site)));
        Assert.Contains("Site name", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathIsRefusedBeforeAnythingIsWritten()
    {
        var refused = RefusedOf(StudySupervisorUnit.Render(Config() with { Registry = "lifecycle.db" }));
        Assert.Contains("must be absolute", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeaseShorterThanAMinuteIsRefused()
    {
        var refused = RefusedOf(StudySupervisorUnit.Render(Config() with { LeaseSeconds = 5 }));
        Assert.Contains("at least 60 s", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Slice F7: the unit carries the Gleam program, the compiler, the environment, and a PATH with
    /// erl on it. On 2026-09-26 a hand-made unit without that PATH failed on "erl: not found".
    /// </summary>
    [Fact]
    public void AUnitWithTheSchedulerProgramPutsErlOnItsPath()
    {
        var unit = Rendered(new StudySupervisorUnitConfig(
            "/home/user/study/runtime/deploy/run-operator-binaries/current", "/home/user/study/runtime/lifecycle.db",
            "local-4070", "/home/user/.venv/bin/python", "/home/user/study/tmp/fern-run-worker", null, 600, 30,
            SchedulerProgram: "/home/user/study/runtime/deploy/scheduler-programs/fern-scheduler-gleam.82cac795",
            ErlangDirectory: "/home/user/.local/bin",
            FernCompile: "/home/user/study/runtime/deploy/compile-binaries/fern-compile.888e83de",
            Environment: [("OMP_NUM_THREADS", "1"), ("PYTHONDONTWRITEBYTECODE", "1")]));

        Assert.Contains("Environment=PATH=/home/user/.local/bin:/usr/local/sbin:", unit);
        Assert.Contains("Environment=FERN_COMPILE_BIN=/home/user/study/runtime/deploy/compile-binaries/fern-compile.888e83de\n", unit);
        Assert.Contains("Environment=OMP_NUM_THREADS=1\n", unit);
        Assert.Contains(" --fern-compile /home/user/study/runtime/deploy/compile-binaries/fern-compile.888e83de", unit);
        Assert.Contains(" --scheduler-program /home/user/study/runtime/deploy/scheduler-programs/fern-scheduler-gleam.82cac795\n", unit);

        var without = StudySupervisorUnit.Render(new StudySupervisorUnitConfig(
            "/x/fern-run", "/x/lifecycle.db", "local-4070", "/x/python", "/x/work", null, 600, 30,
            SchedulerProgram: "/x/program"));
        Assert.True(without is Refused, "a scheduler program without the folder erl is in is refused");
    }
}
