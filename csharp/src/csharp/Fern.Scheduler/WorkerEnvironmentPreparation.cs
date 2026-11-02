using System.Text;
using System.Text.Json;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>
/// Prepare a worker on the machine where it will execute. Source transport and
/// scheduling remain with their existing owners. Ordered workflow:
/// docs/how-to/worker-preparation.md.
/// </summary>
public static class WorkerEnvironmentPreparation
{
    public const string UvVersion = "0.11.8";
    public const string PythonVersion = "3.14";
    // The extras the ExportLock pass installs; recorded with each environment.
    public const string Extras = "train,analysis,dev";
    public enum Pass { InstallUv, ExportLock, CreatePython, InstallPackages, CheckCuda, CompileStudy }
    internal static bool IsExpectedUvVersion(string output) =>
        output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is ["uv", UvVersion, ..];
    public sealed record Request(string Source, string RuntimeRoot, string Study, string FernRun, string FernCompile);
    public interface IHost
    {
        bool IsLinux { get; }
        BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment);
    }
    /// <summary>
    /// CHG 2026-09-11 night (slice 2 of the cell port, row 23): a host that can
    /// be told to end the process early. The production host is one; a fake
    /// that answers without a process need not be, so <see cref="IHost"/> is
    /// unchanged and a caller asks for this at the call.
    /// </summary>
    public interface ICancellableHost : IHost
    {
        BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation);
    }
    public sealed class ExecutionHost : ICancellableHost
    {
        public bool IsLinux => OperatingSystem.IsLinux();
        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            BoundedProbe.Run(program, arguments, timeoutMs, maxBytes, workingDirectory, environment);
        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation) =>
            BoundedProbe.Run(program, arguments, timeoutMs, maxBytes, workingDirectory, environment, cancellation);
    }
    // CHG 2026-09-26: Environment (the folder holding the venv and its recorded inputs)
    // where LockSha256 was; the schema moves to v2 with the shape.
    public sealed record Report(string Python, string CompiledStudy, string Environment, string Uv, bool ReusedEnvironment)
    {
        public string Schema => "fern.worker_environment_preparation.v2";
    }

    /// <summary>
    /// The environment folder under <paramref name="environments"/> whose recorded inputs
    /// (pyproject.toml, uv.lock and environment.json with the versions and extras) equal
    /// these bytes, or a new folder `python&lt;version&gt;-uv&lt;version&gt;-&lt;n&gt;` holding them.
    /// A new folder is filled under a temporary name and renamed, so no folder named like
    /// an environment is ever without its inputs. The caller holds the preparation lease.
    /// </summary>
    internal static string FindOrCreateEnvironment(string environments, byte[] projectBytes, byte[] lockBytes)
    {
        var settings = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { python_version = PythonVersion, uv_version = UvVersion, extras = Extras }));
        (string Name, byte[] Bytes)[] inputs = [("pyproject.toml", projectBytes), ("uv.lock", lockBytes), ("environment.json", settings)];
        Directory.CreateDirectory(environments);
        var prefix = $"python{PythonVersion}-uv{UvVersion}-";
        foreach (var folder in Directory.GetDirectories(environments, prefix + "*").Order(StringComparer.Ordinal))
        {
            if (inputs.All(input => File.Exists(Path.Combine(folder, "inputs", input.Name))
                && File.ReadAllBytes(Path.Combine(folder, "inputs", input.Name)).AsSpan().SequenceEqual(input.Bytes)))
                return folder;
        }
        var number = 1;
        while (Directory.Exists(Path.Combine(environments, prefix + number))) number++;
        var staging = Path.Combine(environments, ".creating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "inputs"));
        foreach (var input in inputs) File.WriteAllBytes(Path.Combine(staging, "inputs", input.Name), input.Bytes);
        var created = Path.Combine(environments, prefix + number);
        Directory.Move(staging, created);
        return created;
    }

    // These passes have one owner so fresh and existing machines cannot acquire
    // separate sequences of hand-written repair commands.
    internal static string? RunPasses(bool prepared, Func<Pass, string?> execute)
    {
        foreach (var pass in Enum.GetValues<Pass>())
        {
            if (prepared && pass < Pass.CheckCuda) continue;
            var problem = execute(pass);
            if (problem is not null) return $"{pass}: {problem}";
        }
        return null;
    }

    public static Report Execute(Request request, TextWriter progress, IHost host)
    {
        if (!host.IsLinux)
            throw new InvalidOperationException("prepare-worker-environment must run on the Linux execution host");
        var source = Path.GetFullPath(request.Source);
        var runtime = Path.GetFullPath(request.RuntimeRoot);
        var study = Path.GetFullPath(request.Study, source);
        var project = Path.Combine(source, "pyproject.toml");
        var dependencyLock = Path.Combine(source, "uv.lock");
        foreach (var path in new[] { study, project, dependencyLock })
            if (!File.Exists(path)) throw new InvalidOperationException($"required source input is missing: {path}");
        if (runtime == source || runtime.StartsWith(source + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("runtime root must be outside the borrowed source tree");
        var fernRun = request.FernRun;
        if (string.IsNullOrEmpty(fernRun) || !File.Exists(fernRun))
            throw new InvalidOperationException("FERN_RUN_BIN must identify the source-matched apphost; use bootstrap_fern.sh");
        // The cell's first act is to ask the Go compiler for its configuration's
        // identity. A frozen source snapshot holds no Go build, so the compiler
        // is an INSTALLED binary on this machine, named here the way fern-run is.
        var fernCompile = request.FernCompile;
        if (string.IsNullOrEmpty(fernCompile) || !File.Exists(fernCompile))
            throw new InvalidOperationException("FERN_COMPILE_BIN must identify the installed fern-compile; install the control plane on this machine");

        var projectBytes = File.ReadAllBytes(project);
        var lockBytes = File.ReadAllBytes(dependencyLock);
        // The environment owns dependencies only. PYTHONPATH supplies the
        // separately verified source, so a code-only change reuses packages.
        // CHG 2026-09-26 (owner: "get rid of 256 entirely"): an environment was the folder
        // named by a SHA-256 over the versions, the extras, pyproject.toml and uv.lock. It is
        // now a folder named by the versions and a number that keeps those inputs in
        // inputs/; this source's environment is the one whose inputs equal its bytes.
        // Folders named by the old hash are no longer found and are rebuilt once.
        Directory.CreateDirectory(runtime);
        using var lease = new FileStream(Path.Combine(runtime, ".worker-preparation.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var environmentRoot = FindOrCreateEnvironment(Path.Combine(runtime, "environments"), projectBytes, lockBytes);
        var python = Path.Combine(environmentRoot, "venv", "bin", "python");
        var uvDirectory = Path.Combine(runtime, "tools", "uv-" + UvVersion);
        var uv = Path.Combine(uvDirectory, "uv");
        var receipt = Path.Combine(environmentRoot, "prepared.json");
        var prepared = File.Exists(receipt);
        if (prepared)
        {
            // CHG 2026-09-26: the receipt no longer carries an identity to compare; the
            // folder was chosen by its recorded inputs.
            if (!File.Exists(python))
                throw new InvalidOperationException($"prepared environment is damaged: {environmentRoot}; it will not be modified in place");
        }
        var invocation = Directory.CreateDirectory(Path.Combine(runtime, "prepare-" + Guid.NewGuid().ToString("N"))).FullName;
        var requirements = Path.Combine(invocation, "requirements.txt");
        var compiled = Path.Combine(invocation, "compiled-study.json");
        var variables = new Dictionary<string, string>
        {
            // CHG 2026-09-26: src/ joins the import roots (serie/ moved to src/serie/).
            ["PYTHONPATH"] = source + ":" + Path.Combine(source, "python") + ":" + Path.Combine(source, "src"),
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["FERN_SOURCE_ROOT"] = source,
            ["FERN_RUN_BIN"] = fernRun,
            ["FERN_COMPILE_BIN"] = fernCompile,
            ["UV_UNMANAGED_INSTALL"] = uvDirectory,
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(runtime, "python"),
            ["UV_CACHE_DIR"] = Path.Combine(runtime, "cache", "uv"),
            ["UV_NO_PROGRESS"] = "1",
            ["UV_HTTP_TIMEOUT"] = "60",
            ["UV_HTTP_RETRIES"] = "2",
        };
        string? Run(string program, string[] arguments, long timeout)
        {
            var outcome = host.Run(program, arguments, timeout, 1_048_576, source, variables);
            return outcome switch
            {
                BoundedProbe.Outcome.Completed { ExitCode: 0 } => null,
                BoundedProbe.Outcome.Completed failed => $"exit {failed.ExitCode}: {failed.Stderr}",
                _ => outcome.ToString(),
            };
        }
        string? VerifyUv()
        {
            var outcome = host.Run(uv, ["--version"], 15_000, 4096, source, variables);
            return outcome is BoundedProbe.Outcome.Completed { ExitCode: 0 } completed
                && IsExpectedUvVersion(Encoding.UTF8.GetString(completed.Stdout))
                ? null : $"expected uv {UvVersion} at {uv}; observed {outcome}";
        }
        var refusal = RunPasses(prepared, pass =>
        {
            progress.WriteLine($"worker preparation: {pass}");
            switch (pass)
            {
                case Pass.InstallUv:
                    if (File.Exists(uv)) return VerifyUv();
                    Directory.CreateDirectory(uvDirectory);
                    var installer = Path.Combine(invocation, "uv-installer.sh");
                    return Run("/usr/bin/curl", ["--fail", "--silent", "--show-error", "--location", "--connect-timeout", "15",
                        "--max-time", "120", "--output", installer,
                        $"https://releases.astral.sh/github/uv/releases/download/{UvVersion}/uv-installer.sh"], 130_000)
                        ?? Run("/bin/sh", [installer], 300_000)
                        ?? VerifyUv();
                case Pass.ExportLock:
                    return Run(uv, ["export", "--locked", "--extra", "train", "--extra", "analysis", "--extra", "dev",
                        "--no-dev", "--no-emit-project", "--format", "requirements-txt", "--output-file", requirements], 120_000);
                case Pass.CreatePython:
                    // Never clear an existing venv, especially one a worker may
                    // hold. A successful environment is never mutated again.
                    return File.Exists(python) ? null : Run(uv, ["venv", "--python", PythonVersion, Path.GetDirectoryName(Path.GetDirectoryName(python))!], 300_000);
                case Pass.InstallPackages:
                    return Run(uv, ["pip", "install", "--python", python, "--require-hashes", "--requirement", requirements], 1_200_000);
                case Pass.CheckCuda:
                    var problem = Run(python, ["-c", "import sys, serie, fern_train, fern_analysis, other_models, torch; "
                        + "assert sys.version_info[:2] == (3,14), sys.version; "
                        + "assert torch.cuda.is_available(), 'CUDA unavailable on execution host'"], 120_000);
                    if (problem is not null) return problem;
                    if (!projectBytes.AsSpan().SequenceEqual(File.ReadAllBytes(project))
                        || !lockBytes.AsSpan().SequenceEqual(File.ReadAllBytes(dependencyLock)))
                        return "dependency source changed during preparation";
                    if (!prepared)
                    {
                        File.WriteAllText(receipt + ".partial", JsonSerializer.Serialize(new { python,
                            python_version = PythonVersion, uv_version = UvVersion, extras = Extras, prepared_at = DateTimeOffset.UtcNow }));
                        File.Move(receipt + ".partial", receipt, overwrite: false);
                    }
                    return null;
                case Pass.CompileStudy:
                    return Run(python, ["-m", "fern_analysis", "study", "compile", study, "--device", "cuda", "--out", compiled], 300_000);
                default: throw new ArgumentOutOfRangeException(nameof(pass));
            }
        });
        if (refusal is not null) throw new InvalidOperationException($"{refusal}; preparation files: {invocation}");
        if (!File.Exists(compiled)) throw new InvalidOperationException("Study compiler returned success without its output");
        StudyFileInputs.Verify(source, StudyFileInputs.Read(compiled, source));
        var report = new Report(python, compiled, environmentRoot, uv, prepared);
        File.WriteAllText(Path.Combine(invocation, "preparation.json"), JsonSerializer.Serialize(report));
        return report;
    }
}
