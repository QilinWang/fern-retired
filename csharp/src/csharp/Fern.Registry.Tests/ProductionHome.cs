using System.Collections.Generic;
using System.Diagnostics;

namespace Fern.Registry.Tests;

/// <summary>
/// Initializes fixture Homes with the embedded current schema.
/// </summary>
internal static class ProductionHome
{
    internal sealed record SubprocessResult(int Exit, string Stdout, string Stderr);

    /// <summary>
    /// Declare that a fixture Site takes each cell's code from a repository, which
    /// a claim now requires. CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    /// slice 4): it replaces recording an installed source for the Site, which is retired.
    /// The repository need not exist for a claim; a worker that makes the code looks at it.
    /// </summary>
    public static void DeclareCodeFrom(RegistryWriter writer, long siteId, string codeFrom = "working-tree", string repository = "/repository")
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "UPDATE sites SET capabilities_json = json_set(capabilities_json, '$.code_from', $from, '$.repository', $repository) WHERE id = $id";
        command.Parameters.AddWithValue("$from", codeFrom);
        command.Parameters.AddWithValue("$repository", repository);
        command.Parameters.AddWithValue("$id", siteId);
        Xunit.Assert.Equal(1, command.ExecuteNonQuery());
    }

    public static string CreateRegistry()
    {
        var directory = Directory.CreateTempSubdirectory("fern-registry-b-");
        var path = Path.Combine(directory.FullName, "lifecycle.db");
        File.WriteAllBytes(path, []);
        Initialize(path);
        return path;
    }

    public static void Initialize(string registryPath)
    {
        switch (RegistrySchema.Initialize(registryPath))
        {
            case RegistryInitializationOutcome.Initialized:
                return;
            case RegistryInitializationOutcome.Refused refused:
                throw new InvalidOperationException($"current Registry initialization refused: {refused.Detail}");
            default:
                throw new InvalidOperationException("Registry initialization returned neither initialized nor refused");
        }
    }

    internal static SubprocessResult RunFernRegistry(string[] args, string stdin, int deadlineMilliseconds) =>
        RunFernRegistryIn(null, args, stdin, deadlineMilliseconds);

    /// <summary>
    /// The same process, started in <paramref name="workingDirectory"/> so a
    /// command that takes relative paths (grid-gaps, inspect) is exercised
    /// exactly as its callers spawn it.
    /// </summary>
    internal static SubprocessResult RunFernRegistryIn(
        string? workingDirectory,
        string[] args,
        string stdin,
        int deadlineMilliseconds,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var executable = Environment.GetEnvironmentVariable("FERN_TEST_REGISTRY_BIN") ?? Path.Combine(
            FindRepoRoot(),
            "src", "csharp", "Fern.Registry.Cli", "bin", "Debug", "net11.0", "fern-registry");
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"fern-registry was not built at {executable}");
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start fern-registry");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        if (!process.WaitForExit(deadlineMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"fern-registry {args[0]} exceeded {deadlineMilliseconds} milliseconds");
        }
        return new SubprocessResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    /// <summary>Return one immutable Registry contract fixture owned by the current test target.</summary>
    internal static string ContractFixturePath(string name)
    {
        var root = FindRepoRoot();
        if (Path.IsPathRooted(name)
            || name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "contract fixture name must be relative without parent traversal",
                nameof(name));
        }
        var path = Path.Combine(root, "src", "csharp", "Fern.Registry.Tests", "Fixtures", "contracts", name);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"the Registry contract fixture is missing: {path}", path);
    }

    internal static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            // The C# solution and its owned schema are durable markers
            // of this checkout; no retired language owner is required.
            if (File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.sln"))
                && File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.Registry", "Schema.sql")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("cannot locate the Fern repository root from the test host");
    }
}

/// <summary>
/// Process-contract host for tests that exercise the private fern-registry
/// command protocol. It deliberately shares no assembly reference with the
/// CLI: arguments and JSON cross the same executable boundary used in an
/// installation.
/// </summary>
internal static class RegistryCli
{
    public const int ExitOk = 0;
    public const int ExitFault = 1;
    public const int ExitRefused = 2;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var result = ProductionHome.RunFernRegistry(
            args,
            stdin.ReadToEnd(),
            deadlineMilliseconds: 30_000);
        stdout.Write(result.Stdout);
        stderr.Write(result.Stderr);
        return result.Exit;
    }
}
