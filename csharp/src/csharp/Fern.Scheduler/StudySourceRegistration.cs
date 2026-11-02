using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>What one registered source became in the store, and what changed since the last one.</summary>
public sealed record RegisteredStudySource(
    long SourceId,
    bool AlreadyRegistered,
    string CommitSha,
    // CHG 2026-09-26 (cards-without-hashes, schema 21): how the source was registered,
    // commit or working-tree, where this was the SHA-256 over its declared files.
    string RegisteredFrom,
    int FileCount,
    /// <summary>
    /// Paths that differ from the most recent previously registered source:
    /// added, removed, or the same path with different bytes. Empty when this
    /// is the first source, or when nothing changed.
    /// </summary>
    IReadOnlyList<string> ChangedFiles,
    /// <summary>
    /// CHG 2026-09-27: declared files with an uncommitted edit that the study's cells do
    /// not load, left at the commit (the code a cell runs); empty without a study's list.
    /// </summary>
    IReadOnlyList<string> LeftAtCommit);

/// <summary>
/// Registers the <c>sources</c> row a study is registered against: the commit
/// its code was at, and the declaration of every file that code is.
///
/// It replaces the frozen source (owner's cut, 2026-09-13). The freeze copied
/// every declared file into a content store under the deploy root and minted a
/// catalogue of its own, because a source digest was once the only answer to
/// "is this the same experiment". That answer moved to the cell identity, which
/// names the configuration, the four layers of the data, the declaration
/// version and the stage. What was left was a second copy of bytes git already
/// holds, 2.2 GB of it on one machine, backed up by nothing and restorable by
/// no command.
///
/// Git already content-addresses trees. So the pin is the commit, and the
/// declaration beside it says which files of that commit are the source, with a
/// digest and a size each, so an install can verify byte for byte without a
/// second store to verify against.
///
/// The selection is unchanged, because it is the declaration of what a Fern
/// source IS rather than an artefact of how it was stored: the runtime paths, the
/// files a runtime source is required to carry, the refusal when an untracked
/// runtime file would be silently omitted, and a compiled study's declared data
/// inputs, which git ignores and which are therefore declared by digest and read
/// from the working tree.
/// </summary>
public static class StudySourceRegistration
{
    /// <summary>The repository root this workspace belongs to, which is where every git read runs.</summary>
    public static string WorkspaceRoot(string from) =>
        GitText(from, ["rev-parse", "--show-toplevel"]).Trim();

    /// <summary>A registration the caller must not proceed past, with the reason a person needs.</summary>
    public sealed class Refusal(string message) : Exception(message);

    // CHG 2026-09-26 (cards-without-hashes, schema 21): BundleScheme ("source-commit-v1")
    // and WorktreeBundleScheme ("source-worktree-v1"), the prefixes of the bundle digest,
    // became the words RegistryOperations.RegisteredFromCommit and RegisteredFromWorkingTree
    // in sources.registered_from. A registration made with --from-worktree is still told
    // apart from one made at a commit; only the digest is gone.

    /// <summary>
    /// Register the declared source at HEAD.
    /// </summary>
    /// <param name="declaredStudyPath">
    /// A compiled study whose declared data inputs join the source, or null for
    /// the code alone.
    /// </param>
    /// <param name="fromWorktree">
    /// Read a declared file from the working tree even when it differs from the
    /// commit. The binary that installs it then holds bytes no commit carries,
    /// which is why it is a flag a person passes and never a fallback.
    /// </param>
    public static RegisteredStudySource Register(
        RegistryWriter writer,
        string workspaceRoot,
        string? declaredStudyPath,
        bool fromWorktree,
        IReadOnlyList<string>? sourceFiles = null)
    {
        var commit = GitText(workspaceRoot, ["rev-parse", "--verify", "HEAD^{commit}"]).Trim();
        if (commit.Length == 0)
        {
            throw new Refusal("this workspace has no commit; a source is registered at one");
        }

        var tracked = GitPaths(workspaceRoot, ["ls-files", "-z", "--cached"]);
        if (tracked.Count == 0)
        {
            throw new Refusal("git returned no tracked source files");
        }
        var studyInputs = declaredStudyPath is { Length: > 0 } path
            ? StudyFileInputs.Read(path, workspaceRoot)
            : [];
        // CHG 2026-09-22: explicitly declared new modules join the source;
        // unrelated untracked files are neither adopted nor inspected.
        var inputs = studyInputs.ToList();
        foreach (var sourceFile in sourceFiles ?? [])
        {
            if (!fromWorktree)
                throw new Refusal("--source-file reads working-tree bytes and requires --from-worktree");
            var full = Path.GetFullPath(sourceFile, workspaceRoot);
            var relative = Path.GetRelativePath(workspaceRoot, full).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or "..")
                || !IsRuntimeSourcePath(relative))
                throw new Refusal($"source file must name runtime code below the source root: {sourceFile}");
            if (!File.Exists(full))
                throw new Refusal($"declared source file is missing: {relative}");
            var bytes = File.ReadAllBytes(full);
            var input = new StudyFileInput(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), (ulong)bytes.LongLength);
            var prior = inputs.FirstOrDefault(existing => existing.Path == relative);
            if (prior is not null && prior != input)
                throw new Refusal($"declared source file conflicts with a Study input: {relative}");
            if (prior is null) inputs.Add(input);
        }
        StudyFileInputs.Verify(workspaceRoot, inputs);

        var declaredPaths = new SortedSet<string>(tracked.Where(IsRuntimeSourcePath), StringComparer.Ordinal);
        declaredPaths.UnionWith(inputs.Select(input => input.Path));

        // Omission is a preparation error, never implicit permission to adopt
        // another lane's new runtime code.
        // CHG 2026-09-22: the declaration owns scope. A directory match is
        // not proof of a dependency; only named required/declared files are checked.
        foreach (var required in RequiredRuntimeFiles)
        {
            if (!declaredPaths.Contains(required))
            {
                throw new Refusal($"runtime source requires {required}; track it or declare it");
            }
        }
        if (declaredPaths.Contains("python/fern_train/config_cards.py") && !declaredPaths.Contains("studies/configs.json"))
        {
            throw new Refusal("runtime source config_cards.py requires studies/configs.json; track it");
        }

        // Which declared paths are symbolic links at the commit. The freeze
        // classified every entry and the installer refuses any kind but a
        // regular file by name; without this the link's target PATH would be
        // declared as the file's contents and a worker would import that text.
        var symbolicLinks = SymbolicLinksAt(workspaceRoot, commit, declaredPaths);

        var trackedDeclared = declaredPaths.Where(tracked.Contains).ToArray();
        // CHG 2026-09-27 (owner: "这一类必须只关注核心file 不能树一动就拒绝", only a study's
        // core files; refusing whenever anything in the tree moved was silly): with a compiled
        // study that lists the files its cells load (fern_train.cell_code_files), only those
        // are checked for uncommitted edits. An edit to any other declared file is left at the
        // commit, which is the code a cell runs, and is named in the answer. A file the cells
        // load that no commit carries is refused unless it is declared with --source-file.
        // Without the list (the code alone, or a study compiled before the list existed) every
        // declared file is checked, as before.
        var core = declaredStudyPath is { Length: > 0 } compiled ? StudyFileInputs.CellCodeFiles(compiled) : null;
        var dirty = DirtyAmong(workspaceRoot, core is null ? trackedDeclared : trackedDeclared.Where(core.Contains).ToArray());
        var leftAtCommit = core is null
            ? []
            : DirtyAmong(workspaceRoot, trackedDeclared.Where(path => !core.Contains(path)).ToArray());
        var uncommitted = core is null
            ? []
            : core.Where(path => !tracked.Contains(path) && !declaredPaths.Contains(path)
                    && File.Exists(Path.Combine(workspaceRoot, path)))
                .Order(StringComparer.Ordinal).ToArray();
        if (uncommitted.Length > 0)
        {
            throw new Refusal(
                $"{uncommitted.Length} file(s) this study's cells load are in no commit, so a run from {commit[..12]} "
                + "would not find them:\n  " + string.Join("\n  ", uncommitted.Take(10))
                + (uncommitted.Length > 10 ? $"\n  ... and {uncommitted.Length - 10} more" : "")
                + "\n  commit them, or declare each with --source-file and --from-worktree");
        }
        if (dirty.Count > 0 && !fromWorktree)
        {
            throw new Refusal(
                $"{dirty.Count} {(core is null ? "declared source" : "of this study's core")} file(s) differ from {commit[..12]}, "
                + "so a run registered at that commit would not be the code in front of you:\n  " + string.Join("\n  ", dirty.Take(10))
                + (dirty.Count > 10 ? $"\n  ... and {dirty.Count - 10} more" : "")
                + "\n  commit them, or pass --from-worktree to register bytes no commit carries");
        }

        var bytesByPath = ReadAtCommit(workspaceRoot, commit, dirty.Count > 0 ? trackedDeclared.Except(dirty).ToArray() : trackedDeclared);
        foreach (var edited in dirty)
        {
            var full = Path.Combine(workspaceRoot, edited);
            if (!File.Exists(full))
            {
                throw new Refusal($"declared source file {edited} differs from {commit[..12]} and is not in this workspace either");
            }
            bytesByPath[edited] = File.ReadAllBytes(full);
        }
        foreach (var input in inputs)
        {
            if (bytesByPath.ContainsKey(input.Path)) continue;
            var full = Path.Combine(workspaceRoot, input.Path);
            if (!File.Exists(full))
            {
                throw new Refusal($"declared study input {input.Path} is not in this workspace");
            }
            bytesByPath[input.Path] = File.ReadAllBytes(full);
        }

        // The scheme says where the bytes came from, so a reader of a `sources`
        // row can tell a registration made from a commit from one made with
        // --from-worktree, which declares bytes no commit carries.
        var registeredFrom = dirty.Count > 0 || sourceFiles is { Count: > 0 }
            ? RegistryOperations.RegisteredFromWorkingTree : RegistryOperations.RegisteredFromCommit;
        var declaration = new JsonArray();
        foreach (var declaredPath in declaredPaths)
        {
            var bytes = bytesByPath[declaredPath];
            // CHG 2026-09-26 (cards-without-hashes, schema 21): a declared file is its path,
            // its kind and its size; its byte_digest and the bundle digest over all of them
            // are gone (owner: "registry should not record those too ... we delete it anyways").
            declaration.Add(new JsonObject
            {
                ["path"] = declaredPath,
                ["kind"] = symbolicLinks.Contains(declaredPath) ? "symbolic_link" : "regular_file",
                ["bytes"] = bytes.LongLength,
            });
        }
        var declaredJson = declaration.ToJsonString();

        // The observation window the rule asks for (AGENTS.md, "Source
        // archives"): every byte read from the working tree is read again after
        // the declaration is built, and a file that moved between the two reads
        // refuses the whole registration. A commit needs no such window -- it is
        // one instant by construction -- so this is only for --from-worktree.
        foreach (var edited in dirty)
        {
            var again = File.ReadAllBytes(Path.Combine(workspaceRoot, edited));
            if (!again.AsSpan().SequenceEqual(bytesByPath[edited]))
            {
                throw new Refusal(
                    $"{edited} changed while this source was being declared; nothing was registered. "
                    + "A source is one instant of one tree, so the whole registration is refused rather than "
                    + "half of it recorded");
            }
        }
        // Explicitly included untracked files share the same stable observation window.
        StudyFileInputs.Verify(workspaceRoot, inputs);

        var previous = NewestDeclaration(writer.Connection);
        var registered = RegistryOperations.RegisterSource(writer, commit, declaredJson, registeredFrom);
        if (!registered.TryGetValue(out var source))
        {
            registered.TryGetRefusal(out var refusal);
            throw new Refusal(refusal.Detail);
        }
        return new RegisteredStudySource(
            source.SourceId,
            source.AlreadyRegistered,
            commit,
            registeredFrom,
            declaredPaths.Count,
            previous is null ? [] : Differences(workspaceRoot, previous.Value, commit, declaredJson),
            leftAtCommit);
    }

    /// <summary>
    /// The declared paths git records as symbolic links, by their mode in the
    /// commit's tree. `git ls-tree` prints 120000 for a link and 100644 or
    /// 100755 for a file, which is the only place that distinction survives:
    /// `cat-file` hands back a link's target as its content and says nothing.
    /// </summary>
    static HashSet<string> SymbolicLinksAt(string workspaceRoot, string commit, IReadOnlyCollection<string> declared)
    {
        var links = new HashSet<string>(StringComparer.Ordinal);
        if (declared.Count == 0) return links;
        foreach (var record in GitText(workspaceRoot, ["ls-tree", "-rz", "--full-tree", commit])
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var path = record[(tab + 1)..];
            if (record.StartsWith("120000 ", StringComparison.Ordinal) && declared.Contains(path))
            {
                links.Add(path);
            }
        }
        return links;
    }

    /// <summary>
    /// Which of these declared files differ from the commit: modified, staged,
    /// or gone from the working tree. Scoped to the declaration, because another
    /// lane touching a file this source does not declare is not this source's
    /// problem.
    /// </summary>
    static IReadOnlyList<string> DirtyAmong(string workspaceRoot, IReadOnlyList<string> declared)
    {
        if (declared.Count == 0) return [];
        var interesting = new HashSet<string>(declared, StringComparer.Ordinal);
        var dirty = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in GitText(workspaceRoot, ["status", "--porcelain=1", "-z"]).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length <= 3) continue;
            var path = record[3..];
            if (interesting.Contains(path)) dirty.Add(path);
        }
        return dirty.ToArray();
    }

    /// <summary>
    /// The bytes of these paths at that commit; a path the commit does not
    /// carry is simply absent from the answer. Read in one <c>git cat-file
    /// --batch</c> (<see cref="ReadAtCommit"/>): one process rather than one
    /// per file, and binary-safe, the batch stream read as bytes, never
    /// decoded as text.
    /// </summary>
    public static Dictionary<string, byte[]> BytesAtCommit(string workspaceRoot, string commit, IReadOnlyList<string> paths) =>
        ReadAtCommit(workspaceRoot, commit, paths, missingIsAbsent: true);

    static Dictionary<string, byte[]> ReadAtCommit(
        string workspaceRoot, string commit, IReadOnlyList<string> paths, bool missingIsAbsent = false)
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (paths.Count == 0) return bytes;
        // The batch protocol is line-delimited, so a path holding a newline
        // would split into two requests and put the stream out of step with the
        // caller. Refused by name rather than asked for.
        foreach (var path in paths)
        {
            if (path.Contains('\n') || path.Contains('\r'))
            {
                throw new Refusal($"a declared path holds a line break and cannot be read from git: {path.Replace("\n", "\\n")}");
            }
        }

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "cat-file", "--batch" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Refusal("cannot start git");
        var errors = process.StandardError.ReadToEndAsync();

        // The requests go in on their own task while this one reads the answers
        // out. git answers each request with the whole blob, so it fills the
        // pipe after a few files and stops reading stdin; a caller that wrote
        // every request before reading anything would deadlock once the
        // requests themselves passed the pipe's capacity. Measured on this
        // repository that was about 780 declared files, and there are 445.
        var input = process.StandardInput;
        var requests = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                input.Write(commit + ":" + path + "\n");
            }
            input.Flush();
            input.Close();
        });

        var stream = process.StandardOutput.BaseStream;
        foreach (var path in paths)
        {
            var header = ReadLine(stream);
            var parts = header.Split(' ');
            if (parts.Length != 3 || parts[1] != "blob")
            {
                // `<object> missing` is one line and no content, so the stream
                // stays in step and the caller is told by omission.
                if (missingIsAbsent && parts.Length >= 2 && parts[^1] is "missing" or "ambiguous") continue;
                throw new Refusal($"{path} is not a file at {commit[..12]}: git said \"{header}\"");
            }
            var size = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var content = new byte[size];
            var read = 0;
            while (read < size)
            {
                var chunk = stream.Read(content, read, (int)(size - read));
                if (chunk <= 0) throw new Refusal($"git stopped {read} of {size} bytes into {path}");
                read += chunk;
            }
            if (stream.ReadByte() != '\n')
            {
                throw new Refusal($"git's object stream is out of step after {path}");
            }
            bytes[path] = content;
        }
        requests.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Refusal($"git cat-file exited {process.ExitCode}: {errors.Result.Trim()}");
        }
        return bytes;
    }

    static string ReadLine(Stream stream)
    {
        var line = new List<byte>(128);
        while (true)
        {
            var next = stream.ReadByte();
            if (next < 0) throw new Refusal("git's object stream ended inside a header");
            if (next == '\n') return Encoding.UTF8.GetString(line.ToArray());
            line.Add((byte)next);
        }
    }

    static (string Commit, string DeclaredJson)? NewestDeclaration(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT commit_sha, declared_files_json FROM sources ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    static IReadOnlyList<string> GitPaths(string workspaceRoot, string[] arguments) =>
        GitText(workspaceRoot, arguments).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    internal static string GitText(string workspaceRoot, string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Refusal("cannot start git");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Refusal($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {errors.Result.Trim()}");
        }
        return output.Result;
    }

    /// <summary>
    /// Which tracked paths a runtime source declares. Moved here unchanged from
    /// the freeze on 2026-09-13: it says what a Fern source IS, which outlives
    /// how the bytes were stored.
    /// </summary>
    internal static bool IsRuntimeSourcePath(string path)
    {
        if (RequiredRuntimeFiles.Contains(path, StringComparer.Ordinal)
            // CHG 2026-09-14: dataset-channels.json joins them. It is what the
            // Go compiler reads instead of taking a dataset's state width as a
            // flag, so a worker machine's snapshot carries it for the same
            // reason it carries the configuration catalogue.
            // CHG 2026-09-14: datasets.json joins them. A study card whose data
            // stage names a dataset card cannot be compiled without it, for the
            // same reason a card naming a configuration needs configs.json.
            || path is "studies/configs.json" or "studies/presets.toml"
                or "studies/dataset-channels.json" or "studies/datasets.json") return true;
        // CHG 2026-09-26: the Serie package moved from serie/ to src/serie/.
        var package = new[] { "python/fern_train/", "python/fern_analysis/", "python/fern_stage_runtime/", "src/serie/", "other_models/" }
            .FirstOrDefault(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        if (package is null || !(path.EndsWith(".py", StringComparison.Ordinal) || path.EndsWith(".pyi", StringComparison.Ordinal)))
            return false;
        return !path[package.Length..].Split('/').Any(part => part.StartsWith('.') || part.Contains(':')
            || part is "tests" or "docs" or "examples" or "benchmarks" or "assets" or "__pycache__"
                or "build" or "dist" or "demo_dist" or "cache" or "results" or "weights" or "checkpoints");
    }

    internal static readonly string[] RequiredRuntimeFiles =
    [
        "pyproject.toml", "uv.lock", "python/fern_train/worker.py",
        "python/fern_train/wire/training_v1.py", "python/fern_train/wire/training_v2.py",
        "python/fern_stage_runtime/stage_pipeline_forecast_origin_v1.json",
        "python/fern_stage_runtime/stage_pipeline_study_cell_v1.json",
    ];

    /// <summary>
    /// Which paths differ between two declarations. The answer is what a person
    /// asks first when an id they expected to be the same is not: "which file".
    ///
    /// CHG 2026-09-26 (cards-without-hashes, schema 21): no digest is compared. A path
    /// declared by one and not the other differs, and so does a path both declare
    /// whose bytes git says differ between the two commits (git diff --name-only).
    /// When the earlier commit is not in this repository, only the declared paths are
    /// compared. Uncommitted edits are not seen here, and a cell runs the commit's
    /// bytes anyway (CellCode).
    /// </summary>
    static IReadOnlyList<string> Differences(
        string workspaceRoot, (string Commit, string DeclaredJson) previous, string commit, string currentJson)
    {
        var before = DeclaredPaths(previous.DeclaredJson);
        var now = DeclaredPaths(currentJson);
        var changed = new SortedSet<string>(before.Except(now).Concat(now.Except(before)), StringComparer.Ordinal);
        if (!string.Equals(previous.Commit, commit, StringComparison.Ordinal))
        {
            try
            {
                foreach (var path in GitPaths(workspaceRoot, ["diff", "--name-only", "-z", previous.Commit, commit]))
                {
                    if (before.Contains(path) && now.Contains(path)) changed.Add(path);
                }
            }
            catch (Refusal)
            {
                // The earlier commit is not in this repository: its content cannot be compared.
            }
        }
        return [.. changed];
    }

    static HashSet<string> DeclaredPaths(string declaredFilesJson)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(declaredFilesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return paths;
        }
        foreach (var file in document.RootElement.EnumerateArray())
        {
            if (file.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
            {
                paths.Add(path.GetString()!);
            }
        }
        return paths;
    }
}
