using System.Text.RegularExpressions;

namespace Fern.Install;

/// <summary>
/// What a deployment root keeps, and what it stops keeping.
///
/// Owner, 2026-09-18: "install new must delete old, and don't store garbage."
/// Publishing a member writes <c>name.sha256</c> beside every earlier copy of
/// the same member and nothing ever removed one, so the root reached 33 GiB:
/// 122 copies of fern-run, 107 of fern-site, and 82 receipts of a service
/// retired on 2026-09-11. A content address earns its place when bytes cross a
/// machine boundary and must be proven there; keeping every copy a machine ever
/// built is accumulation, not provenance.
///
/// Two things are never removed: a file any runtime selection or installation
/// receipt names, because that is what this machine runs, and the newest copies
/// of each member, because a rollback with no bytes to roll back to is not a
/// rollback. Study sources, worker environments and result trees are not
/// touched at all — they answer to the Registry, not to this file.
/// </summary>
public static class DeployRetention
{
    /// <summary>Directories holding published <c>name.sha256</c> members, including retired ones.</summary>
    public static readonly IReadOnlyList<string> MemberDirectories =
    [
        "run-operator-binaries", "worker-site-binaries", "experiment-binaries", "stage-runner-binaries",
        "compile-binaries", "registry-binaries", "scheduler-binaries", "scheduler-worker-binaries",
    ];

    /// <summary>Receipts of the resident scheduler, retired with the Plan layer on 2026-09-11.</summary>
    public const string RetiredReceiptDirectory = "scheduler-services";

    static readonly Regex Digest = new("[0-9a-f]{64}", RegexOptions.Compiled);

    public sealed record Removal(string Path, long Bytes, string Reason);

    public sealed record Plan(IReadOnlyList<Removal> Removals, IReadOnlyList<string> Kept)
    {
        public long Bytes => Removals.Sum(removal => removal.Bytes);
    }

    /// <summary>
    /// What may go: every published copy that no selection or receipt names and
    /// that is not among the newest <paramref name="keep"/> of its member, plus
    /// the retired receipt directory. A binary's <c>.receipt.json</c> travels
    /// with it.
    /// </summary>
    /// <param name="namedElsewhere">
    /// CHG 2026-09-26 (slice F6 of how-a-site-is-reached-and-started): digests
    /// a loaded systemd unit or a Site's declaration names, kept however old.
    /// On 2026-09-26 the cleanup removed <c>fern-compile.888e83de…</c>, which the
    /// local supervisor named on its command line, and three cells failed
    /// before they ran.
    /// </param>
    public static Plan Compute(string deploymentRoot, int keep, IReadOnlySet<string>? namedElsewhere = null)
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "a member keeps at least its newest copy");
        var root = Path.GetFullPath(deploymentRoot);
        var named = NamedDigests(root);
        var removals = new List<Removal>();
        var kept = new List<string>();

        foreach (var directory in MemberDirectories)
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            var published = Directory.EnumerateFiles(path)
                .Where(file => !file.EndsWith(".receipt.json", StringComparison.Ordinal))
                .Select(file => new FileInfo(file))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            var newest = published.Take(keep).Select(file => file.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var file in published)
            {
                var digest = Digest.Match(file.Name);
                if (newest.Contains(file.FullName))
                {
                    kept.Add(file.FullName + "  (one of the newest " + keep + ")");
                    continue;
                }
                if (digest.Success && named.Contains(digest.Value))
                {
                    kept.Add(file.FullName + "  (named by a selection or receipt)");
                    continue;
                }
                if (digest.Success && namedElsewhere is not null && namedElsewhere.Contains(digest.Value))
                {
                    kept.Add(file.FullName + "  (named by a loaded unit or a Site declaration)");
                    continue;
                }
                removals.Add(new Removal(file.FullName, file.Length, "superseded copy"));
                var receipt = new FileInfo(file.FullName + ".receipt.json");
                if (receipt.Exists) removals.Add(new Removal(receipt.FullName, receipt.Length, "its receipt"));
            }
        }

        var retired = Path.Combine(root, RetiredReceiptDirectory);
        if (Directory.Exists(retired))
        {
            foreach (var file in Directory.EnumerateFiles(retired, "*", SearchOption.AllDirectories))
            {
                removals.Add(new Removal(file, new FileInfo(file).Length, "receipt of the retired resident scheduler"));
            }
        }
        return new Plan(removals, kept);
    }

    /// <summary>
    /// CHG 2026-09-26 (review of slice F6): the digests of published files this user's running
    /// processes use: each process's own executable, and every value in its environment that
    /// names a published file (a trainer calls back through <c>FERN_RUN_BIN</c>, which names the
    /// runtime selected when its supervisor started). A running supervisor two installs old keeps
    /// the binary its training still calls. Processes this user cannot read are skipped.
    /// </summary>
    public static IReadOnlySet<string> RunningDigests()
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists("/proc")) return digests;
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            if (!Path.GetFileName(process).All(char.IsDigit)) continue;
            try
            {
                var exe = new FileInfo(Path.Combine(process, "exe")).LinkTarget;
                if (exe is not null) AddDigest(digests, exe.Replace(" (deleted)", "", StringComparison.Ordinal));
                foreach (var entry in File.ReadAllText(Path.Combine(process, "environ")).Split('\0'))
                {
                    var equals = entry.IndexOf('=');
                    if (equals <= 0) continue;
                    foreach (var piece in entry[(equals + 1)..].Split(':')) AddDigest(digests, piece);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return digests;
    }

    static void AddDigest(HashSet<string> digests, string path)
    {
        var match = Digest.Match(Path.GetFileName(path));
        if (match.Success) digests.Add(match.Value);
    }

    /// <summary>Delete what the plan names, then the directories it emptied. Returns the bytes freed.</summary>
    public static long Apply(Plan plan)
    {
        var freed = 0L;
        foreach (var removal in plan.Removals)
        {
            if (!File.Exists(removal.Path)) continue;
            File.Delete(removal.Path);
            freed += removal.Bytes;
        }
        foreach (var directory in plan.Removals
            .Select(removal => Path.GetDirectoryName(removal.Path)!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(directory => directory.Length))
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        return freed;
    }

    /// <summary>
    /// Point <c>&lt;member-directory&gt;/current</c> at the newest published copy, so a
    /// command names a path a person can read instead of 64 hexadecimal characters.
    /// </summary>
    public static IReadOnlyList<string> RefreshCurrentLinks(string deploymentRoot)
    {
        var root = Path.GetFullPath(deploymentRoot);
        var written = new List<string>();
        foreach (var directory in MemberDirectories)
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            var newest = Directory.EnumerateFiles(path)
                .Where(file => !file.EndsWith(".receipt.json", StringComparison.Ordinal)
                    && !file.EndsWith("current", StringComparison.Ordinal))
                .Select(file => new FileInfo(file))
                .MaxBy(file => file.LastWriteTimeUtc);
            if (newest is null) continue;
            var link = Path.Combine(path, "current");
            if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            File.CreateSymbolicLink(link, newest.FullName);
            written.Add(link + " -> " + newest.Name);
        }
        return written;
    }

    /// <summary>Every 64-character digest any selection or installation receipt names.</summary>
    static HashSet<string> NamedDigests(string root)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in new[] { "runtime-selections", "control-plane-installations" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories))
            {
                foreach (Match match in Digest.Matches(File.ReadAllText(file))) named.Add(match.Value);
            }
        }
        return named;
    }
}
