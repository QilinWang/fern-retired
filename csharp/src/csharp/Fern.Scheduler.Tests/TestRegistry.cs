using Fern.Registry;

namespace Fern.Scheduler.Tests;

static class TestRegistry
{
    /// <summary>
    /// A directory that exists, for a fixture whose Site holds an installed
    /// source. CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): a
    /// supervisor looks for its own Site's installed directories, and one that
    /// is not there is not held, so a made-up path no longer passes for one.
    /// </summary>
    public static string HeldSourceDirectory(string name) =>
        Directory.CreateTempSubdirectory($"fern-held-{name}-").FullName;

    /// <summary>
    /// Declare that a fixture Site takes each cell's code from a repository, which
    /// a claim now requires. CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    /// slice 4): it replaces recording an installed source for the Site, which is retired.
    /// The repository need not exist for a claim; a worker that makes the code looks at it.
    /// </summary>
    public static void DeclareCodeFrom(RegistryWriter writer, long siteId, string codeFrom = "working-tree", string repository = "/repository")
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "UPDATE sites SET capabilities_json = json_set(capabilities_json, '$.code_from', $from, '$.repository', $repository) WHERE id = $id";
        command.Parameters.AddWithValue("$from", codeFrom);
        command.Parameters.AddWithValue("$repository", repository);
        command.Parameters.AddWithValue("$id", siteId);
        Xunit.Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// Run git in a fixture repository and return its output. Moved here from
    /// StudySourceInstallationTests.Home on 2026-09-26, when installed sources
    /// and their tests were retired (source-roots-stop-being-full-copies, slice 4).
    /// </summary>
    internal static string Git(string workspace, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Xunit.Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {errors}");
        return output;
    }

    public static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.Registry", "Schema.sql")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("cannot locate the Fern repository root from the test output");
    }

    /// <summary>Initialize a fresh fixture with the same current schema carried by the installed control set.</summary>
    public static string CreateRegistry()
    {
        var directory = Directory.CreateTempSubdirectory("fern-scheduler-cs-");
        var registry = Path.Combine(directory.FullName, "lifecycle.db");
        var initialized = RegistrySchema.Initialize(registry);
        if (initialized is RegistryInitializationOutcome.Refused refused)
            throw new InvalidOperationException($"C# fixture initialization refused: {refused.Detail}");

        // Close the non-pooled fixture capabilities before a caller copies the
        // main file; rows left only in an open WAL are not a database snapshot.
        using (var reader = RegistryHome.OpenReader(registry))
        {
            if (RegistrySchema.ReadIdentity(reader.Connection) != EmbeddedRegistrySchema.Identity)
                throw new InvalidOperationException("C# fixture initialization returned a different schema identity");
            // CHG 2026-09-11 night (slice 8): the office is the Site's supervisor row now.
            using var leases = reader.Connection.CreateCommand();
            leases.CommandText = "SELECT COUNT(*) FROM supervisors";
            if (Convert.ToInt64(leases.ExecuteScalar()) != 0)
                throw new InvalidOperationException("C# fixture initialization wrote an unrelated supervisor office");
        }
        return registry;
    }
}
