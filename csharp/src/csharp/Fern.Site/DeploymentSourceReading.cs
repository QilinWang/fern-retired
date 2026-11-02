using System.Globalization;
using System.Text;

namespace Fern.Site;

/// <summary>
/// One reviewed deployment input as observed at Registry Home and at a Worker
/// Site. A null digest means that side does not have the file at all, which is
/// a different answer from having a different one.
/// </summary>
public sealed record InputComparison(string RelativePath, string? AtHome, string? AtSite)
{
    internal bool Agrees => AtHome is not null && AtSite is not null && AtHome == AtSite;

    internal string Explain() => (AtHome, AtSite) switch
    {
        (not null, null) => RelativePath + ": missing on the Worker Site",
        (null, not null) => RelativePath + ": missing here, present on the Worker Site",
        (null, null) => RelativePath + ": missing on both sides",
        _ => string.Format(
            CultureInfo.InvariantCulture,
            "{0}: differs (here {1}…, Site {2}…)",
            RelativePath,
            DigestPrefix(AtHome!),
            DigestPrefix(AtSite!)),
    };

    static string DigestPrefix(string digest) => digest.Length < 12 ? digest : digest[..12];
}

/// <summary>
/// Reads whether a Worker Site is running the source Home thinks it is, and
/// says so in words a person can act on. It answers a question; it changes
/// nothing and starts nothing.
/// </summary>
public static class DeploymentSourceReading
{
    /// <summary>
    /// The deployment diagnostic's exact watched list: the Python a Stage
    /// actually runs, and the two locks that fix the environment it runs in.
    ///
    /// CHG 2026-08-28: `Cargo.lock` left this list. It was here for a real
    /// reason — it rebuilt the Rust worker without changing the numerical model
    /// implementation, and a Site running a different worker binary is still a
    /// different Site. Nothing compiles on a Site any more: a Site receives
    /// published C# binaries and a .NET runtime, so a Rust lock is not an input
    /// to anything it executes. The file itself is gone from the tree
    /// (retired/cargo-workspace-2026-08-28/Cargo.lock is a witness, not an
    /// input), and `site register` must read only what is there.
    ///
    /// Nothing replaced it, deliberately. What determines the shipped C#
    /// binaries is the installed binary-set digest in the installation receipt,
    /// which every `serve` verifies — the BYTES, not the inputs that produced
    /// them. Adding csproj or lock files here would be a second, weaker
    /// identity for a thing already identified exactly.
    ///
    /// This list was checked against three constants in the Rust supervisor;
    /// that supervisor retired, and the check now reads the same constants from
    /// its dated shelf copy.
    /// </summary>
    // CHG 2026-09-03: the training package moved from the repository root into
    // python/fern_train, route step 9; this list carries the moved paths.
    // CHG 2026-09-26: the Serie package moved from serie/ to src/serie/.
    public static IReadOnlyList<string> ReviewedDeploymentInputs { get; } =
    [
        "python/fern_stage_runtime/__init__.py",
        "python/fern_train/cell.py",
        "python/fern_train/components/action.py",
        "python/fern_train/components/core.py",
        "python/fern_train/components/density.py",
        "python/fern_train/components/__init__.py",
        "python/fern_train/config.py",
        "python/fern_train/data.py",
        "python/fern_train/__init__.py",
        "python/fern_train/loss.py",
        "python/fern_train/model.py",
        "python/fern_train/probes.py",
        "python/fern_train/specs.py",
        "python/fern_train/study.py",
        "python/fern_train/train.py",
        "python/fern_train/worker.py",
        "python/fern_train/zero_shot.py",
        "src/serie/api.py",
        "src/serie/controlled.py",
        "src/serie/counterfactual.py",
        "src/serie/__init__.py",
        "src/serie/noise.py",
        "src/serie/pipeline.py",
        "src/serie/specs.py",
        "src/serie/worlds/factory.py",
        "src/serie/worlds/__init__.py",
        "uv.lock",
        "src/serie/uv.lock",
    ];

    /// <summary>
    /// sha256sum-compatible digests for the requested files that exist below
    /// root. A missing or unreadable file stays ABSENT rather than becoming an
    /// empty digest, so the comparison can name which side is missing it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LocalDigests(string root, IReadOnlyList<string> inputs)
    {
        var digests = new Dictionary<string, string>(inputs.Count, StringComparer.Ordinal);
        foreach (var relativePath in inputs)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                digests[relativePath] = StageAttemptSite.Sha256Hex(File.ReadAllBytes(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Absent, unreadable, or a directory: all three mean this side
                // does not have the file, which Explain() reports by name.
            }
        }
        return digests;
    }

    /// <summary>Every reviewed input, in declaration order, with both sides' readings.</summary>
    public static IReadOnlyList<InputComparison> CompareSource(
        IReadOnlyList<string> inputs,
        IReadOnlyDictionary<string, string> atHome,
        IReadOnlyDictionary<string, string> atSite) =>
        inputs
            .Select(relativePath => new InputComparison(
                relativePath,
                atHome.TryGetValue(relativePath, out var home) ? home : null,
                atSite.TryGetValue(relativePath, out var site) ? site : null))
            .ToList();

    /// <summary>The actionable source-parity answer for one Site, as a person reads it.</summary>
    public static string DescribeSource(string profileId, IReadOnlyList<InputComparison> comparison)
    {
        var disagreeing = comparison.Where(input => !input.Agrees).ToList();
        if (disagreeing.Count == 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}: in sync — all {1} reviewed inputs match this checkout",
                profileId,
                comparison.Count);
        }
        var text = new StringBuilder();
        text.AppendFormat(
            CultureInfo.InvariantCulture,
            "{0}: STALE — {1} of {2} deployment inputs disagree; a new launch must not assume this checkout is "
            + "what the Site will execute:",
            profileId,
            disagreeing.Count,
            comparison.Count);
        foreach (var input in disagreeing)
        {
            text.Append("\n  ").Append(input.Explain());
        }
        text.Append(
            "\n  Build and sync a new immutable Site deployment, verify it, then point future plans at that "
            + "revision.");
        return text.ToString();
    }

    /// <summary>
    /// Local edits, appearances and deletions between two readings, each NAMED
    /// rather than counted, because "three files changed" is not something a
    /// person can act on.
    /// </summary>
    public static IReadOnlyList<string> ChangedInputs(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changed = new List<string>();
        foreach (var relativePath in after.Keys.OrderBy(path => path, StageAttemptSite.Utf8Ordinal))
        {
            if (!before.TryGetValue(relativePath, out var previous))
            {
                changed.Add(relativePath + " (appeared)");
            }
            else if (previous != after[relativePath])
            {
                changed.Add(relativePath);
            }
        }
        foreach (var relativePath in before.Keys.OrderBy(path => path, StageAttemptSite.Utf8Ordinal))
        {
            if (!after.ContainsKey(relativePath))
            {
                changed.Add(relativePath + " (deleted)");
            }
        }
        return changed;
    }
}
