using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Registering the source a study runs: the commit its code was at, and the
/// declaration of every file that code is.
///
/// These are the claims the frozen source used to make by copying the tree into
/// a content store (owner's cut, 2026-09-13). They are made here against git,
/// which already holds those bytes, and one of them is new: a declared file
/// that differs from the commit is refused, where the freeze would have
/// snapshotted it silently.
/// </summary>
public sealed class StudySourceRegistrationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("fern-source-registration-").FullName;

    string Workspace => Path.Combine(root, "workspace");

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A repository carrying every file a runtime source is required to declare.</summary>
    string Repository()
    {
        Directory.CreateDirectory(Workspace);
        TestRegistry.Git(Workspace, "init", "--quiet");
        TestRegistry.Git(Workspace, "config", "user.email", "test@example.invalid");
        TestRegistry.Git(Workspace, "config", "user.name", "registration test");
        foreach (var path in StudySourceRegistration.RequiredRuntimeFiles)
        {
            Write(path, $"# {path}\n");
        }
        Write("python/fern_train/study.py", "# the study compiler\n");
        Write("docs/not-source.md", "# not a runtime source file\n");
        TestRegistry.Git(Workspace, "add", "--all");
        TestRegistry.Git(Workspace, "commit", "--quiet", "-m", "the source");
        return Workspace;
    }

    void Write(string path, string contents)
    {
        var full = Path.Combine(Workspace, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    static RegisteredStudySource Register(string workspace, bool fromWorktree = false)
    {
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        return StudySourceRegistration.Register(writer, workspace, declaredStudyPath: null, fromWorktree);
    }

    [Fact]
    public void TheDeclarationIsTheRuntimeFilesOfTheCommitAndNothingElse()
    {
        var workspace = Repository();

        var registered = Register(workspace);

        Assert.Equal(
            TestRegistry.Git(workspace, "rev-parse", "--verify", "HEAD^{commit}").Trim(),
            registered.CommitSha);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        var declared = Declaration(workspace);
        Assert.Contains("python/fern_train/worker.py", declared);
        Assert.Contains("python/fern_train/study.py", declared);
        // A file the runtime does not read is not part of the source, however
        // tracked it is.
        Assert.DoesNotContain("docs/not-source.md", declared);
    }

    [Fact]
    public void TheSameTreeRegistersTheSameDeclarationAndTheSameDigest()
    {
        var workspace = Repository();

        var first = Register(workspace);
        var second = Register(workspace);

        // CHG 2026-09-26 (cards-without-hashes, schema 21): no digest; the same tree is the
        // same commit, the same declaration and the same registered_from.
        Assert.Equal(first.CommitSha, second.CommitSha);
        Assert.Equal(first.FileCount, second.FileCount);
        Assert.Equal(RegistryOperations.RegisteredFromCommit, first.RegisteredFrom);
        Assert.Equal(first.RegisteredFrom, second.RegisteredFrom);
    }

    /// <summary>
    /// The claim the freeze could not make. It copied whatever was on disk, so a
    /// run could be registered at a commit that does not carry its code and
    /// nothing said so.
    /// </summary>
    [Fact]
    public void ADeclaredFileThatDiffersFromTheCommitIsRefusedByName()
    {
        var workspace = Repository();
        Write("python/fern_train/study.py", "# edited, not committed\n");

        var refusal = Assert.Throws<StudySourceRegistration.Refusal>(() => Register(workspace));

        Assert.Contains("python/fern_train/study.py", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--from-worktree", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A compiled study that names the files its cells load, and no data inputs.</summary>
    string CompiledStudy(params string[] cellCodeFiles)
    {
        var path = Path.Combine(root, "compiled.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema = "fern.compiled-study.v1",
            dataset = new { inputs = new { } },
            cell_code_files = cellCodeFiles,
        }));
        return path;
    }

    static RegisteredStudySource RegisterStudy(string workspace, string compiled)
    {
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        return StudySourceRegistration.Register(writer, workspace, compiled, fromWorktree: false);
    }

    /// <summary>
    /// CHG 2026-09-27 (owner: "这一类必须只关注核心file 不能树一动就拒绝", only a study's core
    /// files; refusing whenever anything in the tree moved was silly): an uncommitted edit to
    /// a declared file the study's cells do not load registers at the commit, which is the
    /// code a cell runs, and is named; the same edit to a file they load is refused.
    /// </summary>
    [Fact]
    public void OnlyAnEditToAFileTheStudysCellsLoadRefusesTheRegistration()
    {
        var workspace = Repository();
        Write("python/fern_train/study.py", "# edited, not committed\n");
        var compiled = CompiledStudy("python/fern_train/worker.py", "pyproject.toml");

        var registered = RegisterStudy(workspace, compiled);

        Assert.Equal(RegistryOperations.RegisteredFromCommit, registered.RegisteredFrom);
        Assert.Equal(["python/fern_train/study.py"], registered.LeftAtCommit);

        Write("python/fern_train/worker.py", "# the worker, edited, not committed\n");
        var refusal = Assert.Throws<StudySourceRegistration.Refusal>(() => RegisterStudy(workspace, compiled));
        Assert.Contains("of this study's core file(s) differ", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("python/fern_train/worker.py", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("python/fern_train/study.py", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-27: a file the study's cells load that no commit carries would be missing
    /// from the code a cell runs, so it is refused by name; an untracked file they do not
    /// load is not looked at.
    /// </summary>
    [Fact]
    public void AFileTheStudysCellsLoadThatNoCommitCarriesIsRefused()
    {
        var workspace = Repository();
        Write("python/fern_train/new_module.py", "# imported by the worker, never committed\n");
        Write("python/fern_train/unrelated.py", "# another lane's new file\n");

        var refusal = Assert.Throws<StudySourceRegistration.Refusal>(() =>
            RegisterStudy(workspace, CompiledStudy("python/fern_train/worker.py", "python/fern_train/new_module.py")));

        Assert.Contains("python/fern_train/new_module.py", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("in no commit", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated.py", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(RegisterStudy(workspace, CompiledStudy("python/fern_train/worker.py")).LeftAtCommit);
    }

    [Fact]
    public void TheOverrideRegistersTheWorkingTreesBytesAndSaysSoInTheDigest()
    {
        var workspace = Repository();
        var committed = Register(workspace);
        Write("python/fern_train/study.py", "# edited, not committed\n");

        var edited = Register(workspace, fromWorktree: true);

        Assert.Equal(committed.CommitSha, edited.CommitSha);
        // The digest says WHICH it is, not only that it differs: bytes no
        // commit carries live on one machine, and a reader of the row should
        // see that without asking git.
        // CHG 2026-09-26 (schema 21): registered_from says it in words, and the two are
        // two sources.
        Assert.Equal(RegistryOperations.RegisteredFromCommit, committed.RegisteredFrom);
        Assert.Equal(RegistryOperations.RegisteredFromWorkingTree, edited.RegisteredFrom);
    }

    /// <summary>
    /// The claim the whole change rests on, and the one nothing was making: what
    /// registration writes, the install reads back and materializes. The two
    /// halves met only in a test that trains on a GPU, so a declaration the
    /// installer refuses -- a digest without its label, say -- could be written
    /// by every registration and noticed by nothing.
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the install is
    /// retired; a commit-mode cell's code is what reads the declaration back now.
    /// </summary>
    [Fact]
    public void WhatRegistrationDeclaresACommitCellWritesBack()
    {
        var workspace = Repository();
        var registryPath = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(registryPath);
        var registered = StudySourceRegistration.Register(writer, workspace, declaredStudyPath: null, fromWorktree: false);

        using (var seed = writer.Connection.CreateCommand())
        {
            seed.CommandText =
                """
                INSERT INTO sites (id, name, capabilities_json, capacity_json, created_at)
                  VALUES (1, 'local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-13T00:00:00Z');
                INSERT INTO studies (id, name, revision, compiled_study_json, source_id, requested_state, supervisor_epoch, created_at)
                  VALUES (1, 'registered-source-test', 1, '{}', $source, 'run', 0, '2026-09-13T00:00:00Z');
                """;
            seed.Parameters.AddWithValue("$source", registered.SourceId);
            seed.ExecuteNonQuery();
        }

        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the install
        // is retired; what reads a registration back now is a commit-mode cell's
        // code, written by CellCode from the same declaration.
        var source = RegistryOperations.ReadSource(writer.Connection, registered.SourceId)!;
        var directory = Directory.CreateTempSubdirectory("fern-registered-code-").FullName;
        var declared = CellCode.ReadDeclaration(source.DeclaredFilesJson, source.SourceId);
        // CHG 2026-09-26 (cards-without-hashes): no digest flag; files the commit holds come from it.
        var fromWorkingTree = CellCode.WriteDeclaredFiles(directory, workspace, source.SourceId, source.CommitSha, declared);

        Assert.Empty(fromWorkingTree);
        Assert.Equal(registered.FileCount, declared.Count);
        foreach (var file in declared)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(workspace, file.Path)),
                File.ReadAllBytes(Path.Combine(directory, file.Path)));
        }
    }

    /// <summary>
    /// A symbolic link is not a source file. git hands back its target's PATH as
    /// the blob's content, so a declaration that called it a regular file would
    /// install a .py holding the text "../shared/thing.py" and the worker would
    /// fail at train time with a syntax error instead of here with a sentence.
    /// </summary>
    [Fact]
    public void ASymbolicLinkIsDeclaredAsOneAndRefusedAtInstall()
    {
        var workspace = Repository();
        Directory.CreateDirectory(Path.Combine(workspace, "other_models", "shared"));
        File.WriteAllText(Path.Combine(workspace, "other_models", "shared", "arch.py"), "# the real one\n");
        File.CreateSymbolicLink(
            Path.Combine(workspace, "other_models", "linked_arch.py"),
            Path.Combine("shared", "arch.py"));
        TestRegistry.Git(workspace, "add", "--all");
        TestRegistry.Git(workspace, "commit", "--quiet", "-m", "a link joins the source");

        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        StudySourceRegistration.Register(writer, workspace, declaredStudyPath: null, fromWorktree: false);

        using var read = writer.Connection.CreateCommand();
        read.CommandText = "SELECT declared_files_json FROM sources ORDER BY id DESC LIMIT 1";
        var declared = (string)read.ExecuteScalar()!;
        using var document = JsonDocument.Parse(declared);
        var link = document.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("path").GetString() == "other_models/linked_arch.py");
        Assert.Equal("symbolic_link", link.GetProperty("kind").GetString());
    }

    /// <summary>
    /// An untracked runtime file is a preparation error, never implicit
    /// permission to adopt another lane's new code.
    /// CHG 2026-09-22: an undeclared file is not evidence of a dependency;
    /// ignore it without adopting it or refusing the declared source.
    /// </summary>
    [Fact]
    public void UndeclaredRuntimeFilesDoNotChangeOrBlockTheDeclaredSource()
    {
        var workspace = Repository();
        var before = Register(workspace);
        Write("python/fern_train/new_lane.py", "# another lane's new module\n");

        var after = Register(workspace);

        // CHG 2026-09-26 (schema 21): the same source is the same registered_from and file count.
        Assert.Equal(before.RegisteredFrom, after.RegisteredFrom);
        Assert.Equal(before.FileCount, after.FileCount);
        Assert.DoesNotContain("new_lane.py", Declaration(workspace), StringComparison.Ordinal);
        Write("python/fern_train/new_lane.py", "# unrelated bytes changed again\n");
        Assert.Equal(before.RegisteredFrom, Register(workspace, fromWorktree: true).RegisteredFrom);
    }

    [Fact]
    public void ASourceMissingAFileTheRuntimeRequiresIsRefusedByName()
    {
        var workspace = Repository();
        TestRegistry.Git(workspace, "rm", "--quiet", "--", "uv.lock");
        TestRegistry.Git(workspace, "commit", "--quiet", "-m", "the lock leaves");

        var refusal = Assert.Throws<StudySourceRegistration.Refusal>(() => Register(workspace));

        Assert.Contains("uv.lock", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNewRuntimeSourceIsIncludedAndMissingOrEscapingPathsAreRefused()
    {
        var workspace = Repository();
        var before = Register(workspace);
        const string declared = "python/fern_train/declared_module.py";
        Write(declared, "# this module is explicitly selected\n");
        Write("python/fern_train/unrelated.py", "# not selected\n");
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        var after = StudySourceRegistration.Register(writer, workspace, null, true, [declared]);
        Assert.Equal(before.FileCount + 1, after.FileCount);
        // CHG 2026-09-26 (schema 21): a file declared with --source-file makes it a working-tree source.
        Assert.Equal(RegistryOperations.RegisteredFromWorkingTree, after.RegisteredFrom);
        Assert.Throws<StudySourceRegistration.Refusal>(() =>
            StudySourceRegistration.Register(writer, workspace, null, false, [declared]));
        Assert.Throws<StudySourceRegistration.Refusal>(() =>
            StudySourceRegistration.Register(writer, workspace, null, true, ["../outside.py"]));
        File.Delete(Path.Combine(workspace, declared));
        Assert.Throws<StudySourceRegistration.Refusal>(() =>
            StudySourceRegistration.Register(writer, workspace, null, true, [declared]));
    }

    static string Declaration(string workspace)
    {
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        StudySourceRegistration.Register(writer, workspace, declaredStudyPath: null, fromWorktree: false);
        using var read = writer.Connection.CreateCommand();
        read.CommandText = "SELECT declared_files_json FROM sources ORDER BY id DESC LIMIT 1";
        return (string)read.ExecuteScalar()!;
    }
}
