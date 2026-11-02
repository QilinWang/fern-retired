using System.Text;
using System.Text.Json;

namespace Fern.Experiment.Tests;

/// <summary>
/// Reads active, implementation-independent Fern contract fixtures.
/// Missing inputs fail because a missing contract witness is not a passing test.
/// </summary>
public static class ContractFixtures
{
    public static string Read(string name) => ReadFrom("experiment", name);

    public static byte[] Bytes(string name) => Encoding.UTF8.GetBytes(Read(name));

    public static JsonDocument Parse(string name) => JsonDocument.Parse(Read(name));

    /// <summary>The path of an experiment contract fixture, for the explicit golden recapture only (FERN_RECAPTURE_GOLDEN).</summary>
    public static string PathOf(string name) => RequiredPath("experiment", name);

    public static string ReadRun(string name) => ReadFrom("run", name);

    public static JsonDocument ParseRun(string name) => JsonDocument.Parse(ReadRun(name));

    public static byte[] ArtifactBytes(string name) =>
        File.ReadAllBytes(RequiredPath("artifact", name));

    /// <summary>
    /// The corpus example of one model family.
    ///
    /// CHG 2026-09-11, the vocabulary cut: the corpora moved out of
    /// <c>specs/training/fern/&lt;version&gt;/examples/</c> to
    /// <c>tests/fixtures/training-configurations/</c> when the TypeSpec chain
    /// retired. They are test fixtures now, not the input half of a generator:
    /// the Go compiler owns the vocabulary and checks itself against them.
    /// </summary>
    public static string CorpusExamplePath(string version, string model) =>
        Path.Combine(RepositoryFiles.Root(), "tests", "fixtures", "training-configurations",
            version, "valid", model + ".json");

    static string ReadFrom(string group, string name) =>
        File.ReadAllText(RequiredPath(group, name));

    static string RequiredPath(string group, string name)
    {
        if (Path.IsPathRooted(name)
            || name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "contract fixture name must be relative without parent traversal",
                nameof(name));
        }

        var path = Path.Combine(
            RepositoryFiles.Root(), "src", "csharp", "Fern.Experiment.Tests", "Fixtures", "contracts", group, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{path} is missing. The current Fern contract is unproved without this fixture.",
                path);
        }
        return path;
    }
}

/// <summary>Reads files owned by the current checkout; it never falls back to retired source or Git history.</summary>
public static class RepositoryFiles
{
    public static string ReadText(string relativePath) => File.ReadAllText(Path(relativePath));

    public static byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Path(relativePath));

    public static string Path(string relativePath)
    {
        if (System.IO.Path.IsPathRooted(relativePath)
            || relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "repository path must be relative without parent traversal",
                nameof(relativePath));
        }

        var path = System.IO.Path.Combine(Root(), relativePath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException(
                $"{path} is missing from the current checkout; active tests do not read retired fallbacks.",
                path);
        }
        return path;
    }

    internal static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(sourceFile)!);
        while (directory is not null
            && !File.Exists(System.IO.Path.Combine(directory.FullName, "src", "csharp", "Fern.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }
}
