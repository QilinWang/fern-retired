using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>Reads immutable scheduler contract fixtures and never falls back to retired source.</summary>
static class ContractWitness
{
    public static string Text(string name) => File.ReadAllText(PathOf(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

    static string PathOf(string name)
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
            TestRegistry.RepoRoot(), "src", "csharp", "Fern.Scheduler.Tests", "Fixtures", "contracts", name);
        Assert.True(File.Exists(path), $"the scheduler contract fixture is missing: {path}");
        return path;
    }
}
