using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Scheduler;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice 10 of the cell port: a member's identity is the digest of its own
/// inputs, so an install rebuilds only the members whose inputs changed.
/// The claim, falsified here on a fixture repository: the closure follows
/// project references and the shared props file's conditional imports; a
/// change under one project changes the digests of the members that
/// reference it and no other; a root build input changes every C# member; a
/// published copy is found by its input digest only when its receipt names
/// a committed build and its bytes still match.
/// </summary>
public sealed class ControlMembersTests
{
    static readonly ControlMember App = new("app", ControlMemberKind.Dotnet, "src/csharp/App/App.csproj", "app-binaries", Trim: false);
    static readonly ControlMember Tool = new("tool", ControlMemberKind.Dotnet, "src/csharp/Tool/Tool.csproj", "tool-binaries", Trim: false);
    static readonly ControlMember Compiler = new("compiler", ControlMemberKind.Gleam, "tools/fern-compile-gleam", "compiler-binaries", Trim: false);

    /// <summary>
    /// 2026-09-26, after the tree moved to src/csharp/: `publish-binary
    /// --member fern-run` refused "committed installation source contains no
    /// C# files" when run by a binary built before the move. On this
    /// repository's own HEAD the committed source carries C# files under
    /// src/csharp/ and nothing under csharp/, and every member's closure
    /// holds its own project (a Gleam member, its project directory).
    /// </summary>
    [Fact]
    public void EveryMembersProjectIsInTheCommittedSourceOfThisRepositorysHead()
    {
        var scratch = Directory.CreateTempSubdirectory("fern-control-source-").FullName;
        try
        {
            var source = CommittedControlSource.Prepare(TestRegistry.RepoRoot(), Path.Combine(scratch, "source"));
            Assert.Contains(source.Files.Keys, path => path.StartsWith("src/csharp/", StringComparison.Ordinal));
            Assert.DoesNotContain(source.Files.Keys, path => path.StartsWith("csharp/", StringComparison.Ordinal));
            foreach (var member in ControlMembers.All)
            {
                var closure = ControlMembers.InputClosure(source, member);
                if (member.Kind == ControlMemberKind.Dotnet)
                {
                    Assert.StartsWith("src/csharp/", member.Project, StringComparison.Ordinal);
                    Assert.Contains(member.Project, closure);
                }
                else
                {
                    Assert.Contains(closure, path => path.StartsWith(member.Project + "/", StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void TheClosureFollowsProjectReferencesAndTheSharedPropsFilesConditionalImports()
    {
        using var repository = new Repository();
        var source = repository.Prepare();

        // CHG 2026-09-26: the closure is sorted ordinally, and with the C#
        // tree under src/ the document and the SDK pin now sort first.
        // CHG 2026-09-26 (later): the declared AdditionalFile is a file under
        // src/csharp/Shared/, no longer docs/how-to/pin.md: the workflow
        // documents stopped being build inputs when the workflow-contract
        // analyzer was removed. It is an input by name, which is what the
        // closure must still follow.
        Assert.Equal(new[]
        {
            "global.json",
            "src/csharp/Analyzer/Analyzer.csproj",
            "src/csharp/Analyzer/Generator.cs",
            "src/csharp/App/App.csproj",
            "src/csharp/App/Program.cs",
            "src/csharp/Directory.Build.props",
            "src/csharp/Lib/Lib.cs",
            "src/csharp/Lib/Lib.csproj",
            "src/csharp/Shared/words.txt",
        }, ControlMembers.InputClosure(source, App));

        // Tool references nothing and the props file names only App, so
        // neither Lib, the analyzer nor the declared file is Tool's input.
        Assert.Equal(new[]
        {
            "global.json",
            "src/csharp/Directory.Build.props",
            "src/csharp/Tool/Program.cs",
            "src/csharp/Tool/Tool.csproj",
        }, ControlMembers.InputClosure(source, Tool));
        Assert.DoesNotContain("src/csharp/Fern.sln", ControlMembers.InputClosure(source, App));

        Assert.Equal(new[]
        {
            "tools/fern-compile-gleam/gleam.toml",
            "tools/fern-compile-gleam/src/fern_compile.gleam",
        }, ControlMembers.InputClosure(source, Compiler));
    }

    [Fact]
    public void ADigestChangesForTheMembersWhoseInputsChangedAndForNoOther()
    {
        using var repository = new Repository();
        var before = repository.Prepare();
        var app = ControlMembers.InputDigest(before, App, "dotnet 11.0.100");
        var tool = ControlMembers.InputDigest(before, Tool, "dotnet 11.0.100");
        var compiler = ControlMembers.InputDigest(before, Compiler, "go version go1.25.1 linux/amd64");
        Assert.StartsWith("sha256:", app, StringComparison.Ordinal);
        Assert.Equal(app, ControlMembers.InputDigest(repository.Prepare(), App, "dotnet 11.0.100"));

        repository.Write("src/csharp/Lib/Lib.cs", "public static class Lib { public const int Version = 2; }\n");
        repository.Commit("a library change");
        var afterLib = repository.Prepare();
        Assert.NotEqual(app, ControlMembers.InputDigest(afterLib, App, "dotnet 11.0.100"));
        Assert.Equal(tool, ControlMembers.InputDigest(afterLib, Tool, "dotnet 11.0.100"));
        Assert.Equal(compiler, ControlMembers.InputDigest(afterLib, Compiler, "go version go1.25.1 linux/amd64"));

        repository.Write("global.json", "{ \"sdk\": { \"version\": \"11.0.101\" } }\n");
        repository.Commit("a root build input change");
        var afterRoot = repository.Prepare();
        Assert.NotEqual(tool, ControlMembers.InputDigest(afterRoot, Tool, "dotnet 11.0.100"));
        Assert.Equal(compiler, ControlMembers.InputDigest(afterRoot, Compiler, "go version go1.25.1 linux/amd64"));

        // The same inputs under another compiler are other bytes.
        Assert.NotEqual(
            ControlMembers.InputDigest(afterRoot, Tool, "dotnet 11.0.100"),
            ControlMembers.InputDigest(afterRoot, Tool, "dotnet 11.0.101"));
    }

    [Fact]
    public void AMemberReferencingAProjectTheSourceDoesNotCarryIsRefusedByName()
    {
        using var repository = new Repository();
        repository.Write("src/csharp/App/App.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"..\\Missing\\Missing.csproj\" /></ItemGroup></Project>\n");
        repository.Commit("a dangling reference");
        var source = repository.Prepare();
        var refused = Assert.Throws<InvalidOperationException>(() => ControlMembers.InputClosure(source, App));
        Assert.Contains("src/csharp/Missing/Missing.csproj", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APublishedCopyIsFoundByItsInputDigestOnlyFromACommittedBuildWithItsBytesIntact()
    {
        using var repository = new Repository();
        var source = repository.Prepare();
        var deployment = Path.Combine(repository.Scratch, "deploy");
        var directory = Directory.CreateDirectory(Path.Combine(deployment, App.PublishDirectory)).FullName;
        var digest = ControlMembers.InputDigest(source, App, "dotnet 11.0.100");
        Assert.Null(ControlMemberPublications.Find(deployment, App, digest));

        var built = Path.Combine(repository.Scratch, "app");
        File.WriteAllText(built, "#!/bin/sh\nexit 0\n");
        var (sha256, published, error) = DurableFiles.PublishBinary(built, directory, App.PublishPrefix);
        Assert.Null(error);
        Assert.True(ControlMemberPublications.WriteReceipt(published!, App, sha256!, digest, source, "dotnet 11.0.100", "test"));
        Assert.False(ControlMemberPublications.WriteReceipt(published!, App, sha256!, digest, source, "dotnet 11.0.100", "test"));

        var receipt = JsonNode.Parse(File.ReadAllText(ControlMemberPublications.ReceiptPath(published!))) as JsonObject;
        Assert.NotNull(receipt);
        Assert.Equal("fern.control-member-publish.v1", receipt!["schema"]!.GetValue<string>());
        Assert.Equal("app", receipt["member"]!.GetValue<string>());
        Assert.Equal(digest, receipt["input_digest"]!.GetValue<string>());
        Assert.Equal(source.Commit, receipt["built_from_commit"]!.GetValue<string>());
        Assert.False(receipt["built_from_dirty_tree"]!.GetValue<bool>());
        Assert.Equal(source.ArchiveSha256, receipt["source_archive_sha256"]!.GetValue<string>());
        Assert.StartsWith("publish --disable-build-servers", receipt["recipe"]!.GetValue<string>(), StringComparison.Ordinal);

        var found = ControlMemberPublications.Find(deployment, App, digest);
        Assert.NotNull(found);
        Assert.Equal(published, found!.Value.Path);
        Assert.Equal(sha256, found.Value.Sha256);
        Assert.Null(ControlMemberPublications.Find(deployment, App, "sha256:" + new string('0', 64)));
        Assert.Null(ControlMemberPublications.Find(deployment, Tool, digest));

        // A receipt from a worktree build names bytes nobody can rebuild from
        // a commit; it is never the answer.
        var dirtyBinary = Path.Combine(repository.Scratch, "app-dirty");
        File.WriteAllText(dirtyBinary, "#!/bin/sh\nexit 1\n");
        var (dirtySha, dirtyPublished, _) = DurableFiles.PublishBinary(dirtyBinary, directory, App.PublishPrefix);
        var dirtySource = source with { Dirty = true };
        var dirtyDigest = "sha256:" + new string('d', 64);
        Assert.True(ControlMemberPublications.WriteReceipt(dirtyPublished!, App, dirtySha!, dirtyDigest, dirtySource, "dotnet 11.0.100", "test"));
        Assert.Null(ControlMemberPublications.Find(deployment, App, dirtyDigest));

        // Bytes that no longer hash to the receipt are not the published copy.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(published!, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(published!, "#!/bin/sh\nexit 3\n");
        Assert.Null(ControlMemberPublications.Find(deployment, App, digest));
    }

    [Fact]
    public void ADocumentIsNeverABuildInputAndADeclaredFileIs()
    {
        // CHG 2026-09-26: docs/how-to/ holds procedures beside the workflow pins
        // since the documentation merge; only a declared AdditionalFile is an
        // input, so an edit to another document there does not refuse an install.
        // CHG 2026-09-26 (later): was AnUndeclaredDocumentBesideAWorkflowPinIsNotABuildInput.
        // The workflow pins and the analyzer that read them are gone, so no
        // document is an input at all, edited, untracked or once declared; the
        // file a project declares by name, and an untracked file in a project's
        // own directory, still are.
        using var repository = new Repository();
        var source = repository.Prepare();
        repository.Write("docs/how-to/other.md", "# edited, not an input of App\n");
        repository.Write("docs/how-to/new-procedure.md", "# untracked\n");
        repository.Write("docs/how-to/study-run.md", "# the workflow document, edited\n");
        Assert.Empty(CommittedControlSource.DifferingInputs(repository.Root, source, App));
        repository.Write("src/csharp/Shared/words.txt", "edited\n");
        Assert.Equal(new[] { "src/csharp/Shared/words.txt" }, CommittedControlSource.DifferingInputs(repository.Root, source, App));
        repository.Write("src/csharp/App/New.cs", "// untracked source\n");
        Assert.Contains("src/csharp/App/New.cs", CommittedControlSource.DifferingInputs(repository.Root, source, App));
    }

    [Fact]
    public void AFileDeclaredOutsideTheBuildRootsIsRefusedByName()
    {
        // The installer archives only src/csharp/ and tools/fern-compile-gleam/ (and
        // the root build files). A declaration elsewhere used to be dropped from
        // the archive and the closure without a word while the build still read
        // it from the working tree; it is refused, naming the project and path.
        using var repository = new Repository();
        repository.Write("config/x.json", "{}\n");
        repository.Write("src/csharp/Directory.Build.props",
            "<Project>\n"
            + "  <ItemGroup Condition=\"'$(MSBuildProjectName)' == 'App'\">\n"
            + "    <AdditionalFiles Include=\"$(MSBuildThisFileDirectory)../../config/x.json\" />\n"
            + "  </ItemGroup>\n"
            + "</Project>\n");
        repository.Commit("a props file declares a file outside the build roots");
        var shared = Assert.Throws<InvalidOperationException>(() => ControlMembers.InputClosure(repository.Prepare(), App));
        Assert.Contains("project App declares config/x.json", shared.Message, StringComparison.Ordinal);
        Assert.Contains("a build input must live under src/csharp/ or tools/fern-compile-gleam/", shared.Message, StringComparison.Ordinal);

        // The same refusal when the project file itself declares it.
        repository.Write("src/csharp/Directory.Build.props", "<Project />\n");
        repository.Write("src/csharp/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
            + "    <ProjectReference Include=\"..\\Lib\\Lib.csproj\" />\n"
            + "    <AdditionalFiles Include=\"..\\..\\..\\config\\x.json\" />\n"
            + "  </ItemGroup>\n</Project>\n");
        repository.Commit("the project file declares it");
        var own = Assert.Throws<InvalidOperationException>(() => ControlMembers.InputClosure(repository.Prepare(), App));
        Assert.Contains("project App declares config/x.json (in src/csharp/App/App.csproj)", own.Message, StringComparison.Ordinal);
        Assert.Contains("a build input must live under src/csharp/ or tools/fern-compile-gleam/", own.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecipeIsTheBuildsFixedArgumentsAndTheGleamExport()
    {
        var recipe = ControlMembers.Recipe(ControlMembers.Named("fern-run"));
        Assert.Equal("publish", recipe[0]);
        Assert.Contains("-p:PublishSingleFile=true", recipe);
        Assert.DoesNotContain("-p:PublishTrimmed=true", recipe);
        // CHG 2026-09-27: the compiler is the Gleam program, exported and packed.
        var gleam = ControlMembers.Recipe(ControlMembers.Named("fern-compile"));
        Assert.Equal(new[] { "gleam export erlang-shipment", "gleam run -m export_executable" }, gleam);
        // CHG 2026-09-14: four, not five. fern-stage-runner left with the
        // Rust-era stage contracts its two verbs answered; fern-registry is
        // published by its own verb and chosen by select-runtime, so it shares
        // the members' input identity and reuse but not the set.
        Assert.Equal(4, ControlMembers.InstalledSet.Count);
        Assert.DoesNotContain(ControlMembers.InstalledSet, member => member.Name == "fern-registry");
        Assert.DoesNotContain(ControlMembers.All, member => member.Name == "fern-stage-runner");
        var unknown = Assert.Throws<InvalidOperationException>(() => ControlMembers.Named("fern-nothing"));
        Assert.Contains("fern-site, fern-experiment, fern-run, fern-compile, fern-registry", unknown.Message, StringComparison.Ordinal);
    }

    sealed class Repository : IDisposable
    {
        public string Scratch { get; } = Directory.CreateTempSubdirectory("fern-control-members-test-").FullName;
        public string Root { get; }
        int prepared;

        public Repository()
        {
            Root = Directory.CreateDirectory(Path.Combine(Scratch, "repository")).FullName;
            Git("init", "--quiet");
            Git("config", "user.name", "members test");
            Git("config", "user.email", "members-test@example.invalid");
            Write("global.json", "{ \"sdk\": { \"version\": \"11.0.100\" } }\n");
            Write("src/csharp/Directory.Build.props",
                "<Project>\n"
                + "  <ItemGroup Condition=\"'$(MSBuildProjectName)' == 'App'\">\n"
                + "    <ProjectReference Include=\"$(MSBuildThisFileDirectory)Analyzer/Analyzer.csproj\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />\n"
                + "    <AdditionalFiles Include=\"$(MSBuildThisFileDirectory)Shared/words.txt\" />\n"
                + "  </ItemGroup>\n"
                + "</Project>\n");
            Write("src/csharp/App/App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Lib\\Lib.csproj\" />\n  </ItemGroup>\n</Project>\n");
            Write("src/csharp/App/Program.cs", "System.Console.WriteLine(Lib.Version);\n");
            Write("src/csharp/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            Write("src/csharp/Lib/Lib.cs", "public static class Lib { public const int Version = 1; }\n");
            Write("src/csharp/Analyzer/Analyzer.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            Write("src/csharp/Analyzer/Generator.cs", "// generates\n");
            Write("src/csharp/Tool/Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            Write("src/csharp/Tool/Program.cs", "System.Console.WriteLine(\"tool\");\n");
            Write("src/csharp/Shared/words.txt", "words an analyzer reads\n");
            Write("docs/how-to/study-run.md", "# a workflow document\n");
            Write("docs/how-to/other.md", "# not an input of App\n");
            Write("tools/fern-compile-gleam/gleam.toml", "name = \"fern_compile\"\n");
            Write("tools/fern-compile-gleam/src/fern_compile.gleam", "pub fn main() { Nil }\n");
            Write("tools/fern-compile-gleam/test/case_test.gleam", "pub fn case_test() { Nil }\n");
            Write("README.md", "not a build input\n");
            // Sits directly under csharp/ and is NOT an input: `dotnet publish`
            // is given a project, and a lane adding a project to the solution
            // must not rebuild every member.
            Write("src/csharp/Fern.sln", "Microsoft Visual Studio Solution File\n");
            Commit("initial source");
        }

        public CommittedControlSource Prepare() =>
            CommittedControlSource.Prepare(Root, Path.Combine(Scratch, $"build-{++prepared}"));

        public void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Commit(string message)
        {
            Git("add", ".");
            Git("commit", "--quiet", "-m", message);
        }

        string Git(params string[] arguments)
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
