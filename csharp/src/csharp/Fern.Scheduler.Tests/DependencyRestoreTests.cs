using System.Diagnostics;
using Xunit;

namespace Fern.Scheduler.Tests;

// Exercise NuGet against disposable inputs; never edit the checkout graph to test refusal.
public sealed class DependencyRestoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedRestoreRejectsChangedOrMissingLockWithoutRewritingIt(bool missingLock)
    {
        var root = Directory.CreateTempSubdirectory("fern-dependency-restore-").FullName;
        try
        {
            var repo = TestRegistry.RepoRoot();
            File.Copy(Path.Combine(repo, "global.json"), Path.Combine(root, "global.json"));
            File.Copy(Path.Combine(repo, "src/csharp/Directory.Build.props"), Path.Combine(root, "Directory.Build.props"));
            File.Copy(Path.Combine(repo, "src/csharp/Directory.Packages.props"), Path.Combine(root, "Directory.Packages.props"));
            const string prefix = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>";
            var project = Path.Combine(root, "Fixture.csproj");
            File.WriteAllText(project, prefix + "</Project>");
            var initial = await Restore(root, "-p:RestoreLockedMode=false");
            Assert.True(initial.Exit == 0, initial.Output);
            var lockPath = Path.Combine(root, "packages.lock.json");
            var before = File.ReadAllBytes(lockPath);
            var unchanged = await Restore(root);
            Assert.True(unchanged.Exit == 0, unchanged.Output);

            if (missingLock) File.Delete(lockPath);
            else File.WriteAllText(project, prefix + "<ItemGroup><PackageReference Include=\"System.CommandLine\" /></ItemGroup></Project>");
            var changed = await Restore(root);

            Assert.NotEqual(0, changed.Exit);
            Assert.Contains(missingLock ? "FERNLOCK001" : "NU1004", changed.Output, StringComparison.Ordinal);
            if (missingLock) Assert.False(File.Exists(lockPath));
            else Assert.Equal(before, File.ReadAllBytes(lockPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static async Task<(int Exit, string Output)> Restore(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "restore", "Fixture.csproj", "--disable-build-servers", "--verbosity", "quiet", "-m:1" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw;
        }
        return (process.ExitCode, await output + await errors);
    }
}
