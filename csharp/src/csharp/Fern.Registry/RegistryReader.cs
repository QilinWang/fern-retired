using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// Read-only connection. query_only is set; a write is refused by SQLite.
/// </summary>
public sealed class RegistryReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RegistryAccess _access;

    internal RegistryReader(SqliteConnection connection, RegistryAccess access)
    {
        _connection = connection;
        _access = access;
    }

    public SqliteConnection Connection => _connection;

    public void Dispose()
    {
        try { _connection.Dispose(); }
        finally { _access.Dispose(); }
    }
}
