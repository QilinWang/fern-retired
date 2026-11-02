using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fern.Install;

/// <summary>Which toolchain builds a member.</summary>
/// <summary>CHG 2026-09-27: Gleam replaces Go. The one Gleam member is packed as an escript, one file, so it publishes like the others.</summary>
public enum ControlMemberKind { Dotnet, Gleam }

/// <summary>
/// One executable the control plane publishes: what it is built from and
/// where its content-addressed copies live under the deployment root.
/// </summary>
/// <param name="Name">The executable's name, which is the binary name the build inventory records.</param>
/// <param name="Kind">The toolchain that builds it.</param>
/// <param name="Project">Repository-relative: the C# project file, or the Go module directory.</param>
/// <param name="PublishDirectory">The directory under the deployment root holding <c>&lt;name&gt;.&lt;sha256&gt;</c> copies.</param>
/// <param name="Trim">Whether the .NET publish trims (no member does today; see the notes at each build site).</param>
public sealed record ControlMember(string Name, ControlMemberKind Kind, string Project, string PublishDirectory, bool Trim)
{
    public string PublishPrefix => Name + ".";
}

/// <summary>
/// The control plane's members and the inputs each is built from.
///
/// CHG 2026-09-11 night (slice 10 of the cell port): an installed member used
/// to carry one identity, the commit, and a commit changes when any file
/// changes, so every install rebuilt every member and a one-line Registry
/// change cost five publishes. A member's identity is now the digest of its
/// own inputs: the committed blobs under its project and the projects it
/// references, transitively; the root build inputs every C# project imports;
/// the publish recipe; and the toolchain version. A published member whose
/// receipt carries the same input digest is the same build and is reused;
/// only the members whose inputs changed are built. The closure is read from
/// the project files themselves (ProjectReference, and what the shared
/// Directory.Build.props imports into a named project), not from a table
/// that would go stale when a reference is added.
/// </summary>
public static class ControlMembers
{
    public static readonly IReadOnlyList<ControlMember> All =
    [
        new("fern-site", ControlMemberKind.Dotnet, CommittedControlSource.CSharpPrefix + "Fern.Site.Cli/Fern.Site.Cli.csproj", "worker-site-binaries", Trim: false),
        new("fern-experiment", ControlMemberKind.Dotnet, CommittedControlSource.CSharpPrefix + "Fern.Experiment.Cli/Fern.Experiment.Cli.csproj", "experiment-binaries", Trim: false),
        new("fern-run", ControlMemberKind.Dotnet, CommittedControlSource.CSharpPrefix + "Fern.Run.Cli/Fern.Run.Cli.csproj", "run-operator-binaries", Trim: false),
        // CHG 2026-09-27 (owner: keep Gleam, retire Go): the card compiler is the
        // Gleam program, packed as one escript by `gleam run -m export_executable`.
        new("fern-compile", ControlMemberKind.Gleam, "tools/fern-compile-gleam", "compile-binaries", Trim: false),
        new("fern-registry", ControlMemberKind.Dotnet, CommittedControlSource.CSharpPrefix + "Fern.Registry.Cli/Fern.Registry.Cli.csproj", "registry-binaries", Trim: false),
    ];

    /// <summary>
    /// CHG 2026-09-14: fern-stage-runner left. Its two verbs, `probe` and
    /// `check-return`, were served by the Rust-era stage contracts, and those
    /// retired with the plan layer they belonged to
    /// (retired/csharp/rust-era-experiment-types-2026-09-14/). A cell's result
    /// is read by the worker that ran it.
    ///
    /// The four members one control-plane installation activates as a set.
    /// fern-registry is published by <c>publish-registry-binary</c> and chosen
    /// by <c>select-runtime</c>; it shares the members' input identity and
    /// reuse but not the set.
    /// </summary>
    public static IReadOnlyList<ControlMember> InstalledSet => All.Where(member => member.Name != "fern-registry").ToList();

    public static ControlMember Named(string name) =>
        All.FirstOrDefault(member => member.Name == name)
        ?? throw new InvalidOperationException(
            $"{name} is not a control-plane member; the members are {string.Join(", ", All.Select(member => member.Name))}");

    /// <summary>
    /// The fixed part of a member's build command: everything but the paths
    /// of one build and the provenance stamps. It is part of the input digest,
    /// so a changed recipe is a changed member, and the build sites use this
    /// same list, so the recipe recorded is the recipe run.
    /// </summary>
    public static IReadOnlyList<string> Recipe(ControlMember member) => member.Kind switch
    {
        ControlMemberKind.Gleam => ["gleam export erlang-shipment", "gleam run -m export_executable"],
        _ => DotnetRecipe(member.Trim),
    };

    static IReadOnlyList<string> DotnetRecipe(bool trim)
    {
        var arguments = new List<string>
        {
            "publish", "--disable-build-servers", "--nologo", "-c", "Release", "-r", "linux-x64", "--self-contained", "true",
            // The installer publishes ONE file per member, content-addressed,
            // and copies nothing beside it. Without this the scheduler's
            // e_sqlite3.so is left in the publish directory and thrown away, so
            // an installed scheduler dies on its first registry open with
            // "Unable to load shared library 'e_sqlite3'". Every installed
            // scheduler under deploy/scheduler-binaries/ has that defect;
            // nothing caught it because the installed binary was never the one
            // anybody ran. Self-extract keeps the one-file contract and makes
            // the native library part of it.
            "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
        };
        if (trim) arguments.Add("-p:PublishTrimmed=true");
        return arguments;
    }

    /// <summary>
    /// The toolchain versions the digests name, each observed once per
    /// operation and only when a member of that kind asks: the same inputs
    /// under another compiler are other bytes.
    /// </summary>
    public sealed class Toolchains(string workingDirectory)
    {
        readonly Dictionary<ControlMemberKind, string> observed = [];

        public string For(ControlMemberKind kind)
        {
            if (!observed.TryGetValue(kind, out var version))
            {
                // CHG 2026-09-27: a Gleam member's toolchain is gleam and the Erlang release its escript runs on.
                version = kind == ControlMemberKind.Gleam
                    ? Observe("gleam", ["--version"], workingDirectory) + " on OTP "
                      + Observe("erl", ["-noshell", "-eval", "io:put_chars(erlang:system_info(otp_release)), halt()."], workingDirectory)
                    : "dotnet " + Observe("dotnet", ["--version"], workingDirectory);
                observed[kind] = version;
            }
            return version;
        }

        static string Observe(string program, IReadOnlyList<string> arguments, string workingDirectory)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(program)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception missing)
            {
                throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} could not start: {program} is not on this machine's PATH ({missing.Message})");
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} exceeded its one-minute deadline");
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} failed with exit status {process.ExitCode}: {stderr.Result.Trim()}");
            }
            return stdout.Result.Trim();
        }
    }

    /// <summary>
    /// The repository-relative paths a member is built from, sorted. A Go
    /// member is its module. A .NET member is its project directory and the
    /// directories of every project it references, transitively, through
    /// <c>ProjectReference</c> items in the project files and through the
    /// items the shared <c>Directory.Build.props</c> gives it (an analyzer
    /// project and its additional files, when the props file names this
    /// project or names none); plus the root build inputs: every archived
    /// file at the repository root and directly under <c>src/csharp/</c>
    /// (CHG 2026-09-26: was <c>csharp/</c> before the tree moved).
    /// CHG 2026-09-26: a declared item outside src/csharp/ and tools/fern-compile/
    /// is refused by name (RequireBuildRoot) instead of dropped from the closure.
    /// </summary>
    internal static IReadOnlyList<string> InputClosure(CommittedControlSource source, ControlMember member)
    {
        var files = source.Files;
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        if (member.Kind == ControlMemberKind.Gleam)
        {
            prefixes.Add(member.Project.TrimEnd('/') + "/");
        }
        else
        {
            foreach (var path in files.Keys)
            {
                // CHG 2026-09-26: the C# directory is src/csharp/ (CommittedControlSource.CSharpPrefix).
                if (!path.Contains('/') || (path.StartsWith(CommittedControlSource.CSharpPrefix, StringComparison.Ordinal)
                    && path.IndexOf('/', CommittedControlSource.CSharpPrefix.Length) < 0))
                {
                    if (IsBuildInput(path)) named.Add(path);
                }
            }
            var pending = new Stack<string>();
            pending.Push(member.Project);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var project = pending.Pop();
                if (!visited.Add(project)) continue;
                if (!files.ContainsKey(project))
                {
                    throw new InvalidOperationException($"{member.Name} references project {project}, which the committed source does not carry");
                }
                prefixes.Add(project[..(project.LastIndexOf('/') + 1)]);
                foreach (var referenced in ProjectReferences(source, project)) pending.Push(referenced);
                var projectName = Path.GetFileNameWithoutExtension(project);
                foreach (var props in named.Where(path => path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    foreach (var imported in SharedImports(source, props, projectName))
                    {
                        if (imported.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) pending.Push(imported);
                        else named.Add(imported);
                    }
                }
            }
        }
        return files.Keys
            .Where(path => named.Contains(path) || prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether a file at the repository root or directly under <c>src/csharp/</c>
    /// (CHG 2026-09-26: was <c>csharp/</c>) is an input of every project: the imported build properties, the SDK
    /// pin, the package sources and the analyzer settings.
    ///
    /// CHG 2026-09-12: not everything that sits there. The first version took
    /// every file at those two levels, which put <c>csharp/Fern.sln</c> in
    /// every member's closure — and `dotnet publish` is given a project, never
    /// the solution, so a lane adding an unrelated project rebuilt all five
    /// members. Observed the same day: a Registry-only commit rebuilt
    /// fern-site, fern-stage-runner and fern-experiment because another lane
    /// had added the dashboard projects to the solution.
    /// </summary>
    static bool IsBuildInput(string path) =>
        path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The member's identity for reuse: <c>sha256:</c> over its name, its
    /// recipe, the toolchain, and every input path with its Git blob id (a
    /// worktree build names a changed file by its content instead; such a
    /// build is never reused, its receipt says it was dirty).
    /// </summary>
    internal static string InputDigest(CommittedControlSource source, ControlMember member, string toolchain)
    {
        var material = new StringBuilder();
        material.Append("member ").Append(member.Name).Append('\n');
        material.Append("recipe ").Append(string.Join(' ', Recipe(member))).Append('\n');
        material.Append("toolchain ").Append(toolchain).Append('\n');
        foreach (var path in InputClosure(source, member))
        {
            material.Append(path).Append(' ').Append(source.Files[path]).Append('\n');
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    static readonly Regex IncludeItem = new(
        "<(ProjectReference|AdditionalFiles)\\s[^>]*?Include=\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ItemGroup = new(
        "<ItemGroup(?<attributes>[^>]*)>(?<body>.*?)</ItemGroup>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ProjectNameCondition = new(
        "'\\$\\(MSBuildProjectName\\)'\\s*==\\s*'([^']+)'",
        RegexOptions.Compiled);

    /// <summary>The projects a project file references, as repository-relative paths.</summary>
    /// <remarks>
    /// CHG 2026-09-26: every item the project declares, additional files included,
    /// is also checked against the build roots (RequireBuildRoot), so a file the
    /// installer does not archive is refused by name rather than left out.
    /// </remarks>
    static IReadOnlyList<string> ProjectReferences(CommittedControlSource source, string project)
    {
        var text = File.ReadAllText(Path.Combine(source.Root, project));
        var directory = project[..(project.LastIndexOf('/') + 1)];
        var name = Path.GetFileNameWithoutExtension(project);
        var references = new List<string>();
        foreach (Match match in IncludeItem.Matches(text))
        {
            var declared = RequireBuildRoot(Resolve(match.Groups[2].Value, directory, directory), name, project);
            if (match.Groups[1].Value == "ProjectReference") references.Add(declared);
        }
        return references;
    }

    /// <summary>
    /// The project references and additional files a shared props file gives
    /// the project named: the items of every ItemGroup whose condition names
    /// it, or that has no project-name condition at all.
    /// </summary>
    static IReadOnlyList<string> SharedImports(CommittedControlSource source, string props, string projectName)
    {
        var text = File.ReadAllText(Path.Combine(source.Root, props));
        var directory = props[..(props.LastIndexOf('/') + 1)];
        var imports = new List<string>();
        foreach (Match group in ItemGroup.Matches(text))
        {
            var names = ProjectNameCondition.Matches(group.Groups["attributes"].Value).Select(match => match.Groups[1].Value).ToList();
            if (names.Count > 0 && !names.Contains(projectName, StringComparer.Ordinal)) continue;
            foreach (Match item in IncludeItem.Matches(group.Groups["body"].Value))
            {
                imports.Add(RequireBuildRoot(Resolve(item.Groups[2].Value, directory, directory), projectName, props));
            }
        }
        return imports;
    }

    /// <summary>
    /// A declared build input, refused by name when it lies outside the two
    /// roots the installer archives.
    ///
    /// CHG 2026-09-26: the installer archives only files under src/csharp/ and
    /// tools/fern-compile/ (and the root build files), so a project that declared
    /// a file anywhere else, as an AdditionalFiles item say, had it silently left
    /// out of the archive, of the input closure and of the check for uncommitted
    /// edits, while the build itself still read it from the working tree. Since
    /// the workflow documents in docs/how-to/ stopped being inputs, nothing
    /// legitimate lies outside those roots, so such a declaration is refused.
    /// </summary>
    static string RequireBuildRoot(string declared, string project, string declaredIn)
    {
        if (declared.StartsWith(CommittedControlSource.CSharpPrefix, StringComparison.Ordinal)
            || declared.StartsWith(CommittedControlSource.CompilerPrefix, StringComparison.Ordinal)) return declared;
        throw new InvalidOperationException(
            $"project {project} declares {declared} (in {declaredIn}); a build input must live under "
            + $"{CommittedControlSource.CSharpPrefix} or {CommittedControlSource.CompilerPrefix}");
    }

    /// <summary>
    /// An MSBuild Include to a repository-relative path: backslashes are
    /// separators, <c>$(MSBuildThisFileDirectory)</c> is the file's own
    /// directory, and a relative path is taken from the referencing file's
    /// directory with <c>..</c> and <c>.</c> segments folded.
    /// </summary>
    static string Resolve(string include, string thisFileDirectory, string relativeTo)
    {
        var path = include.Replace('\\', '/').Replace("$(MSBuildThisFileDirectory)", thisFileDirectory, StringComparison.Ordinal);
        if (path.Contains("$(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"build input {include} uses an MSBuild property the installer does not resolve");
        }
        var combined = path.StartsWith(thisFileDirectory, StringComparison.Ordinal) ? path : relativeTo + path;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0) throw new InvalidOperationException($"build input {include} leaves the repository");
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }
        return string.Join('/', segments);
    }
}

/// <summary>
/// The receipt beside a published member: which inputs built these bytes.
/// Written once, when the content-addressed copy is first published; a later
/// publish of the same bytes leaves it, so the receipt is the first
/// publisher's account. <c>publish-registry-binary</c> keeps its own richer
/// receipt for fern-registry and carries the same <c>input_digest</c>.
/// </summary>
public sealed record ControlMemberPublishReceipt(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("published_path")] string PublishedPath,
    [property: JsonPropertyName("byte_size")] long ByteSize,
    [property: JsonPropertyName("input_digest")] string InputDigest,
    [property: JsonPropertyName("built_from_commit")] string BuiltFromCommit,
    [property: JsonPropertyName("built_from_dirty_tree")] bool BuiltFromDirtyTree,
    [property: JsonPropertyName("source_archive_sha256")] string? SourceArchiveSha256,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("toolchain")] string Toolchain,
    [property: JsonPropertyName("published_at")] string PublishedAt,
    [property: JsonPropertyName("publisher")] string Publisher);

public static class ControlMemberPublications
{
    public const string ReceiptSchema = "fern.control-member-publish.v1";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ReceiptPath(string publishedPath) => publishedPath + ".receipt.json";

    /// <summary>
    /// A published copy of the member built from exactly these inputs, or
    /// null. Only a receipt from a committed build counts, and only when the
    /// bytes beside it still hash to what it says; the newest such publish
    /// wins when several do.
    /// </summary>
    public static (string Path, string Sha256)? Find(string deploymentRoot, ControlMember member, string inputDigest)
    {
        var directory = Path.Combine(deploymentRoot, member.PublishDirectory);
        if (!Directory.Exists(directory)) return null;
        var candidates = new List<(string PublishedAt, string Path, string Sha256)>();
        foreach (var receiptPath in Directory.EnumerateFiles(directory, member.PublishPrefix + "*.receipt.json"))
        {
            ControlMemberPublishReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<ControlMemberPublishReceipt>(File.ReadAllBytes(receiptPath));
            }
            catch (JsonException)
            {
                continue;
            }
            if (receipt is null || receipt.Schema != ReceiptSchema || receipt.Member != member.Name
                || receipt.InputDigest != inputDigest || receipt.BuiltFromDirtyTree)
            {
                continue;
            }
            var binary = receiptPath[..^".receipt.json".Length];
            if (!File.Exists(binary) || DurableFiles.FileSha256(binary) != receipt.Sha256) continue;
            candidates.Add((receipt.PublishedAt, binary, receipt.Sha256));
        }
        if (candidates.Count == 0) return null;
        var newest = candidates.OrderByDescending(candidate => candidate.PublishedAt, StringComparer.Ordinal).First();
        return (newest.Path, newest.Sha256);
    }

    /// <summary>Write the receipt beside a published copy unless one is there already.</summary>
    internal static bool WriteReceipt(
        string publishedPath, ControlMember member, string sha256, string inputDigest,
        CommittedControlSource source, string toolchain, string publisher)
    {
        var receiptPath = ReceiptPath(publishedPath);
        if (File.Exists(receiptPath)) return false;
        var receipt = new ControlMemberPublishReceipt(
            ReceiptSchema, member.Name, sha256, publishedPath, new FileInfo(publishedPath).Length, inputDigest,
            source.Commit, source.Dirty, source.Dirty ? null : source.ArchiveSha256,
            string.Join(' ', ControlMembers.Recipe(member)), toolchain,
            DateTimeOffset.UtcNow.ToString("O"), publisher);
        var staging = receiptPath + ".publish-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(receipt, Json) + "\n");
            File.Move(staging, receiptPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(receiptPath))
        {
            // Another publish of the same bytes wrote it first; theirs stands.
            return false;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
