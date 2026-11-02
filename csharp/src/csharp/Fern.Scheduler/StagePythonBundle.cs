using System.Security.Cryptography;
using System.Text;

namespace Fern.Scheduler.AttemptContracts;

/// <summary>
/// The frozen Python source bundle's content identity, ported from
/// crates/fern-artifact/src/stage_runner.rs (`stage_python_bundle_digest`,
/// `stage_implementation_digest`) and the caller that mints it,
/// fern-cli-rs/src/experiment_v2.rs (`hashed_python_source`,
/// `collect_python_source_files`).
///
/// WHAT WAS ACTUALLY MISSING. The two hashing rules were already implemented in
/// C#, inside <c>ReadyStageWorker.ObservePythonImplementation</c>, and their
/// byte construction agrees with Rust exactly. What had no C# owner is the
/// FILE SELECTION that mints a bundle from a source root: Rust keeps only
/// <c>.py</c>, <c>.pyi</c> and <c>.lock</c>, skips <c>__pycache__</c> and
/// <c>tests</c> directories, and refuses a symlink anywhere in the walk.
/// ReadyStageWorker's observer takes a DECLARED file list instead and, when
/// given none, enumerates every file with no filter at all — correct for
/// checking a worker against a frozen contract, wrong for producing one.
///
/// This type is therefore the selection rule plus a second expression of the
/// two hashes, and <c>StagePythonBundleTests</c> pins it against
/// ReadyStageWorker's implementation on real source roots so the two cannot
/// drift. Deduping them is a follow-up: the observer would need the selection
/// rule, or this would need the observer made public.
/// </summary>
public static class StagePythonBundle
{
    // The four schema constants the implementation digest binds, so a Stage
    // frozen under one protocol can never be read under another.
    const string Protocol = "fern.stage_runner.v1";
    const string RequestSchema = "fern.stage_runner_request.v1";
    const string ReportSchema = "fern.stage_runner_report.v1";
    const string CapabilitySchema = "fern.stage_runner_capability.v1";

    /// <summary>The four handlers `hashed_python_source` binds for fern_train.worker.</summary>
    public static readonly IReadOnlyList<string> FernTrainHandlers =
    [
        "serie.materialize.cell",
        "fern.train.current",
        "fern.postprocess",
        "oracle.controlled_linear.generate",
        // CHG 2026-09-04 (study chain, the formal shape); the Python HANDLERS tuple appends the same word last.
        "fern.study.run_cell",
    ];

    public abstract record BundleResult
    {
        public sealed record Minted(string BundleDigest, string ImplementationDigest) : BundleResult;
        public sealed record Refused(string Field, string Detail) : BundleResult;
        private BundleResult() { }
    }

    static string Sha256Of(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Rust `collect_python_source_files`: recurse the root, refuse a symlink,
    /// skip `__pycache__` and `tests` directories, keep only `.py`, `.pyi` and
    /// `.lock`, and name each file by its root-relative path with `/`.
    /// </summary>
    public static IReadOnlyList<(string RelativePath, string ContentDigest)> CollectSourceFiles(string sourceRoot)
    {
        var files = new List<(string, string)>();
        Walk(sourceRoot, sourceRoot, files);
        return files;
    }

    static void Walk(string root, string directory, List<(string, string)> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                throw new StageBundleRefusal($"{entry} must not be a symlink");
            }

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (name is "__pycache__" or "tests")
                {
                    continue;
                }

                Walk(root, entry, files);
                continue;
            }

            if (!File.Exists(entry))
            {
                continue;
            }

            var extension = Path.GetExtension(entry);
            if (extension is not (".py" or ".pyi" or ".lock"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            files.Add((relative, Sha256Of(File.ReadAllBytes(entry))));
        }
    }

    public sealed class StageBundleRefusal(string message) : Exception(message);

    /// <summary>
    /// Rust `stage_python_bundle_digest`. Paths are sorted, a repeat is
    /// refused, every path must be portable and relative, and the material is
    /// the domain prefix followed by NUL-separated path/digest pairs.
    /// </summary>
    public static BundleResult BundleDigest(IReadOnlyList<(string RelativePath, string ContentDigest)> files)
    {
        if (files.Count == 0)
        {
            return new BundleResult.Refused(
                "python_bundle.files", "a frozen Python bundle must name at least one file");
        }

        var ordered = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        using var material = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        material.AppendData("fern.stage.python_bundle.v1\0"u8);
        string? previous = null;
        foreach (var (relative, digest) in ordered)
        {
            if (previous == relative)
            {
                return new BundleResult.Refused(
                    "python_bundle.relative_path", $"path \"{relative}\" is repeated");
            }

            if (PortablePathRefusal(relative) is { } refusal)
            {
                return new BundleResult.Refused($"python_bundle.relative_path.{relative}", refusal);
            }

            material.AppendData(Encoding.UTF8.GetBytes(relative));
            material.AppendData([0]);
            material.AppendData(Encoding.UTF8.GetBytes(digest));
            material.AppendData([0]);
            previous = relative;
        }

        return new BundleResult.Minted(
            "sha256:" + Convert.ToHexStringLower(material.GetHashAndReset()), "");
    }

    /// Rust `require_portable_bundle_path`, with its exact three sentences.
    static string? PortablePathRefusal(string relativePath)
    {
        if (relativePath.Length == 0 || Path.IsPathRooted(relativePath))
        {
            return "must be a portable relative path";
        }

        foreach (var component in relativePath.Split('/'))
        {
            if (component == ".") return "must not contain '.' path components";
            if (component == "..") return "must not contain '..' path components";
        }

        return null;
    }

    /// <summary>
    /// Rust `stage_implementation_digest`. The entrypoint is followed by a NUL
    /// before each sorted handler and one more before the artifact digest, so
    /// the boundary between a handler list and the bundle can never be moved.
    /// </summary>
    public static string ImplementationDigest(
        string runnerKind, string entrypoint, IReadOnlyList<string> handlers, string artifactDigest)
    {
        using var material = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        material.AppendData("fern.stage.implementation.v1\0"u8);
        foreach (var value in new[] { Protocol, RequestSchema, ReportSchema, CapabilitySchema, runnerKind })
        {
            material.AppendData(Encoding.UTF8.GetBytes(value));
            material.AppendData([0]);
        }

        material.AppendData(Encoding.UTF8.GetBytes(entrypoint));
        foreach (var handler in handlers.OrderBy(handler => handler, StringComparer.Ordinal))
        {
            material.AppendData([0]);
            material.AppendData(Encoding.UTF8.GetBytes(handler));
        }

        material.AppendData([0]);
        material.AppendData(Encoding.UTF8.GetBytes(artifactDigest));
        return "sha256:" + Convert.ToHexStringLower(material.GetHashAndReset());
    }

    /// <summary>
    /// Rust `hashed_python_source`: the source root must carry
    /// python/fern_train/worker.py, and the whole selected bundle binds the
    /// fern_train.worker entrypoint and its four handlers into one
    /// implementation identity.
    /// </summary>
    public static BundleResult HashedPythonSource(string sourceRoot)
    {
        if (!File.Exists(Path.Combine(sourceRoot, "python", "fern_train", "worker.py")))
        {
            return new BundleResult.Refused(
                "python_source_root", $"python source root {sourceRoot} has no python/fern_train/worker.py");
        }

        var files = CollectSourceFiles(sourceRoot);
        if (BundleDigest(files) is not BundleResult.Minted minted)
        {
            return BundleDigest(files);
        }

        return new BundleResult.Minted(
            minted.BundleDigest,
            ImplementationDigest("python_module", "fern_train.worker", FernTrainHandlers, minted.BundleDigest));
    }
}
