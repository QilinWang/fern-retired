using System.Text;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class WorkerEnvironmentPreparationTests
{
    [Theory]
    [InlineData("uv 0.11.8 (verified-build)", true)]
    [InlineData("uv 0.10.0", false)]
    [InlineData("uv 0.11.80", false)]
    [InlineData("", false)]
    public void SuccessfulVersionCommandStillMustMatchThePinnedVersion(string output, bool expected)
        => Assert.Equal(expected, WorkerEnvironmentPreparation.IsExpectedUvVersion(output));

    [Fact]
    public void ReadyEnvironmentSkipsPackageMutationButStillChecksCudaAndCompiles()
    {
        var calls = new List<WorkerEnvironmentPreparation.Pass>();
        var result = WorkerEnvironmentPreparation.RunPasses(true, pass => { calls.Add(pass); return null; });
        Assert.Null(result);
        Assert.Equal([WorkerEnvironmentPreparation.Pass.CheckCuda, WorkerEnvironmentPreparation.Pass.CompileStudy], calls);
    }

    [Fact]
    public void FreshEnvironmentUsesTheSameOrderedPassesAndStopsAtFirstFailure()
    {
        var calls = new List<WorkerEnvironmentPreparation.Pass>();
        var result = WorkerEnvironmentPreparation.RunPasses(false, pass =>
        {
            calls.Add(pass);
            return pass == WorkerEnvironmentPreparation.Pass.InstallPackages ? "package download failed" : null;
        });
        Assert.Contains("package download failed", result);
        Assert.Equal([WorkerEnvironmentPreparation.Pass.InstallUv, WorkerEnvironmentPreparation.Pass.ExportLock,
            WorkerEnvironmentPreparation.Pass.CreatePython, WorkerEnvironmentPreparation.Pass.InstallPackages], calls);
        Assert.DoesNotContain(WorkerEnvironmentPreparation.Pass.CompileStudy, calls);
    }

    [Fact]
    public void FailedCudaCheckCannotBecomeReadyOrCompileAStudy()
    {
        var calls = new List<WorkerEnvironmentPreparation.Pass>();
        Assert.NotNull(WorkerEnvironmentPreparation.RunPasses(true, pass =>
        {
            calls.Add(pass);
            return "CUDA unavailable";
        }));
        Assert.Equal([WorkerEnvironmentPreparation.Pass.CheckCuda], calls);
    }

    [Fact]
    public void RealPreparationOwnerInstallsThenReusesAndPassesTheMatchingHostToTheCompiler()
    {
        using var fixture = new PreparationFixture();
        var first = WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture);
        Assert.False(first.ReusedEnvironment);
        Assert.True(File.Exists(first.CompiledStudy));
        Assert.Contains(fixture.Calls, arguments => arguments.Contains("--locked"));
        Assert.Contains(fixture.Calls, arguments => arguments.Contains("--require-hashes"));
        Assert.DoesNotContain(fixture.Calls, arguments => arguments.Contains("sync") || arguments.Contains("run-cell"));
        fixture.Calls.Clear();
        var second = WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture);
        Assert.True(second.ReusedEnvironment);
        Assert.Equal(first.Python, second.Python);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal("-c", fixture.Calls[0][0]);
        Assert.Equal("compile", fixture.Calls[1][3]);
    }

    [Fact]
    public void AChangedLockGetsItsOwnEnvironmentAndTheFirstIsFoundAgain()
    {
        using var fixture = new PreparationFixture();
        var first = WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture);
        var lockFile = Path.Combine(fixture.Request.Source, "uv.lock");
        Assert.Equal(File.ReadAllBytes(lockFile), File.ReadAllBytes(Path.Combine(first.Environment, "inputs", "uv.lock")));
        Assert.EndsWith("python3.14-uv0.11.8-1", first.Environment);
        var original = File.ReadAllText(lockFile);
        File.WriteAllText(lockFile, original + "\n# one more pin\n");
        var second = WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture);
        Assert.False(second.ReusedEnvironment);
        Assert.EndsWith("python3.14-uv0.11.8-2", second.Environment);
        File.WriteAllText(lockFile, original);
        var third = WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture);
        Assert.True(third.ReusedEnvironment);
        Assert.Equal(first.Environment, third.Environment);
    }

    [Fact]
    public void WrongInstalledUvStopsTheRealOwnerBeforePackageOrInterpreterChanges()
    {
        using var fixture = new PreparationFixture { UvOutput = "uv 0.10.0" };
        var error = Assert.Throws<InvalidOperationException>(() => WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture));
        Assert.Contains("expected uv 0.11.8", error.Message);
        Assert.DoesNotContain(fixture.Calls, arguments => arguments[0] is "export" or "venv" or "pip");
        Assert.Empty(Directory.GetFiles(fixture.Request.RuntimeRoot, "prepared.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void FailedCudaLeavesNoPreparedReceiptAndDoesNotCallCompiler()
    {
        using var fixture = new PreparationFixture { CudaExit = 1 };
        Assert.Throws<InvalidOperationException>(() => WorkerEnvironmentPreparation.Execute(fixture.Request, TextWriter.Null, fixture));
        Assert.Empty(Directory.GetFiles(fixture.Request.RuntimeRoot, "prepared.json", SearchOption.AllDirectories));
        Assert.DoesNotContain(fixture.Calls, arguments => arguments.Contains("compile"));
    }

    sealed class PreparationFixture : WorkerEnvironmentPreparation.IHost, IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("fern-prepare-owner-").FullName;
        public WorkerEnvironmentPreparation.Request Request { get; }
        public List<string[]> Calls { get; } = [];
        public bool IsLinux => true;
        public string UvOutput { get; init; } = "uv 0.11.8 (fixture)";
        public int CudaExit { get; init; }
        public PreparationFixture()
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            foreach (var name in new[] { "pyproject.toml", "uv.lock", "study.json", "fern-run", "fern-compile" })
                File.WriteAllText(Path.Combine(source, name), "fixture");
            Request = new(source, Path.Combine(root, "runtime"), "study.json",
                Path.Combine(source, "fern-run"), Path.Combine(source, "fern-compile"));
        }
        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            Assert.Equal(Request.Source, workingDirectory);
            Assert.Equal(Request.FernRun, environment["FERN_RUN_BIN"]);
            // The cell asks the Go compiler for its configuration's identity
            // before it trains; a prepared environment that does not name the
            // compiler is an environment that refuses on its first cell.
            Assert.Equal(Request.FernCompile, environment["FERN_COMPILE_BIN"]);
            // CHG 2026-09-26: src/ is an import root since serie moved to src/serie/.
            Assert.Equal(Request.Source + ":" + Path.Combine(Request.Source, "python") + ":" + Path.Combine(Request.Source, "src"),
                environment["PYTHONPATH"]);
            Assert.InRange(timeoutMs, 1, 1_200_000);
            if (args[0] == "--version") return new BoundedProbe.Outcome.Completed(0, Encoding.UTF8.GetBytes(UvOutput), "");
            if (program == "/bin/sh")
            {
                Directory.CreateDirectory(environment["UV_UNMANAGED_INSTALL"]);
                File.WriteAllText(Path.Combine(environment["UV_UNMANAGED_INSTALL"], "uv"), "fake executable");
            }
            if (args[0] == "export") File.WriteAllText(args[^1], "fixture requirements");
            if (args[0] == "venv")
            {
                Directory.CreateDirectory(Path.Combine(args[^1], "bin"));
                File.WriteAllText(Path.Combine(args[^1], "bin", "python"), "fake executable");
            }
            if (args[0] == "-c") return new BoundedProbe.Outcome.Completed(CudaExit, [], CudaExit == 0 ? "" : "CUDA unavailable");
            if (args[0] == "-m") File.WriteAllText(args[^1], "{\"schema\":\"fern.compiled-study.v1\",\"dataset\":{\"inputs\":{}},\"cells\":[]}");
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
