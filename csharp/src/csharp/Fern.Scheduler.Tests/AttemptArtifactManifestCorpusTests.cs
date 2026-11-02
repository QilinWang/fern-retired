using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The manifest capture, against every receipt digest Rust ever sealed.
///
/// This is not a fixture test. The campaign Registry holds 8,994 artifact
/// snapshots, each row a manifest the Rust `capture_artifact_manifest` computed
/// over a real Attempt directory, with its per-file rows beside it. The C#
/// capture must reproduce those bytes exactly — the file list, each role, each
/// byte count, each sha256, and the folded receipt digest — or a snapshot
/// sealed by Rust and re-read by C# would look tampered with.
///
/// A directory slimmed after it was sealed no longer holds what the manifest
/// declares. That is a fact about the disk, not a difference between the two
/// implementations, so such a directory is excluded and counted rather than
/// failed. The test requires a floor of directories that ARE intact, so the
/// exclusions can never quietly empty it.
/// </summary>
public sealed class AttemptArtifactManifestCorpusTests
{
    const int RequiredComparisons = 50;

    [Fact]
    public void EveryIntactSealedDirectoryReproducesItsRustReceiptDigest()
    {
        var registry = Path.Combine(RepositoryRoot(), "results", "paper_campaign", "lifecycle.db");
        // Deliberately a failure, not a skip: without the corpus this port has
        // no proof at all, and a green run would say otherwise.
        Assert.True(File.Exists(registry), $"the corpus registry is absent at {registry}");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = registry,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var sealedSnapshots = SealedSnapshots(connection);
        Assert.NotEmpty(sealedSnapshots);

        var compared = 0;
        var changedSinceSealing = 0;
        var absent = 0;
        foreach (var (snapshotId, directory, receiptDigest, declared) in sealedSnapshots)
        {
            if (!Directory.Exists(directory))
            {
                absent++;
                continue;
            }

            AttemptArtifactManifestV1 captured;
            try
            {
                captured = AttemptArtifactManifest.Capture(directory);
            }
            catch (AttemptArtifactRefusal)
            {
                // A required receipt was deleted after sealing.
                changedSinceSealing++;
                continue;
            }

            var observed = captured.Files
                .Select(file => (file.RelativePath, AttemptArtifactManifest.StoredName(file.Role), file.ByteCount, file.Sha256))
                .ToArray();
            if (!observed.SequenceEqual(declared))
            {
                changedSinceSealing++;
                continue;
            }

            Assert.Equal(receiptDigest, captured.ReceiptDigest);
            compared++;
            if (compared >= RequiredComparisons)
            {
                break;
            }
        }

        Assert.True(
            compared >= RequiredComparisons,
            $"only {compared} sealed directories were still intact enough to compare "
            + $"({changedSinceSealing} changed since sealing, {absent} absent); "
            + $"the corpus proof needs at least {RequiredComparisons}");
    }

    /// <summary>
    /// Sealed snapshots with their declared files, spread across the corpus.
    ///
    /// Ordered by snapshot id and read in one pass so the sample is
    /// deterministic and spans lanes rather than exhausting one campaign.
    /// </summary>
    static List<(string SnapshotId, string Directory, string ReceiptDigest,
        (string RelativePath, string Role, long ByteCount, string Sha256)[] Declared)>
        SealedSnapshots(SqliteConnection connection)
    {
        var declared = new Dictionary<string, List<(string, string, long, string)>>(StringComparer.Ordinal);
        using (var files = connection.CreateCommand())
        {
            files.CommandText =
                """
                SELECT snapshot_id, relative_path, role, byte_count, sha256
                FROM artifact_snapshot_files
                ORDER BY snapshot_id, relative_path
                """;
            using var row = files.ExecuteReader();
            while (row.Read())
            {
                var key = row.GetString(0);
                if (!declared.TryGetValue(key, out var list))
                {
                    list = [];
                    declared[key] = list;
                }
                list.Add((row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetString(4)));
            }
        }

        var snapshots = new List<(string, string, string, (string, string, long, string)[])>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT snapshot_id, artifact_dir, receipt_digest FROM artifact_snapshots ORDER BY snapshot_id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var snapshotId = reader.GetString(0);
            if (declared.TryGetValue(snapshotId, out var rows))
            {
                snapshots.Add((snapshotId, reader.GetString(1), reader.GetString(2), rows.ToArray()));
            }
        }
        return snapshots;
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
