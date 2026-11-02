using Fern.Registry;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Fern.Install;

public sealed record BinarySetMember(string BinaryName, string Sha256, string Path);

public sealed record BinaryCandidate(string BinaryName, string Sha256, string Path, string SourceRevision, long BuiltAtMs);

public sealed record BinarySet(string SetId, string SourceRevision, long InstalledAtMs);

public sealed record ActiveBinaryMember(
    string SetId,
    string SourceRevision,
    long InstalledAtMs,
    string BinaryName,
    string Sha256,
    string Path);

public union BinarySetIdentity(Accepted<string>, Refused);

public union BinarySetActivation(Accepted<BinarySet>, Refused);

public union ActiveBinaryResolution(Accepted<ActiveBinaryMember>, Refused);

/// <summary>
/// Records produced executable bytes separately from the one complete set an
/// installer has selected (Go: package buildinventory). A build is evidence,
/// not activation: only Activate changes what an operator may resolve. This
/// is the installer's own SQLite ledger, never the Registry Home; it holds
/// no scientific fact and its schema is created by this class alone.
/// </summary>
public sealed class BuildInventory : IDisposable
{
    public const string FileName = "build-inventory.db";

    const string SetIdPreimageHeader = "fern.installed-binary-set.v1";

    readonly SqliteConnection connection;

    BuildInventory(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public static BuildInventory Open(string path)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = absolute,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        // The Go inventory applies these as open pragmas on every connection so
        // the first schema read cannot race another installer process; the
        // 60 s busy timeout is the same measured value.
        var opened = new SqliteConnection(builder.ConnectionString);
        opened.Open();
        using (var pragma = opened.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 60000; PRAGMA foreign_keys = 1;";
            pragma.ExecuteNonQuery();
        }

        var inventory = new BuildInventory(opened);
        inventory.CreateSchema();
        return inventory;
    }

    public void Dispose() => connection.Dispose();

    void CreateSchema()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS binary_builds (
                binary_name TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                path TEXT NOT NULL,
                source_revision TEXT NOT NULL,
                built_at_ms INTEGER NOT NULL,
                PRIMARY KEY (binary_name, sha256, path)
            );
            CREATE TABLE IF NOT EXISTS binary_sets (
                set_id TEXT PRIMARY KEY,
                source_revision TEXT NOT NULL,
                installed_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS binary_set_members (
                set_id TEXT NOT NULL REFERENCES binary_sets(set_id),
                binary_name TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                path TEXT NOT NULL,
                PRIMARY KEY (set_id, binary_name)
            );
            CREATE TABLE IF NOT EXISTS active_binary_set (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                set_id TEXT NOT NULL REFERENCES binary_sets(set_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    public BinarySetActivation Activate(string sourceRevision, IReadOnlyList<BinarySetMember> members)
    {
        if (sourceRevision.Length == 0)
        {
            sourceRevision = "unknown";
        }

        if (members.Count == 0)
        {
            return new Refused("a binary set needs at least one member");
        }

        var verified = new List<BinarySetMember>(members.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (ValidateBinaryName(member.BinaryName) is { } nameError)
            {
                return new Refused(nameError);
            }

            if (!seen.Add(member.BinaryName))
            {
                return new Refused($"binary set repeats \"{member.BinaryName}\"");
            }

            var file = VerifiedFile(member.Path, "installed binary");
            if (file.Error is not null)
            {
                return new Refused($"verify {member.BinaryName}: {file.Error}");
            }

            if (file.Digest != member.Sha256)
            {
                return new Refused(
                    $"{member.BinaryName} bytes have SHA-256 {file.Digest}, expected {member.Sha256}");
            }

            verified.Add(new BinarySetMember(member.BinaryName, file.Digest, file.Path));
        }

        verified.Sort((left, right) => string.CompareOrdinal(left.BinaryName, right.BinaryName));
        return SetIdFor(verified) switch
        {
            Refused refused => refused,
            Accepted<string> identity => Store(identity.Value),
        };

        BinarySetActivation Store(string setId)
        {
            var set = new BinarySet(setId, sourceRevision, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var transaction = connection.BeginTransaction();
            using (var sets = connection.CreateCommand())
            {
                sets.Transaction = transaction;
                sets.CommandText = """
                    INSERT INTO binary_sets (set_id, source_revision, installed_at_ms)
                    VALUES ($set_id, $source_revision, $installed_at_ms)
                    ON CONFLICT(set_id) DO UPDATE SET source_revision = excluded.source_revision
                    """;
                sets.Parameters.AddWithValue("$set_id", set.SetId);
                sets.Parameters.AddWithValue("$source_revision", set.SourceRevision);
                sets.Parameters.AddWithValue("$installed_at_ms", set.InstalledAtMs);
                sets.ExecuteNonQuery();
            }

            foreach (var member in verified)
            {
                using var rows = connection.CreateCommand();
                rows.Transaction = transaction;
                rows.CommandText = """
                    INSERT INTO binary_set_members (set_id, binary_name, sha256, path)
                    VALUES ($set_id, $binary_name, $sha256, $path)
                    ON CONFLICT(set_id, binary_name) DO UPDATE SET path = excluded.path
                    """;
                rows.Parameters.AddWithValue("$set_id", set.SetId);
                rows.Parameters.AddWithValue("$binary_name", member.BinaryName);
                rows.Parameters.AddWithValue("$sha256", member.Sha256);
                rows.Parameters.AddWithValue("$path", member.Path);
                rows.ExecuteNonQuery();
            }

            using (var active = connection.CreateCommand())
            {
                active.Transaction = transaction;
                active.CommandText = """
                    INSERT INTO active_binary_set (singleton, set_id) VALUES (1, $set_id)
                    ON CONFLICT(singleton) DO UPDATE SET set_id = excluded.set_id
                    """;
                active.Parameters.AddWithValue("$set_id", set.SetId);
                active.ExecuteNonQuery();
            }

            transaction.Commit();
            return new Accepted<BinarySet>(set);
        }
    }

    public ActiveBinaryResolution ResolveActive(string binaryName)
    {
        if (ValidateBinaryName(binaryName) is { } nameError)
        {
            return new Refused(nameError);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sets.set_id, sets.source_revision, sets.installed_at_ms,
                   members.binary_name, members.sha256, members.path
            FROM active_binary_set AS active
            JOIN binary_sets AS sets ON sets.set_id = active.set_id
            JOIN binary_set_members AS members ON members.set_id = sets.set_id
            WHERE active.singleton = 1 AND members.binary_name = $binary_name
            """;
        command.Parameters.AddWithValue("$binary_name", binaryName);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return new Refused($"active installed binary set has no {binaryName}");
        }

        var member = new ActiveBinaryMember(
            row.GetString(0),
            row.GetString(1),
            row.GetInt64(2),
            row.GetString(3),
            row.GetString(4),
            row.GetString(5));
        var file = VerifiedFile(member.Path, "active installed binary");
        if (file.Error is not null)
        {
            return new Refused(file.Error);
        }

        if (file.Digest != member.Sha256)
        {
            return new Refused(
                $"active {binaryName} bytes have SHA-256 {file.Digest}, expected {member.Sha256}");
        }

        return new Accepted<ActiveBinaryMember>(member with { Path = file.Path });
    }

    /// <summary>
    /// Identity-bearing: SHA-256 over "fern.installed-binary-set.v1", NUL, then
    /// for each member in ordinal name order its name, NUL, its lowercase hex
    /// digest, NUL. Proved against a set id the Go binary wrote (see
    /// BuildInventoryTests).
    /// </summary>
    public static BinarySetIdentity SetIdFor(IReadOnlyList<BinarySetMember> members)
    {
        if (members.Count == 0)
        {
            return new Refused("a binary set needs at least one member");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (ValidateBinaryName(member.BinaryName) is { } nameError)
            {
                return new Refused(nameError);
            }

            if (!seen.Add(member.BinaryName))
            {
                return new Refused($"binary set repeats \"{member.BinaryName}\"");
            }

            if (member.Sha256.Length != 64 || !member.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return new Refused($"{member.BinaryName} SHA-256 must be 64 lowercase hexadecimal characters");
            }
        }

        var ordered = members.OrderBy(member => member.BinaryName, StringComparer.Ordinal).ToList();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(SetIdPreimageHeader));
        hash.AppendData([0]);
        foreach (var member in ordered)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(member.BinaryName));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(member.Sha256));
            hash.AppendData([0]);
        }

        return new Accepted<string>("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    static string? ValidateBinaryName(string name)
    {
        if (name.Length == 0)
        {
            return "binary name is empty";
        }

        foreach (var character in name)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')
            {
                continue;
            }

            return $"binary name \"{name}\" contains unsupported characters";
        }

        return null;
    }

    static (string Path, string Digest, string? Error) VerifiedFile(string path, string label)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(absolute))
        {
            return (absolute, "", $"{label} {absolute} is not a regular file");
        }

        if (!File.Exists(absolute))
        {
            return (absolute, "", $"open {label} {absolute}: no such file");
        }

        using var stream = File.OpenRead(absolute);
        return (absolute, Convert.ToHexStringLower(SHA256.HashData(stream)), null);
    }
}
