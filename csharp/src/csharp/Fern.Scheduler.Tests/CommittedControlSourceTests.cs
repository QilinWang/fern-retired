using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Fern.Scheduler;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>Real Git objects, not shared-worktree cleanliness, determine installation source.</summary>
public sealed class CommittedControlSourceTests
{
    [Fact]
    public async Task GitTimeoutWaitsForExitAndBothOutputReadersBeforeCleanup()
    {
        using var repo = new Repository();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo.Root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        // A real Git child with inherited pipes, without installing a script
        // or changing another invocation's PATH or repository configuration.
        foreach (var argument in new[] { "-c", "alias.stall=!echo ready; echo ready >&2; exec sleep 30", "stall" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ready", await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var elapsed = Stopwatch.StartNew();

        var error = Assert.Throws<InvalidOperationException>(() =>
            CommittedControlSource.ReadGitOutput(process, output, errors, 1, 5_000));

        Assert.Contains("exceeded its deadline", error.Message, StringComparison.Ordinal);
        Assert.True(process.HasExited);
        Assert.True(output.IsCompletedSuccessfully);
        Assert.True(errors.IsCompletedSuccessfully);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Directory.Delete(repo.Root, recursive: true);
        Assert.False(Directory.Exists(repo.Root));
    }

    [Fact]
    public async Task UnfinishedGitOutputRetainsOnlyItsOwnBuildDirectory()
    {
        using var repo = new Repository();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo.Root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(5_000));
        await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5));
        // Deterministically represent a reader still awaiting inherited-pipe
        // EOF after its parent exited. Never leave a real orphan in a test.
        var unfinished = new TaskCompletionSource<string>();
        var build = new InstallCli.PreparedControlBuild();
        try
        {
            var error = Assert.Throws<CommittedControlSource.GitTerminationException>(() =>
                CommittedControlSource.ReadGitOutput(process, output, unfinished.Task, 1, 10));
            var retained = build.RetainForGitTermination(error);
            build.Dispose();
            Assert.Contains(build.Root, retained.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(build.Root));
            Assert.True(Directory.Exists(repo.Root));
        }
        finally
        {
            unfinished.SetResult("");
            Directory.Delete(build.Root, recursive: true);
        }
    }

    [Fact]
    public void NoDocumentTravelsWithTheBuildSource()
    {
        // CHG 2026-09-26: this was CommittedWorkflowDocumentsTravelWithTheBuildSource,
        // which held the committed docs/how-to/ files in the build source because
        // the workflow-contract analyzer compiled docs/how-to/study-run.md and its
        // README. The analyzer was removed on the owner's word (consequences.md
        // B23 in docs/programs/2026-09-26-cards-without-hashes); no document is a
        // build input now, committed or not, and none is archived with the source.
        using var repo = new Repository();
        repo.Write("docs/how-to/study-run.md", "committed workflow\n");
        repo.Write("docs/how-to/README.md", "committed index\n");
        repo.Git("add", "docs/how-to/study-run.md", "docs/how-to/README.md");
        repo.Git("commit", "--quiet", "-m", "workflow document");
        repo.Write("docs/how-to/study-run.md", "uncommitted workflow\n");
        repo.Write("docs/how-to/draft.md", "untracked workflow\n");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);

        Assert.False(File.Exists(Path.Combine(source.Root, "docs/how-to/study-run.md")));
        Assert.False(File.Exists(Path.Combine(source.Root, "docs/how-to/README.md")));
        Assert.False(File.Exists(Path.Combine(source.Root, "docs/how-to/draft.md")));
        Assert.DoesNotContain(source.Files.Keys, path => path.StartsWith("docs/", StringComparison.Ordinal));
        Assert.False(CommittedControlSource.WorktreeDiffers(repo.Root), "an edited document is not a source that differs from HEAD");
    }

    [Fact]
    // CHG 2026-09-27: the compiler is the Gleam project; its tests and recorded studies stay behind.
    public void TheCompilerProjectTravelsWithTheBuildSourceWithoutItsTestsOrRecords()
    {
        using var repo = new Repository();
        repo.Write("tools/fern-compile-gleam/gleam.toml", "name = \"fern_compile\"\n");
        repo.Write("tools/fern-compile-gleam/src/fern_compile.gleam", "pub fn main() { Nil }\n");
        repo.Write("tools/fern-compile-gleam/src/fern_compile/wire/decode.gleam", "pub fn x() { Nil }\n");
        repo.Write("tools/fern-compile-gleam/test/fern_compile_test.gleam", "pub fn x_test() { Nil }\n");
        repo.Write("tools/fern-compile-gleam/testdata/goldens/card--origin/cell-identities.json", "{}\n");
        repo.Write("tools/other-tool/main.go", "package main\n");
        repo.Git("add", "tools");
        repo.Git("commit", "--quiet", "-m", "gleam compiler");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);

        Assert.True(File.Exists(Path.Combine(source.Root, "tools/fern-compile-gleam/gleam.toml")));
        Assert.True(File.Exists(Path.Combine(source.Root, "tools/fern-compile-gleam/src/fern_compile.gleam")));
        Assert.True(File.Exists(Path.Combine(source.Root, "tools/fern-compile-gleam/src/fern_compile/wire/decode.gleam")));
        Assert.False(File.Exists(Path.Combine(source.Root, "tools/fern-compile-gleam/test/fern_compile_test.gleam")));
        Assert.False(File.Exists(Path.Combine(source.Root, "tools/fern-compile-gleam/testdata/goldens/card--origin/cell-identities.json")));
        Assert.False(File.Exists(Path.Combine(source.Root, "tools/other-tool/main.go")));
    }

    /// <summary>
    /// CHG 2026-09-14: a test project is a test input, not a build input. No
    /// published member is built from one -- a member's input digest is taken
    /// over its project reference closure, which never reaches a Fern.X.Tests.
    /// Counting them cost a real install: two untracked files another lane left
    /// in Fern.Registry.Tests made the tree differ from HEAD, and the only way
    /// through was --from-worktree, which stamps all four binaries "built from
    /// a dirty tree" and drops their source archive digest.
    /// </summary>
    [Fact]
    public void ATestProjectIsNeitherArchivedNorAReasonTheTreeDiffersFromHead()
    {
        using var repo = new Repository();
        repo.Write("src/csharp/Fern.Run.Cli/Verbs.cs", "committed verb\n");
        repo.Write("src/csharp/Fern.Registry.Tests/RegistrySchemaTests.cs", "committed test\n");
        repo.Git("add", "src/csharp");
        repo.Git("commit", "--quiet", "-m", "a project and its tests");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);
        Assert.True(File.Exists(Path.Combine(source.Root, "src/csharp/Fern.Run.Cli/Verbs.cs")));
        Assert.False(File.Exists(Path.Combine(source.Root, "src/csharp/Fern.Registry.Tests/RegistrySchemaTests.cs")));

        // The half that cost the install: an untracked file in a test project
        // is not a differing build source, and one beside a member is.
        repo.Write("src/csharp/Fern.Registry.Tests/RegistryInitializationTests.cs", "another lane's test\n");
        Assert.False(CommittedControlSource.WorktreeDiffers(repo.Root));
        repo.Write("src/csharp/Fern.Run.Cli/Another.cs", "an uncommitted verb\n");
        Assert.True(CommittedControlSource.WorktreeDiffers(repo.Root));
    }

    [Fact]
    public void UncommittedDocumentsAndWorkConfigurationDoNotBlockCommittedSource()
    {
        using var repo = new Repository();
        repo.Write("docs/rules/naming.md", "edited while publishing\n");
        repo.Write("work.toml", "another task\n");
        repo.Write("docs/new-design.md", "not committed\n");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);

        Assert.Equal(repo.Commit, source.Commit);
        Assert.Equal("committed code\n", File.ReadAllText(Path.Combine(source.Root, "src/csharp/Program.cs")));
        Assert.False(Directory.Exists(Path.Combine(source.Root, "docs")));
        Assert.False(File.Exists(Path.Combine(source.Root, "work.toml")));
        Assert.False(Directory.Exists(Path.Combine(source.Root, ".git")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source.ArchivePath))), source.ArchiveSha256);
    }

    [Fact]
    public void UncommittedCodeMigrationsAndBuildConfigurationNeverEnterTheArchive()
    {
        using var repo = new Repository();
        repo.Write("src/csharp/Program.cs", "uncommitted code\n");
        repo.Write("src/csharp/Migrations/up.sql", "uncommitted schema\n");
        repo.Write("Directory.Build.props", "uncommitted build configuration\n");
        repo.Write("src/csharp/New.cs", "untracked code\n");
        repo.Git("add", "src/csharp/Migrations/up.sql");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);

        Assert.Equal("committed code\n", File.ReadAllText(Path.Combine(source.Root, "src/csharp/Program.cs")));
        Assert.Equal("committed schema\n", File.ReadAllText(Path.Combine(source.Root, "src/csharp/Migrations/up.sql")));
        Assert.Equal("<Project />\n", File.ReadAllText(Path.Combine(source.Root, "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(source.Root, "src/csharp/New.cs")));
    }

    [Fact]
    public void EmptyRegularFilesRemainPartOfTheCommittedSource()
    {
        using var repo = new Repository();
        repo.Write("src/csharp/Empty.cs", "");
        repo.Git("add", "src/csharp/Empty.cs");
        repo.Git("commit", "--quiet", "-m", "empty source");
        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);
        Assert.Equal("", File.ReadAllText(Path.Combine(source.Root, "src/csharp/Empty.cs")));
    }

    [Theory]
    [InlineData("global.json")]
    // CHG 2026-09-26: docs/how-to/study-run.md and docs/how-to/README.md left
    // this list with the workflow-contract analyzer that compiled them; no
    // document is a build input (NoDocumentTravelsWithTheBuildSource above).
    [InlineData("src/csharp/Directory.Packages.props")]
    [InlineData("src/csharp/Example/packages.lock.json")]
    [InlineData("src/csharp/Example/packages.publish.lock.json")]
    public void PackageAndSdkIdentityComesFromTheCommit(string path)
    {
        using var repo = new Repository();
        repo.Write(path, "committed dependency identity\n");
        repo.Git("add", path);
        repo.Git("commit", "--quiet", "-m", "dependency identity");
        repo.Write(path, "uncommitted dependency identity\n");

        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);

        Assert.Equal("committed dependency identity\n", File.ReadAllText(Path.Combine(source.Root, path)));
    }

    [Fact]
    public void PreparedSourceSurvivesLaterCommitsAndWorkingTreeDeletion()
    {
        using var repo = new Repository();
        var source = CommittedControlSource.Prepare(repo.Root, repo.Destination);
        repo.Write("src/csharp/Program.cs", "new commit\n");
        repo.Git("add", "src/csharp/Program.cs");
        repo.Git("commit", "--quiet", "-m", "later change");
        File.Delete(Path.Combine(repo.Root, "src/csharp/Program.cs"));

        Assert.Equal(repo.Commit, source.Commit);
        Assert.NotEqual(repo.Git("rev-parse", "HEAD").Trim(), source.Commit);
        Assert.Equal("committed code\n", File.ReadAllText(Path.Combine(source.Root, "src/csharp/Program.cs")));
    }

    [Fact]
    public void ArchiveAttributesCannotSilentlyOmitDeclaredSource()
    {
        using var repo = new Repository();
        repo.Write(".gitattributes", "src/csharp/Program.cs export-ignore\n");
        repo.Git("add", ".gitattributes");
        repo.Git("commit", "--quiet", "-m", "exclude source");
        var error = Assert.Throws<InvalidOperationException>(() =>
            CommittedControlSource.Prepare(repo.Root, repo.Destination));
        Assert.Contains("src/csharp/Program.cs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolicSourceCannotBorrowMutableBytesOutsideTheCommit()
    {
        using var repo = new Repository();
        File.CreateSymbolicLink(Path.Combine(repo.Root, "src/csharp/Linked.cs"), "Program.cs");
        repo.Git("add", "src/csharp/Linked.cs");
        repo.Git("commit", "--quiet", "-m", "source link");
        var error = Assert.Throws<InvalidOperationException>(() =>
            CommittedControlSource.Prepare(repo.Root, repo.Destination));
        Assert.Contains("src/csharp/Linked.cs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGitIdentityIsARefusalNotACleanBuild()
    {
        using var repo = new Repository();
        var absent = Directory.CreateDirectory(Path.Combine(repo.Scratch, "not-a-repository")).FullName;
        Assert.Throws<InvalidOperationException>(() => CommittedControlSource.Prepare(absent, repo.Destination));
    }

    [Theory]
    [InlineData("Fern.Run.Cli")]
    [InlineData("Fern.Registry.Cli")]
    public void CommittedBuildMetadataNamesTheSelectedCommitAndArchive(string project)
    {
        var commit = new string('a', 40);
        var archive = new string('b', 64);
        var (exit, output, error) = BuildMetadata(project, commit, archive);
        Assert.True(exit == 0, error);
        using var json = JsonDocument.Parse(output);
        var metadata = json.RootElement.GetProperty("Items").GetProperty("AssemblyMetadata")
            .EnumerateArray().ToDictionary(item => item.GetProperty("Identity").GetString()!,
                item => item.GetProperty("Value").GetString());
        Assert.Equal(commit, metadata["FernBuiltFromCommit"]);
        Assert.Equal("0", metadata["FernBuiltFromDirtyTree"]);
        Assert.Equal(archive, metadata["FernCommittedSourceArchiveSha256"]);
    }

    [Theory]
    [InlineData("Fern.Run.Cli")]
    [InlineData("Fern.Registry.Cli")]
    public void ACommitWithoutAnArchiveCannotClaimCommittedBuildMetadata(string project)
    {
        var (exit, output, error) = BuildMetadata(project, new string('a', 40), "");
        Assert.NotEqual(0, exit);
        Assert.Contains("Committed source requires", output + error, StringComparison.Ordinal);
    }

    // The worktree path is a second source of bytes with the same declared
    // rules. These prove the rules the committed path enforced by construction
    // still hold when the bytes come from the filesystem instead of Git.

    [Fact]
    public void WorktreeSourceRefusesASymlinkedAncestorDirectory()
    {
        using var repo = new Repository();
        var outside = Directory.CreateDirectory(Path.Combine(repo.Scratch, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "Program.cs"), "bytes nobody reviewed\n");
        Directory.Delete(Path.Combine(repo.Root, "src", "csharp"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(repo.Root, "src", "csharp"), outside);

        var error = Assert.Throws<InvalidOperationException>(
            () => CommittedControlSource.PrepareFromWorktree(repo.Root, repo.Destination));

        // FileInfo.LinkTarget describes the leaf only; without the ancestor
        // walk File.Copy follows this link and installs foreign bytes.
        Assert.Contains("is reached through", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorktreeSourceRefusesASymlinkedWorkspaceRoot()
    {
        using var repo = new Repository();
        var link = Path.Combine(repo.Scratch, "linked-root");
        Directory.CreateSymbolicLink(link, repo.Root);

        var error = Assert.Throws<InvalidOperationException>(
            () => CommittedControlSource.PrepareFromWorktree(link, repo.Destination));

        // Path.GetFullPath is lexical and resolves no links, so the root is
        // the one ancestor a per-component walk never reaches on its own.
        Assert.Contains("is a link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorktreeSourceRefusesATrackedEntryThatIsNotARegularFile()
    {
        using var repo = new Repository();
        var target = Path.Combine(repo.Root, "src", "csharp", "Program.cs");
        File.Delete(target);
        File.CreateSymbolicLink(target, Path.Combine(repo.Root, "work.toml"));
        repo.Git("add", "src/csharp/Program.cs");

        var error = Assert.Throws<InvalidOperationException>(
            () => CommittedControlSource.PrepareFromWorktree(repo.Root, repo.Destination));

        // Git mode 120000. The committed path refused this by reading the mode;
        // so does this one, rather than inferring it from the filesystem.
        Assert.Contains("Git mode 120000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorktreeSourceRecordsWhichDeclaredPathsDifferFromTheCommit()
    {
        using var repo = new Repository();
        repo.Write("src/csharp/Program.cs", "edited after the commit\n");
        repo.Write("src/csharp/Added.cs", "never committed\n");

        var source = CommittedControlSource.PrepareFromWorktree(repo.Root, repo.Destination);

        Assert.True(source.Dirty);
        Assert.Equal(repo.Commit, source.Commit);
        // One boolean cannot tell an edited comment from a half-written file.
        Assert.Equal(
            new[] { "src/csharp/Added.cs", "src/csharp/Program.cs" },
            source.Differences.Select(difference => difference.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.All(source.Differences, difference => Assert.Equal(64, difference.Sha256.Length));
        Assert.DoesNotContain(source.Differences, difference => difference.Path == "Directory.Build.props");
    }

    [Fact]
    public void WorktreeSourceIgnoresAChangeOutsideItsDeclaredBuildSources()
    {
        using var repo = new Repository();
        repo.Write("results/run.log", "a training run appending while we copy\n");
        repo.Write("docs/rules/naming.md", "another lane editing prose\n");

        var source = CommittedControlSource.PrepareFromWorktree(repo.Root, repo.Destination);

        // The observation window is scoped to build sources. Unfiltered, every
        // GPU run on this machine would abort an installation.
        Assert.True(source.Dirty);
        Assert.DoesNotContain(source.Differences, difference => difference.Path.StartsWith("results/", StringComparison.Ordinal));
    }

    static (int Exit, string Output, string Error) BuildMetadata(string project, string commit, string archive)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestRegistry.RepoRoot(), RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        // Evaluate just the real stamping target, not a compiler or installer.
        foreach (var argument in new[] {
            "msbuild", $"src/csharp/{project}/{project}.csproj", "--nologo",
            "-target:FernStampBuildIdentity", "-getItem:AssemblyMetadata",
            "-p:FernCommittedSourceRevision=" + commit,
            "-p:FernCommittedSourceArchiveSha256=" + archive,
        }) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }

    sealed class Repository : IDisposable
    {
        public string Scratch { get; } = Directory.CreateTempSubdirectory("fern-committed-source-test-").FullName;
        public string Root { get; }
        public string Destination { get; }
        public string Commit { get; }

        public Repository()
        {
            Root = Directory.CreateDirectory(Path.Combine(Scratch, "repository")).FullName;
            Destination = Path.Combine(Scratch, "build");
            Git("init", "--quiet");
            Git("config", "user.name", "source test");
            Git("config", "user.email", "source-test@example.invalid");
            Write("src/csharp/Program.cs", "committed code\n");
            Write("src/csharp/Migrations/up.sql", "committed schema\n");
            Write("Directory.Build.props", "<Project />\n");
            Write("docs/rules/naming.md", "committed rule\n");
            Write("work.toml", "committed direction\n");
            Git("add", ".");
            Git("commit", "--quiet", "-m", "initial source");
            Commit = Git("rev-parse", "HEAD").Trim();
        }

        public void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(10_000));
            Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
            return output.GetAwaiter().GetResult();
        }

        public void Dispose() => Directory.Delete(Scratch, recursive: true);
    }
}
