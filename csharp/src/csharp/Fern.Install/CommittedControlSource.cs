using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace Fern.Install;

/// <summary>
/// Installation source is a fixed commit's declared C# files, not the shared
/// worktree. This boundary owns Git selection and byte verification; InstallCli
/// owns the temporary directory's lifetime and the subsequent member builds.
/// </summary>
internal sealed record SourceDifference(string Path, long ByteCount, string Sha256);

/// <param name="Files">
/// CHG 2026-09-11 night (slice 10): every archived path with the Git blob id of
/// the bytes archived (for a worktree build, the blob id of the working file),
/// so a member's input digest can be taken from the same table the archive was
/// verified against, without a second walk of the tree.
/// </param>
internal sealed record CommittedControlSource(string Root, string Commit, string ArchivePath, string ArchiveSha256,
    bool Dirty, IReadOnlyList<SourceDifference> Differences, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>
    /// The C# tree's repository-relative prefix, with its trailing slash.
    /// CHG 2026-09-26: csharp/ moved to src/csharp/ (Fern.Registry.RepositoryLayout).
    /// </summary>
    internal const string CSharpPrefix = Fern.Registry.RepositoryLayout.CSharpDirectory + "/";

    /// <summary>
    /// The card compiler's project, the other root a build input may live in.
    /// A project that declares a file outside these two roots is refused by name
    /// (ControlMembers.RequireBuildRoot), because nothing else is archived.
    /// CHG 2026-09-27: the Gleam project; the Go module it replaced was
    /// tools/fern-compile/, now under retired/go/.
    /// </summary>
    internal const string CompilerPrefix = "tools/fern-compile-gleam/";

    public static CommittedControlSource Prepare(string workspace, string destination)
    {
        var commit = Git(workspace, ["rev-parse", "--verify", "HEAD^{commit}"]).Trim();
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("installation source has no valid committed Git identity");

        // Read one immutable tree, then pass its explicit regular-file list to
        // archive. Never archive a directory recursively or borrow a worktree
        // symlink, submodule, untracked input, bin/obj, dataset or result store.
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in Git(workspace, ["ls-tree", "-rz", "--full-tree", commit]).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t');
            if (separator < 0) throw new InvalidOperationException("Git returned an invalid source entry");
            var path = record[(separator + 1)..];
            if (!IsBuildSource(path)) continue;
            var identity = record[..separator].Split(' ');
            if (identity.Length != 3 || identity[1] != "blob" || identity[0] is not ("100644" or "100755")
                || path.Split('/').Any(part => part is "" or "." or "..") || path.Contains('\\'))
                throw new InvalidOperationException($"installation source {path} must be a regular committed file");
            files.Add(path, identity[2]);
        }
        if (!files.Keys.Any(path => path.StartsWith(CSharpPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("committed installation source contains no C# files");

        var root = Path.GetFullPath(destination);
        var archive = root + ".tar";
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(archive))
            throw new InvalidOperationException($"installation source destination {root} is not empty and owned by this operation");
        Directory.CreateDirectory(root);
        var arguments = new List<string> { "archive", "--format=tar", "--output=" + archive, commit, "--" };
        arguments.AddRange(files.Keys.Select(path => ":(top,literal)" + path));
        Git(workspace, arguments);

        // Git attributes can exclude or substitute an archived file. Compare
        // every extracted byte with its declared blob rather than treating a
        // successful archive command as proof of a complete source copy.
        var remaining = new HashSet<string>(files.Keys, StringComparer.Ordinal);
        using (var stream = File.OpenRead(archive))
        using (var reader = new TarReader(stream))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || !remaining.Remove(entry.Name) || (entry.Length > 0 && entry.DataStream is null))
                    throw new InvalidOperationException($"archive contains undeclared or non-regular source {entry.Name}");
                var path = Path.Combine(root, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = File.Create(path)) entry.DataStream?.CopyTo(output);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                hash.AppendData(Encoding.UTF8.GetBytes($"blob {entry.Length}\0"));
                using (var input = File.OpenRead(path))
                {
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = input.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, count));
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != files[entry.Name])
                    throw new InvalidOperationException($"archive source {entry.Name} differs from its committed Git blob");
            }
        }
        if (remaining.Count != 0)
            throw new InvalidOperationException($"archive omitted committed source {remaining.Order(StringComparer.Ordinal).First()}");
        using var archiveBytes = File.OpenRead(archive);
        return new CommittedControlSource(root, commit, archive, Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), false, [], files);
    }

    /// <summary>
    /// The build sources of ONE member that differ from HEAD, including
    /// untracked files inside its own project directories.
    ///
    /// Owner, 2026-09-18: "the file itself is clean then compile, never ever
    /// does it to the whole tree." A member is built from its project and the
    /// projects it references; an edit anywhere else changes none of its bytes.
    /// Refusing fern-run because another lane is mid-edit in Fern.Dash blocked
    /// an install that had nothing to do with that work, and the only way
    /// through stamped every binary "built from a dirty tree".
    /// </summary>
    public static IReadOnlyList<string> DifferingInputs(string workspace, CommittedControlSource committed, ControlMember member)
    {
        var closure = ControlMembers.InputClosure(committed, member);
        var files = new HashSet<string>(closure, StringComparer.Ordinal);
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in closure)
        {
            var cut = path.LastIndexOf('/');
            if (cut <= 0) continue;
            var directory = path[..(cut + 1)];
            // The root build inputs sit directly under csharp/ and at the
            // repository root; taking THEIR directory as a prefix would make
            // every file in the tree an input of every member, which is the
            // whole-tree rule this replaced. Only a project's own directory
            // catches the untracked file a commit cannot name.
            // CHG 2026-09-26: that directory is src/csharp/ now.
            if (directory == CSharpPrefix) continue;
            // CHG 2026-09-26 (after the workflow-contract analyzer was removed):
            // a rule stood here (1c7f6e7fa) that skipped the directory of any
            // closure file outside src/csharp/ and tools/fern-compile/, so an
            // untracked procedure beside the declared workflow document in
            // docs/how-to/ did not refuse an install. It is gone because it can
            // no longer act: the closure holds nothing outside those two roots,
            // since ControlMembers refuses such a declaration by name, and
            // IsBuildSource archives nothing there.
            directories.Add(directory);
        }
        var differing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in Git(workspace, ["status", "--porcelain=1", "-z", "--untracked-files=all"])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length <= 3) continue;
            // A path git calls deleted is not a source that would change a
            // build from committed bytes: the archive carries it. It is also
            // what a worktree assembled out of symlinks reports for every file
            // it did not link, which would otherwise refuse every member.
            if (record[0] == 'D' || record[1] == 'D') continue;
            var path = record[3..];
            if (!IsBuildSource(path)) continue;
            // A tracked input by name; an untracked file by the directory it
            // would be compiled from, since HEAD cannot name it.
            if (files.Contains(path) || directories.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                differing.Add(path);
        }
        return differing.ToList();
    }

    /// <summary>
    /// Any build source that differs from HEAD, including an untracked one.
    /// </summary>
    public static bool WorktreeDiffers(string workspace)
    {
        foreach (var record in Git(workspace, ["status", "--porcelain=1", "-z", "--untracked-files=all"])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length > 3 && IsBuildSource(record[3..])) return true;
        }
        return false;
    }

    /// <summary>
    /// The same declared file set, taken from the working tree rather than a
    /// commit. Selection, the portable-path rules and the refusal of anything
    /// that is not a regular file are unchanged; what changes is where the
    /// bytes come from and what the receipt then says, because Dirty travels
    /// with them and `fern-run version` prints "(dirty tree)".
    ///
    /// This exists because a control binary is a TOOL, not a scientific fact.
    /// A Cell's science is fixed by its frozen implementation source, which is
    /// content-addressed and verified separately. Refusing to install a tool
    /// because the tree is dirty protects no result and blocks every install,
    /// which is how one broken commit held three machines hostage.
    /// </summary>
    public static CommittedControlSource PrepareFromWorktree(string workspace, string destination)
    {
        var commit = Git(workspace, ["rev-parse", "--verify", "HEAD^{commit}"]).Trim();
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("installation source has no valid Git identity to record");

        var paths = DeclaredWorktreePaths(workspace);
        var root = Path.GetFullPath(destination);
        var archive = root + ".tar";
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(archive))
            throw new InvalidOperationException($"installation source destination {root} is not empty and owned by this operation");
        Directory.CreateDirectory(root);

        // AGENTS.md, "Source archives": read the identity-bearing views before
        // and after copying bytes and refuse the whole operation if any view
        // changed. Four lanes edit csharp/ at once here, so a file rewritten
        // mid-copy would otherwise enter a binary nobody wrote.
        var budget = Stopwatch.StartNew();
        var before = ObservedWorktree(workspace, paths);
        var differences = new List<SourceDifference>();
        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var origin = ResolveDeclaredFile(workspace, path);
            var copy = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            CopyDeclaredFile(origin, copy, path, budget);
            copied[path] = GitBlobSha1(copy);
        }
        // Content is the identity. A size-and-timestamp view is a stat view,
        // and a rewrite preserving both is invisible to it AND to `git status`,
        // which reads the same stat data. Re-hashing each origin is one extra
        // read and makes "refuse if anything moved" true rather than
        // approximate, which is what AGENTS.md "Source archives" asks for.
        foreach (var path in paths)
        {
            if (GitBlobSha1(ResolveDeclaredFile(workspace, path)) == copied[path]) continue;
            throw new InvalidOperationException(
                $"installation source {path} changed while it was being copied; refusing the whole operation rather "
                + "than installing a tree that never existed. Retry when the working tree is settled.");
        }
        if (!ObservedWorktree(workspace, paths).SequenceEqual(before, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "the declared installation source set changed while it was being copied; refusing the whole "
                + "operation. Retry when the working tree is settled.");

        // Provenance carries contents, not a mood: which declared paths differ
        // from the base commit, and their bytes. One boolean cannot tell an
        // edited comment from another lane's half-written scheduler.
        var committed = CommittedBlobs(workspace, commit);
        foreach (var path in paths)
        {
            var copy = Path.Combine(root, path);
            if (committed.TryGetValue(path, out var known) && known == copied[path]) continue;
            using var bytes = File.OpenRead(copy);
            differences.Add(new SourceDifference(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        using (var stream = File.Create(archive))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            foreach (var path in paths) writer.WriteEntry(Path.Combine(root, path), path);
        }
        using var archiveBytes = File.OpenRead(archive);
        return new CommittedControlSource(root, commit, archive,
            Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), true, differences, copied);
    }

    static SortedSet<string> DeclaredWorktreePaths(string workspace)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        // Tracked entries carry their Git mode, which is how the committed path
        // refused a gitlink (160000), a symlink (120000) and anything that is
        // not a regular 100644/100755 blob. Reading `-s` restores that exact
        // refusal instead of inferring it from the filesystem afterwards.
        foreach (var record in Git(workspace, ["ls-files", "-sz"]).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t');
            if (separator < 0) throw new InvalidOperationException("Git returned an invalid source entry");
            var candidate = record[(separator + 1)..];
            if (!IsBuildSource(candidate)) continue;
            var mode = record[..separator].Split(' ')[0];
            if (mode is not ("100644" or "100755"))
                throw new InvalidOperationException(
                    $"installation source {candidate} has Git mode {mode}; only a regular file may be installed");
        }
        foreach (var listing in new[]
        {
            Git(workspace, ["ls-files", "-z", "--cached"]),
            Git(workspace, ["ls-files", "-z", "--others", "--exclude-standard"]),
        })
        {
            foreach (var path in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsBuildSource(path)) continue;
                if (path.Split('/').Any(part => part is "" or "." or "..") || path.Contains('\\'))
                    throw new InvalidOperationException($"installation source {path} must be a portable relative path");
                paths.Add(path);
            }
        }
        if (!paths.Any(path => path.StartsWith(CSharpPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("working-tree installation source contains no C# files");
        return paths;
    }

    /// <summary>
    /// The declared file, or a refusal naming what is wrong with it. Every
    /// ancestor is walked because FileInfo.LinkTarget describes the final
    /// component only: with a symlinked directory above it, File.Copy follows
    /// the link at the OS level and bytes from outside the workspace enter the
    /// installed binary. The committed path could not do this — its bytes came
    /// from the object database — so the walk arrives with the worktree.
    /// </summary>
    static string ResolveDeclaredFile(string workspace, string path)
    {
        // Path.GetFullPath is lexical: it normalizes "." and ".." and does not
        // resolve links, so a symlinked workspace root would pass unseen, and a
        // root-level declared file has no ancestor components of its own.
        var root = Path.GetFullPath(workspace);
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidOperationException($"installation workspace {root} is a link");
        var current = root;
        foreach (var part in path.Split('/')[..^1])
        {
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists || directory.LinkTarget is not null)
                throw new InvalidOperationException(
                    $"installation source {path} is reached through {current}, which is missing or a link");
        }
        var file = new FileInfo(Path.Combine(current, path.Split('/')[^1]));
        if (!file.Exists || file.LinkTarget is not null)
            throw new InvalidOperationException($"installation source {path} is missing, a directory, or a link");
        return file.FullName;
    }

    /// <summary>
    /// Copy one declared file under a deadline. `git ls-files --others` will
    /// list a FIFO, socket or device, all of which pass an existence check and
    /// then block File.Copy forever with nothing to interrupt it. The committed
    /// path could not meet one, because it required a 100644/100755 Git mode.
    /// A deadline refuses by name instead of hanging an installation.
    /// </summary>
    static void CopyDeclaredFile(string origin, string destination, string path, Stopwatch budget)
    {
        // One budget for the whole operation. Per-file deadlines bound nothing
        // in aggregate: 736 declared files at 30 seconds each is six hours
        // before an installation gives up.
        var remaining = TimeSpan.FromSeconds(120) - budget.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new InvalidOperationException($"installation source copy exceeded its total deadline at {path}");
        var perFile = remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
        var copy = Task.Run(() => File.Copy(origin, destination));
        if (!copy.Wait(perFile))
            throw new InvalidOperationException(
                $"installation source {path} did not copy within its deadline; it is not a regular file "
                + "(a FIFO, socket or device reads forever) or the filesystem is not answering");
        if (copy.IsFaulted) throw new InvalidOperationException(
            $"installation source {path} could not be copied: {copy.Exception?.GetBaseException().Message}");
    }

    /// <summary>One comparable line per declared path: size and write time.</summary>
    static List<string> ObservedWorktree(string workspace, IEnumerable<string> paths)
    {
        var observed = new List<string>();
        foreach (var path in paths)
        {
            var file = new FileInfo(Path.Combine(Path.GetFullPath(workspace), path));
            observed.Add($"{path}\t{(file.Exists ? file.Length : -1)}\t{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}");
        }
        // Scoped exactly like WorktreeDiffers. Unfiltered, a training run
        // appending to results/ or another lane touching python/ would abort a
        // 736-file installation that has nothing to do with either.
        foreach (var record in Git(workspace, ["status", "--porcelain=1", "-z", "--untracked-files=all"])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length > 3 && IsBuildSource(record[3..])) observed.Add(record);
        }
        return observed;
    }

    static Dictionary<string, string> CommittedBlobs(string workspace, string commit)
    {
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in Git(workspace, ["ls-tree", "-rz", "--full-tree", commit]).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t');
            if (separator < 0) continue;
            var identity = record[..separator].Split(' ');
            if (identity.Length == 3 && identity[1] == "blob") blobs[record[(separator + 1)..]] = identity[2];
        }
        return blobs;
    }

    static string GitBlobSha1(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var length = new FileInfo(path).Length;
        hash.AppendData(Encoding.UTF8.GetBytes($"blob {length}\0"));
        using var input = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, count));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static bool IsBuildSource(string path)
    {
        if (path.StartsWith(CSharpPrefix, StringComparison.Ordinal))
        {
            // CHG 2026-09-26: the parts are taken below src/csharp/, so parts[0]
            // is the project directory; split from the repository root, the
            // project would be parts[2] and a test project would pass as a build input.
            var parts = path[CSharpPrefix.Length..].Split('/');
            if (parts.Any(part => part is "bin" or "obj")) return false;
            // CHG 2026-09-14: a test project is a test input, not a build
            // input -- the same distinction the Go branch below already makes
            // for testdata. No published member is built from Fern.X.Tests, so
            // an edit there changes no installed byte. Counting them cost a
            // real install: two untracked files in Fern.Registry.Tests, left by
            // another lane, made the whole tree differ from HEAD, and the only
            // way through was --from-worktree, which stamps all four binaries
            // "built from a dirty tree" and drops their source archive digest.
            // Two files nobody builds from degraded the provenance of four
            // binaries.
            return !parts[0].EndsWith(".Tests", StringComparison.Ordinal);
        }
        // CHG 2026-09-11: the installer builds the card compiler from the same
        // commit as the C# members, so its project travels too; its tests are
        // test inputs, not build inputs. CHG 2026-09-27: the Gleam project, whose
        // tests live under test/ (the Go module's parity records were under testdata/).
        if (path.StartsWith(CompilerPrefix, StringComparison.Ordinal))
            return !path.StartsWith(CompilerPrefix + "test/", StringComparison.Ordinal)
                && !path.StartsWith(CompilerPrefix + "testdata/", StringComparison.Ordinal);
        // CHG: workflow documents are Roslyn AdditionalFiles, so immutable
        // builds carry their committed bytes alongside the C# owner methods.
        // CHG 2026-09-26: no longer. The workflow-contract analyzer that read
        // docs/how-to/study-run.md and docs/how-to/README.md was removed on the
        // owner's word (docs/programs/2026-09-26-cards-without-hashes,
        // consequences.md B23), so no document is compiled and none is a build
        // source: an edit under docs/ neither refuses an install nor changes
        // what one is built from.
        if (path.Contains('/')) return false;
        // Root imports/settings can affect all C# projects. Other-language
        // configuration and human/work records are not installation inputs.
        return path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || path.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
            || path.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);
    }

    static string Git(string workspace, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start Git for installation source");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        return ReadGitOutput(process, output, errors, 60_000, 10_000);
    }

    internal sealed class GitTerminationException(string message, Exception? inner = null)
        : InvalidOperationException(message, inner);

    // The process and reader tasks are explicit so tests exercise this same
    // deadline path with a real Git child, without changing global settings.
    internal static string ReadGitOutput(Process process, Task<string> output, Task<string> errors,
        int executionDeadlineMs, int terminationDeadlineMs)
    {
        bool finished;
        try
        {
            finished = process.WaitForExit(executionDeadlineMs) && Task.WaitAll([output, errors], terminationDeadlineMs);
        }
        catch (AggregateException error)
        {
            throw new GitTerminationException("Git installation source output EOF could not be confirmed", error);
        }
        if (!finished)
        {
            // CHG: Kill requests termination; it does not prove exit or EOF.
            // Cleanup may proceed only after all three have been observed.
            var shutdown = Stopwatch.StartNew();
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                int Remaining() => (int)Math.Max(0, terminationDeadlineMs - shutdown.ElapsedMilliseconds);
                if (!process.WaitForExit(Remaining()) || !Task.WaitAll([output, errors], Remaining()))
                    throw new GitTerminationException("Git installation source termination or output EOF was not confirmed");
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception
                or AggregateException or InvalidOperationException && error is not GitTerminationException)
            {
                throw new GitTerminationException("Git installation source termination could not be confirmed", error);
            }
            throw new InvalidOperationException("Git installation source read exceeded its deadline");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"cannot read committed installation source: {errors.Result.Trim()}");
        return output.Result;
    }
}
