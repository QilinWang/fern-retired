using System.Diagnostics;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Run the built fern-run binary and read back its exit code and streams.
///
/// It lived in ExperimentLaunchVerbTests, which owned the plan-experiment /
/// run-experiment flag surface; that file retired on 2026-09-04 with the recipe
/// route (retired/csharp/recipe-route-2026-09-04) and four other test classes
/// were calling this helper through it, so it moved here verbatim.
/// </summary>
public static class FernRunHost
{
    static string Host()
    {
        var path = Path.Combine(
            TestRegistry.RepoRoot(), "src", "csharp", "Fern.Run.Cli", "bin", "Debug", "net11.0", "fern-run");
        Assert.True(File.Exists(path), $"build src/csharp/Fern.Run.Cli first: {path} is absent");
        return path;
    }

    internal static (int Exit, string Stdout, string Stderr) Invoke(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Host(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = TestRegistry.RepoRoot(),
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, stdout, stderr);
    }
}
