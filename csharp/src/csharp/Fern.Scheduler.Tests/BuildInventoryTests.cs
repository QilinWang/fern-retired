using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class BuildInventoryTests
{
    [Fact]
    public void RustWorkerDeployCommandIsRetiredFromTheCsharpOperatorSurface()
    {
        var root = TestRegistry.RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "csharp", "Fern.Run.Cli", "Program.cs"));

        Assert.DoesNotContain("DeployVerbs.Add", program, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "src", "csharp", "Fern.Run.Cli", "DeployVerbs.cs")));
        // The retired copy under retired/csharp/2026-08-30-rust-worker-deploy
        // left the worktree with commit 89be5954; git history holds it, and
        // this test only guards that the operator surface has no deploy verb.
    }

    [Fact]
    public void UnifiedSolutionContainsEveryControlPlaneProjectAndActiveSchedulerCallersUseCsharpTree()
    {
        var root = TestRegistry.RepoRoot();
        var solution = File.ReadAllLines(Path.Combine(root, "src", "csharp", "Fern.sln"));
        var projectPaths = solution
            .Where(line => line.StartsWith("Project(\"", StringComparison.Ordinal))
            .Select(line => line.Split('"')[5])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedProjectPaths = new[]
        {
            // Dashboard is now the Gleam application in tools/fern-dash-gleam.
            @"Fern.Experiment.Cli\Fern.Experiment.Cli.csproj",
            @"Fern.Experiment.Tests\Fern.Experiment.Tests.csproj",
            @"Fern.Experiment\Fern.Experiment.csproj",
            // CHG 2026-09-12: the first split out of Fern.Scheduler. It owns
            // publishing and verifying the binaries a machine runs; ordinal
            // order puts it before every Fern.R* path.
            @"Fern.Install\Fern.Install.csproj",
            @"Fern.Registry.Analyzer\Fern.Registry.Analyzer.csproj",
            @"Fern.Registry.Cli\Fern.Registry.Cli.csproj",
            @"Fern.Registry.Tests\Fern.Registry.Tests.csproj",
            @"Fern.Registry\Fern.Registry.csproj",
            @"Fern.Run.Cli\Fern.Run.Cli.csproj",
            @"Fern.RunData\Fern.RunData.csproj",
            @"Fern.Scheduler.Tests\Fern.Scheduler.Tests.csproj",
            @"Fern.Scheduler\Fern.Scheduler.csproj",
            @"Fern.Site.Cli\Fern.Site.Cli.csproj",
            @"Fern.Site.Tests\Fern.Site.Tests.csproj",
            @"Fern.Site\Fern.Site.csproj",
            // CHG 2026-09-14: Fern.StageRunner.Cli left the solution with the
            // Rust-era stage contracts its two verbs were built on.
            // CHG 2026-09-11: the three training-schema projects left the
            // solution with the vocabulary cut. C# reads no scientific words,
            // so it needs no reader generated from a schema; the Go compiler
            // under tools/fern-compile owns them, and the generator is evidence
            // under retired/csharp/training-schema-generator-2026-09-11.
            // The analyzer was already in the solution and missing from this
            // list, which is what a sequence comparison is for.
            // CHG 2026-09-26: Fern.Workflow.Analyzer left the solution on the
            // owner's word (docs/programs/2026-09-26-cards-without-hashes,
            // consequences.md B23): the workflow pins it checked are gone.
        };

        Assert.Equal(expectedProjectPaths, projectPaths);
        foreach (var projectPath in projectPaths)
        {
            Assert.True(
                File.Exists(Path.Combine(root, "src", "csharp", projectPath.Replace('\\', Path.DirectorySeparatorChar))),
                $"Fern.sln names missing project {projectPath}");
        }

        // CHG 2026-08-28: this half used to read fern-cli-rs/src/command_runtime.rs
        // and scheduler_service.rs to prove the Rust caller pointed at
        // csharp/Fern.Scheduler.Serve/bin rather than at the old
        // tools/fern-scheduler-cs. The Rust tree was retired that day, so there
        // is no caller left to read and the promise had to be restated for a
        // tree with no Rust: NO LIVE CALLER NAMES A RETIRED TREE.
        //
        // Both halves are asserted, because either alone is satisfiable by
        // accident. A directory can be gone while a path into it survives in
        // code, and a path can be absent from code while the directory sits
        // there inviting someone to use it.
        foreach (var retired in new[] { "fern-cli-rs", Path.Combine("tools", "fern-scheduler-cs") })
        {
            Assert.False(
                Directory.Exists(Path.Combine(root, retired)),
                $"{retired} was retired; a copy in the worktree is something a caller can still find");
        }

        // Provenance is not a caller. Production C# cites fern-cli-rs by the
        // hundred — "Ported from fern-cli-rs/src/…" — and those comments are
        // the record of where each port came from, so the rule is about code,
        // not about the string. Every mention outside a comment would be a path
        // this tree still resolves.
        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "csharp"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, source);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(source))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(
                    code.Contains("tools/fern-scheduler-cs", StringComparison.Ordinal),
                    $"{relative} names the retired scheduler location outside a comment: {code}");
                Assert.False(
                    code.Contains("fern-cli-rs/", StringComparison.Ordinal),
                    $"{relative} resolves a path into the retired Rust tree: {code}");
            }
        }
    }

    /// <summary>
    /// Identity-bearing bytes are proved against the Go binary's output, not
    /// against C#. These rows are the active binary set the Go installer wrote
    /// into deploy/build-inventory.db (source_revision f2bf16589935+dirty,
    /// installed_at_ms 1787727215319), read with sqlite3 on 2026-08-27.
    /// </summary>
    [Fact]
    public void SetIdReproducesTheGoWrittenActiveBinarySet()
    {
        var members = new List<BinarySetMember>
        {
            new("fern-worker", "7c268979dffc6469db70982edf2c92ec844fa0e7c468c435793532baf3b6ad7d", "/x/fern-worker"),
            new("fern-stage-runner", "afcff758a184cdf895fda1c8b6d8249ed97a6a9cf5b560dba66cd1da9eb357c2", "/x/fern-stage-runner"),
            new("fern-site", "bb71fdfb5ffd99e204c0e9376fea5cfe7dad1174b47d4635b08498d108514269", "/x/fern-site"),
            new("fern-scheduler", "be67e40085e89b89eb5bb4d600707712f6b1b0cf94bbf1fe8c9e85eceb988f9d", "/x/fern-scheduler"),
            new("fern-run", "b897b7eb5983bbac36a3b9cfc9c398eee11cdc61561a2e72715426ef042bb1e5", "/x/fern-run"),
            new("fern-home", "f3b549504d9877778c649d88f8d58fa6215dbf1d9cc20fd6def425fd867201a9", "/x/fern-home"),
            new("fern-experiment", "25d2fbe70d3fd833fd164d3b9fc76e5f4f30eaeb0ddaae0de399e652e79c5838", "/x/fern-experiment"),
            new("fern-card", "caab59b0450d4ba604e401d878b56a6b1796aa314dd2ec0e2cb3469df8055640", "/x/fern-card"),
        };
        var identity = UnionCase.Of<Accepted<string>>(BuildInventory.SetIdFor(members));
        Assert.Equal("sha256:edd9b1eccd03a794dac273756a339fc2081902db24a11fc88a356c87d5677a13", identity.Value);
    }

    [Fact]
    public void SetIdRefusesUppercaseDigestsRepeatsAndEmptySets()
    {
        Assert.Contains(
            "a binary set needs at least one member",
            UnionCase.Of<Refused>(BuildInventory.SetIdFor([])).Detail,
            StringComparison.Ordinal);
        var upper = new BinarySetMember("fern-run", new string('A', 64), "/x");
        Assert.Contains("64 lowercase hexadecimal", UnionCase.Of<Refused>(BuildInventory.SetIdFor([upper])).Detail, StringComparison.Ordinal);
        var twice = new BinarySetMember("fern-run", new string('a', 64), "/x");
        Assert.Contains("repeats", UnionCase.Of<Refused>(BuildInventory.SetIdFor([twice, twice])).Detail, StringComparison.Ordinal);
    }
}
