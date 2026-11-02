using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Experiment;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// The directory a claimed cell's trainer runs in, and what the attempt records
/// about it. A Site declares where its cells take their code from
/// (<c>code_from</c> in its execution profile, read by
/// <see cref="RegistryOperations.CodeOf"/>):
///
/// <list type="bullet">
/// <item><c>working-tree</c> (the owner's option A): the Site's git repository as
/// it is when the cell starts. The attempt records the repository's HEAD, the
/// commit the study's source was registered at, which declared files differ
/// from that commit now, and the difference itself as a file in the attempt's
/// folder, so what ran can be told from what was registered.</item>
/// <item><c>commit</c> (the owner's option B): the source's declared files, read
/// from git at the registered commit into
/// <c>&lt;work root&gt;/cell-code/attempt-&lt;id&gt;</c> just before the trainer
/// starts, and deleted when the attempt ends.</item>
/// </list>
///
/// Its own file because it owns one question, where a cell's code comes from,
/// which neither the install (a stored tree) nor the worker (a process and its
/// beat) owns. Design: docs/programs/2026-09-26-source-roots-stop-being-full-copies/design.md,
/// slice 2; the owner asked for both A and B with the default chosen later.
/// CHG 2026-09-26 (slice 4): the third answer, installed-source, is retired;
/// the helpers that read a declaration and write its files moved here from
/// StudySourceInstallation when the install was retired.
///
/// CHG 2026-09-26 (slice 3, owner: "做法1合理"): a supervisor on another machine
/// (<paramref name="codeFromHome"/>) has no repository. Its worker asks the Home
/// whether a cell's code can be given and, once the cell is claimed, asks for
/// it: the Home makes the directory the same way on its own machine
/// (<see cref="Pack"/>), sends it packed, and the worker unpacks it into the
/// attempt's directory, runs there and deletes it. Nothing is kept on either side.
/// </summary>
public sealed class CellCode(IStudyStore store, string workRoot, bool codeFromHome)
{
    /// <summary>The kind of the event every attempt records about its code.</summary>
    public const string EventKind = "cell_code";

    /// <summary>Under the work root, where <c>commit</c> writes one directory per attempt.</summary>
    public const string DirectoryName = "cell-code";

    /// <summary>Under <c>working-tree</c>, the attempt's file holding how the declared files differ from the registered commit.</summary>
    public const string DifferenceFile = "code-against-registered.diff";

    /// <summary>
    /// The directory a cell runs in, the event detail that says where it came
    /// from, and whether it is this attempt's to delete when the attempt ends.
    /// </summary>
    public sealed record Prepared(string Directory, string DetailJson, bool DeleteAfter);

    /// <summary>
    /// One attempt's code as the Home sends it: the directory packed as a
    /// gzip-compressed tar (base64, to travel inside the Home's JSON), what the
    /// attempt records about it, and, for a working-tree Site, how the declared
    /// files differ from the registered commit.
    /// </summary>
    public sealed record Package(string ArchiveBase64, string DetailJson, string? Difference);

    readonly Dictionary<long, RegistryOperations.SourceDeclaration> _sources = [];
    readonly HashSet<(string Repository, string Commit)> _commitsPresent = [];

    /// <summary>The directory <c>commit</c> writes for one attempt.</summary>
    public static string AttemptDirectory(string workRoot, long attemptId) =>
        Path.Combine(Path.GetFullPath(workRoot), DirectoryName, $"attempt-{attemptId}");

    /// <summary>
    /// Why this machine cannot give a cell of this source its code on a Site
    /// that declares <paramref name="code"/>, or null when it can. Asked before
    /// the claim, only for the supervisor's own Site, so a machine that cannot
    /// provide the code never opens an attempt for it: on 2026-09-26 a missing
    /// directory found after the claim cost 74 attempts in fifteen seconds.
    /// </summary>
    /// <summary>
    /// CHG 2026-09-27 (owner: "fix all of them", after the review of 2026-09-26):
    /// a Site that takes code from a commit runs only sources registered from a
    /// commit. A source registered from a working tree, or before schema 21
    /// recorded how, may hold files no commit has, and what they held cannot
    /// be known here; the search for their bytes by SHA-256 across the
    /// registered commit, later commits and the folder is gone with this rule.
    /// The remedy is one command: commit, then register the source again.
    /// </summary>
    public static string? NotFromACommit(RegistryOperations.SourceDeclaration source) =>
        source.RegisteredFrom == RegistryOperations.RegisteredFromCommit
            ? null
            : $"source {source.SourceId} was registered from {source.RegisteredFrom switch
            {
                RegistryOperations.RegisteredFromWorkingTree => "a working tree",
                _ => "a tree its registration did not record",
            }}, so what its files held cannot be known from commit {Short(source.CommitSha)}; "
              + "commit those files and register the source again, and a Site that takes code from a commit can run it";

    public string? CannotGive(long siteId, RegistryOperations.SiteCode code, long sourceId)
    {
        if (codeFromHome)
        {
            // The repository is on the Home's machine; the Home looks.
            return store.CannotGiveCode(siteId, sourceId);
        }
        var repository = code.Repository!;
        if (!Directory.Exists(repository))
        {
            return $"the Site takes its cells' code from {code.From} of {repository}, which does not exist on this machine";
        }
        if (!Directory.Exists(Path.Combine(repository, ".git")) && !File.Exists(Path.Combine(repository, ".git")))
        {
            return $"the Site takes its cells' code from {code.From} of {repository}, which is not a git repository";
        }
        if (code.From == RegistryOperations.CodeFromWorkingTree)
        {
            return null;
        }
        if (Source(sourceId) is not { } source)
        {
            return $"source {sourceId} is not registered, so the commit its cells run at is unknown";
        }
        if (NotFromACommit(source) is { } notFromACommit)
        {
            return notFromACommit;
        }
        if (_commitsPresent.Contains((repository, source.CommitSha)))
        {
            return null;
        }
        try
        {
            StudySourceRegistration.GitText(repository, ["cat-file", "-e", source.CommitSha + "^{commit}"]);
            _commitsPresent.Add((repository, source.CommitSha));
            return null;
        }
        catch (StudySourceRegistration.Refusal)
        {
            return $"the repository {repository} does not hold commit {Short(source.CommitSha)}, "
                + $"which source {sourceId} was registered at; its cells cannot be given their code here";
        }
    }

    /// <summary>
    /// Make the directory a claimed cell runs in. <paramref name="differencePath"/>
    /// is where a <c>working-tree</c> attempt writes how its declared files
    /// differ from the registered commit. A refusal here comes after the claim,
    /// so it is the machine's failure, not the cell's: the caller records it so.
    /// </summary>
    public TextResult<Prepared> Prepare(StudyOrchestrator.ClaimedCell cell, string differencePath)
    {
        if (cell.SourceRoot is not { Length: > 0 } root)
        {
            return TextResult<Prepared>.Refused($"cell {cell.CellName} was claimed without its Site's repository");
        }
        // CHG 2026-09-26 (slice 4): a claim from a Home older than slice 2 names
        // no code_from, which meant an installed source; those are retired.
        if (cell.CodeFrom is not { } codeFrom
            || codeFrom is not (RegistryOperations.CodeFromWorkingTree or RegistryOperations.CodeFromCommit))
        {
            return TextResult<Prepared>.Refused(
                $"cell {cell.CellName} was claimed with code_from {cell.CodeFrom ?? "(none)"}; a Site takes its code from {RegistryOperations.CodeFromWorkingTree} or {RegistryOperations.CodeFromCommit}");
        }
        if (codeFromHome)
        {
            return FromHome(cell, differencePath);
        }
        if (cell.SourceId is not { } sourceId)
        {
            return TextResult<Prepared>.Refused($"cell {cell.CellName} was claimed without its source, so its code cannot be made");
        }
        if (Source(sourceId) is not { } source)
        {
            return TextResult<Prepared>.Refused($"source {sourceId} of cell {cell.CellName} is not registered");
        }
        List<DeclaredFile> declared;
        try
        {
            declared = ReadDeclaration(source.DeclaredFilesJson, sourceId);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
        {
            return TextResult<Prepared>.Refused($"source {sourceId}'s declared files cannot be read: {error.Message}");
        }
        if (codeFrom == RegistryOperations.CodeFromWorkingTree)
        {
            return FromWorkingTree(root, source, declared, differencePath);
        }
        if (NotFromACommit(source) is { } notFromACommit)
        {
            return TextResult<Prepared>.Refused(notFromACommit);
        }
        return FromCommit(cell, root, source, declared);
    }

    /// <summary>Delete the directory an attempt was given, when it was made for that attempt.</summary>
    public static void Release(Prepared prepared)
    {
        if (!prepared.DeleteAfter || !Directory.Exists(prepared.Directory))
        {
            return;
        }
        Directory.Delete(prepared.Directory, recursive: true);
    }

    /// <summary>
    /// Delete the <c>commit</c> directories under the work root whose attempts
    /// are no longer open: what a supervisor that died mid-attempt left. Returns
    /// what it deleted. An attempt still open keeps its directory, because its
    /// trainer may still be reading it.
    /// </summary>
    public static IReadOnlyList<string> RemoveLeftovers(string workRoot, IReadOnlySet<long> openAttempts)
    {
        var parent = Path.Combine(Path.GetFullPath(workRoot), DirectoryName);
        if (!Directory.Exists(parent))
        {
            return [];
        }
        var removed = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(parent, "attempt-*"))
        {
            var name = Path.GetFileName(directory);
            if (!long.TryParse(name["attempt-".Length..], out var attemptId) || openAttempts.Contains(attemptId))
            {
                continue;
            }
            Directory.Delete(directory, recursive: true);
            removed.Add(directory);
        }
        return removed;
    }

    TextResult<Prepared> FromWorkingTree(
        string repository, RegistryOperations.SourceDeclaration source,
        IReadOnlyList<DeclaredFile> declared, string differencePath)
    {
        var described = DescribeWorkingTree(repository, source, declared);
        if (!described.IsOk)
        {
            return TextResult<Prepared>.Refused(described.Refusal);
        }
        var (detail, difference) = described.Value;
        detail["directory"] = repository;
        if (difference is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(differencePath))!);
            File.WriteAllText(differencePath, difference);
            detail["difference"] = Path.GetFullPath(differencePath);
        }
        return TextResult<Prepared>.Ok(new Prepared(repository, detail.ToJsonString(), DeleteAfter: false));
    }

    /// <summary>
    /// What a working-tree attempt records: the repository's HEAD, the
    /// registered commit, which declared files differ from it now, which are
    /// absent, and the difference itself (null when nothing differs).
    /// </summary>
    static TextResult<(JsonObject Detail, string? Difference)> DescribeWorkingTree(
        string repository, RegistryOperations.SourceDeclaration source,
        IReadOnlyList<DeclaredFile> declared)
    {
        var paths = declared.Select(file => file.Path).ToArray();
        string? difference = null;
        var detail = new JsonObject
        {
            ["code_from"] = RegistryOperations.CodeFromWorkingTree,
            ["registered_commit"] = source.CommitSha,
            ["declared_files"] = paths.Length,
        };
        try
        {
            detail["head"] = StudySourceRegistration.GitText(repository, ["rev-parse", "--verify", "HEAD^{commit}"]).Trim();
        }
        catch (StudySourceRegistration.Refusal refusal)
        {
            return TextResult<(JsonObject, string?)>.Refused($"the repository {repository} has no readable HEAD: {refusal.Message}");
        }
        // How the declared files in the working tree differ from the registered
        // commit, tracked files only (git does not compare an untracked file
        // with a commit). A commit this repository does not hold is said so and
        // does not stop the cell: under this answer the cell runs what is here.
        try
        {
            var differing = StudySourceRegistration.GitText(
                    repository, ["--literal-pathspecs", "diff", "--name-only", source.CommitSha, "--", .. paths])
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var list = new JsonArray();
            foreach (var path in differing) list.Add(path);
            detail["differing_files"] = list;
            if (differing.Length != 0)
            {
                difference = StudySourceRegistration.GitText(
                    repository, ["--literal-pathspecs", "diff", source.CommitSha, "--", .. paths]);
            }
        }
        catch (StudySourceRegistration.Refusal refusal)
        {
            detail["differing_files_unknown"] = refusal.Message;
        }
        var missing = new JsonArray();
        foreach (var path in paths.Where(path => !File.Exists(Path.Combine(repository, path)))) missing.Add(path);
        if (missing.Count != 0) detail["declared_files_absent"] = missing;
        return TextResult<(JsonObject, string?)>.Ok((detail, difference));
    }

    /// <summary>
    /// On the Home's machine: make one attempt's code the way its Site declares
    /// and pack it. <c>commit</c> writes the declared files at the registered
    /// commit, as a local commit Site would. <c>working-tree</c> copies what a
    /// local working-tree Site would import: every tracked runtime file and every
    /// declared file, as the working tree holds them now. (A local working-tree
    /// cell runs in the whole repository; a remote one gets these files, so a
    /// new module that is neither tracked nor declared does not travel.) The
    /// directory is made in a temporary folder and deleted once packed.
    /// </summary>
    public TextResult<Package> Pack(RegistryOperations.SiteCode code, RegistryOperations.SourceDeclaration source)
    {
        if (code.From is not (RegistryOperations.CodeFromWorkingTree or RegistryOperations.CodeFromCommit))
        {
            return TextResult<Package>.Refused($"a Site whose code comes from {code.From} is not sent code by the Home");
        }
        List<DeclaredFile> declared;
        try
        {
            declared = ReadDeclaration(source.DeclaredFilesJson, source.SourceId);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
        {
            return TextResult<Package>.Refused($"source {source.SourceId}'s declared files cannot be read: {error.Message}");
        }
        var repository = code.Repository!;
        var directory = Path.Combine(Path.GetFullPath(workRoot), $"fern-cell-code-pack-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            JsonObject detail;
            string? difference = null;
            if (code.From == RegistryOperations.CodeFromCommit)
            {
                if (NotFromACommit(source) is { } notFromACommit)
                {
                    return TextResult<Package>.Refused(notFromACommit);
                }
                var fromWorkingTree = WriteDeclaredFiles(directory, repository, source.SourceId, source.CommitSha, declared);
                var taken = new JsonArray();
                foreach (var path in fromWorkingTree) taken.Add(path);
                detail = new JsonObject
                {
                    ["code_from"] = RegistryOperations.CodeFromCommit,
                    ["registered_commit"] = source.CommitSha,
                    ["declared_files"] = declared.Count,
                    ["from_working_tree"] = taken,
                };
            }
            else
            {
                var described = DescribeWorkingTree(repository, source, declared);
                if (!described.IsOk)
                {
                    return TextResult<Package>.Refused(described.Refusal);
                }
                (detail, difference) = described.Value;
                var runtime = StudySourceRegistration.GitText(repository, ["ls-files", "-z"])
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Where(StudySourceRegistration.IsRuntimeSourcePath);
                var sent = 0;
                foreach (var path in runtime.Concat(declared.Select(file => file.Path)).Distinct(StringComparer.Ordinal))
                {
                    var from = Path.Combine(repository, path);
                    if (!File.Exists(from))
                    {
                        continue;
                    }
                    var to = SafeChildPath(directory, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to);
                    sent++;
                }
                detail["files_sent"] = sent;
            }
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                TarFile.CreateFromDirectory(directory, gzip, includeBaseDirectory: false);
            }
            detail["packed_bytes"] = packed.Length;
            return TextResult<Package>.Ok(new Package(Convert.ToBase64String(packed.ToArray()), detail.ToJsonString(), difference));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or StudySourceRegistration.Refusal)
        {
            return TextResult<Package>.Refused(
                $"the code of source {source.SourceId} could not be made from {code.From} of {repository}: {error.Message}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// On a remote worker: ask the Home for this attempt's code and unpack it
    /// into the attempt's directory, which is deleted when the attempt ends. A
    /// Home that cannot be reached is not answered here: the exception reaches
    /// the orchestrator, which records the attempt the way it records any
    /// worker that could not finish.
    /// </summary>
    TextResult<Prepared> FromHome(StudyOrchestrator.ClaimedCell cell, string differencePath)
    {
        var asked = store.PackCellCode(cell.AttemptId, cell.FencingEpoch);
        if (!asked.TryGetValue(out var package))
        {
            asked.TryGetRefusal(out var refusal);
            return TextResult<Prepared>.Refused($"the Home did not give the code of cell {cell.CellName}: {refusal.Detail}");
        }
        var directory = AttemptDirectory(workRoot, cell.AttemptId);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            Directory.CreateDirectory(directory);
            using (var archive = new MemoryStream(Convert.FromBase64String(package.ArchiveBase64)))
            using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, directory, overwriteFiles: false);
            }
            var detail = JsonNode.Parse(package.DetailJson)!.AsObject();
            detail["directory"] = directory;
            detail["made_by"] = "home";
            if (package.Difference is { } difference)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(differencePath))!);
                File.WriteAllText(differencePath, difference);
                detail["difference"] = Path.GetFullPath(differencePath);
            }
            return TextResult<Prepared>.Ok(new Prepared(directory, detail.ToJsonString(), DeleteAfter: true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            return TextResult<Prepared>.Refused($"the code the Home sent for cell {cell.CellName} could not be unpacked: {error.Message}");
        }
    }

    TextResult<Prepared> FromCommit(
        StudyOrchestrator.ClaimedCell cell, string repository, RegistryOperations.SourceDeclaration source,
        IReadOnlyList<DeclaredFile> declared)
    {
        var directory = AttemptDirectory(workRoot, cell.AttemptId);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            Directory.CreateDirectory(directory);
            // CHG 2026-09-26 (owner): a source registered from a commit is taken
            // from git as it is; its declared bytes are that commit's.
            var fromWorkingTree = WriteDeclaredFiles(directory, repository, source.SourceId, source.CommitSha, declared);
            var taken = new JsonArray();
            foreach (var path in fromWorkingTree) taken.Add(path);
            return TextResult<Prepared>.Ok(new Prepared(directory, new JsonObject
            {
                ["code_from"] = RegistryOperations.CodeFromCommit,
                ["directory"] = directory,
                ["registered_commit"] = source.CommitSha,
                ["declared_files"] = declared.Count,
                ["from_working_tree"] = taken,
            }.ToJsonString(), DeleteAfter: true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or StudySourceRegistration.Refusal)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            return TextResult<Prepared>.Refused(
                $"the code of cell {cell.CellName} could not be written from commit {Short(source.CommitSha)} of {repository}: {error.Message}");
        }
    }

    RegistryOperations.SourceDeclaration? Source(long sourceId)
    {
        if (_sources.TryGetValue(sourceId, out var known))
        {
            return known;
        }
        if (store.ReadSource(sourceId) is not { } read)
        {
            return null;
        }
        _sources[sourceId] = read;
        return read;
    }

    static string Short(string commit) => commit[..Math.Min(12, commit.Length)];

    // ── a source's declaration and its files (moved from StudySourceInstallation, 2026-09-26) ──

    /// <summary>One declared file of a registered source: its path, its kind and its size.</summary>
    // CHG 2026-09-26 (cards-without-hashes; owner: "registry should not record those too ... we
    // delete it anyways"): the declared digest is no longer read; a declaration that still
    // carries byte_digest is read without it.
    // CHG 2026-09-26 (after 搬迁2号's review): RegisteredDigest was the byte_digest a
    // declaration written before schema 21 carries, read so those sources kept the check
    // the installed binary made (46 of 99 studies with unfinished cells were registered
    // from a working tree). CHG 2026-09-27: gone; such a source is refused by
    // NotFromACommit before any file is read, and a digest the declaration still
    // carries is left unread.
    internal sealed record DeclaredFile(string Path, string Kind, long Bytes);

    internal static List<DeclaredFile> ReadDeclaration(string declaredJson, long sourceId)
    {
        using var document = JsonDocument.Parse(declaredJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"source {sourceId} declared_files_json must be an array of declared files");
        }

        var declared = new List<DeclaredFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in document.RootElement.EnumerateArray())
        {
            var path = RequiredString(file, "path", $"source {sourceId} file");
            var kind = RequiredString(file, "kind", $"source {sourceId} file {path}");
            if (kind != "regular_file")
            {
                throw new InvalidOperationException(
                    $"source {sourceId} declares {path} as {kind}; only a regular file can be installed");
            }
            if (!file.TryGetProperty("bytes", out var byteCount) || !byteCount.TryGetInt64(out var bytes) || bytes < 0)
            {
                throw new InvalidOperationException($"source {sourceId} file {path} has an invalid byte count");
            }
            if (!seen.Add(path))
            {
                throw new InvalidOperationException($"source {sourceId} declares {path} more than once");
            }
            declared.Add(new DeclaredFile(path, kind, bytes));
        }

        declared.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return declared;
    }

    /// <summary>
    /// Write a source's declared files into <paramref name="directory"/>, which
    /// must be empty: each from git at <paramref name="commitSha"/>, or from the
    /// workspace when the commit does not hold the declared bytes and the
    /// workspace does, then check that the directory holds exactly the declared
    /// files. Returns the paths that came from the workspace. Moved out of
    /// <see cref="Materialize"/> unchanged on 2026-09-26 so a cell's code can be
    /// written for one attempt (source-roots-stop-being-full-copies, slice 2).
    /// Moved here unchanged from StudySourceInstallation on 2026-09-26 (slice 4),
    /// when the install and Materialize were retired.
    ///
    /// CHG 2026-09-26 (owner: files taken from the commit are no longer checked
    /// against SHA-256): with <paramref name="commitBytesAreDeclared"/>, a file
    /// the commit holds is written as git returns it, without hashing it against
    /// the declaration. That is true of a source registered from a commit
    /// (bundle prefix source-commit-v1, registered_from commit since schema 21): its declared bytes were
    /// read from that same commit, and git returns one content per path and
    /// commit. A file the commit does not hold (a study input that was never
    /// committed) still comes from the workspace only if it matches its
    /// declaration, and without this flag everything is checked as before.
    ///
    /// CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "this is
    /// sha256 exercise, and registry should not record those too ... we delete it
    /// anyways"; "we will figure out the mb part"): no digest is compared, and the flag
    /// is gone. A file the commit holds is written as git returns it. A declared file
    /// the commit does not hold (a study input that was never committed) is taken from
    /// the workspace as it is now, without a check, and returned so the attempt names
    /// it under from_working_tree. A source registered from a working tree with
    /// uncommitted edits therefore runs its commit's version of those files.
    ///
    /// CHG 2026-09-26 (after 搬迁2号's review; the owner decides new registrations): a
    /// declaration written before schema 21 still carried each file's digest, and for it
    /// the check before that day was kept exactly: registered from a commit, the commit's
    /// file was taken as git returns it; otherwise the commit's file only if it had the
    /// declared digest, else the workspace's only if it did, else the cell was refused.
    ///
    /// CHG 2026-09-27 (owner: "fix all of them"): that search is gone. The caller has
    /// already refused a source not registered from a commit (<see cref="NotFromACommit"/>),
    /// so every file here is the commit's as git returns it, or, when the commit does not
    /// hold it (a study input never committed), the workspace's as it is now, named in the
    /// answer so the attempt records it under from_working_tree.
    /// </summary>
    internal static IReadOnlyList<string> WriteDeclaredFiles(
        string directory, string workspace, long sourceId, string commitSha, IReadOnlyList<DeclaredFile> files)
    {
        var fromWorkspace = new List<string>();
        // Every declared path is checked for shape before anything is read:
        // a path that escapes the root is refused here rather than handed to
        // git, which would answer a question about a file outside the
        // repository.
        foreach (var file in files)
        {
            SafeChildPath(directory, file.Path);
        }
        var atCommit = StudySourceRegistration.BytesAtCommit(
            workspace, commitSha, files.Select(file => file.Path).ToArray());
        foreach (var file in files)
        {
            var bytes = atCommit.GetValueOrDefault(file.Path);
            if (bytes is null)
            {
                var onDisk = Path.Combine(workspace, file.Path);
                if (!File.Exists(onDisk))
                {
                    throw new InvalidOperationException(
                        $"source {sourceId} declares {file.Path}, and neither "
                        + $"{commitSha[..Math.Min(12, commitSha.Length)]} nor this workspace holds it; "
                        + "the source was registered from a tree this machine no longer has");
                }
                bytes = File.ReadAllBytes(onDisk);
                fromWorkspace.Add(file.Path);
            }
            if (bytes is null)
            {
                throw new InvalidOperationException($"no bytes were read for {file.Path}");
            }

            var target = SafeChildPath(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        return fromWorkspace;
    }

    // CHG 2026-09-26 (cards-without-hashes): VerifyView, which re-read a written
    // directory against the declared digests and refused an undeclared file, and
    // LowerHex, which parsed a declared digest, left with the digests. A directory
    // written here, fresh and file by file, holds only the declared files.

    static string RequiredString(JsonElement value, string property, string owner)
    {
        if (!value.TryGetProperty(property, out var found)
            || found.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(found.GetString()))
        {
            throw new InvalidOperationException($"{owner} has no {property}");
        }
        return found.GetString()!;
    }

    /// <summary>
    /// A declared path is one portable relative path inside the root. A path
    /// that climbs out of it would write bytes the manifest never named.
    /// </summary>
    internal static string SafeChildPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)
            || relative.Contains('\\')
            || relative.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException($"declared source path {relative} is not a portable relative path");
        }
        var absoluteRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(absoluteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = absoluteRoot.EndsWith(Path.DirectorySeparatorChar)
            ? absoluteRoot
            : absoluteRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"declared source path {relative} escapes its source root");
        }
        return path;
    }
}
