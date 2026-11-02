using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// One write entry point. Always BEGIN IMMEDIATE. Deferred BEGIN cannot be
/// expressed: the ADO.NET transaction helper is not called.
/// </summary>
public sealed class RegistryWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RegistryAccess _access;
    private bool _writing;

    internal RegistryWriter(SqliteConnection connection, RegistryAccess access)
    {
        _connection = connection;
        _access = access;
    }

    public SqliteConnection Connection => _connection;

    public T WriteImmediate<T>(Func<SqliteConnection, T> work)
    {
        // CHG: a replay envelope and its domain operation share one commit.
        // The same lock also serializes the supervisor's renewal with its worker.
        lock (_connection)
        {
            if (_writing) return work(_connection);
            using (var begin = _connection.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                begin.ExecuteNonQuery();
            }

            _writing = true;
            try
            {
                var result = work(_connection);
                using var commit = _connection.CreateCommand();
                commit.CommandText = "COMMIT";
                commit.ExecuteNonQuery();
                return result;
            }
            catch
            {
                using var rollback = _connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                try
                {
                    rollback.ExecuteNonQuery();
                }
                catch (Exception)
                {
                    // Connection is already failed; the original exception is the one to throw.
                }

                throw;
            }
            finally { _writing = false; }
        }
    }

    /// <summary>Replay one HTTP request from durable outcomes, or commit its result with its effects.</summary>
    /// <remarks>
    /// CHG 2026-09-26 (schema 19, docs/programs/2026-09-26-cards-without-hashes):
    /// the request is stored as the text it arrived as, the operation, a newline
    /// and the body, and a request id sent again is compared with it byte for
    /// byte, where a SHA-256 of that text was stored and compared. A different
    /// request is refused with both texts, shortened. An outcome recorded before
    /// schema 19 holds no text, so its request id cannot be compared and is
    /// refused by name rather than answered as if it matched.
    /// </remarks>
    public string Replay(string requestId, string operation, string request, Func<string> execute)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("request id must be a UUID");
        var requestText = operation + "\n" + request;
        return WriteImmediate(connection =>
        {
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT request_text, result_json FROM operation_outcomes WHERE operation_scope = 'home-http' AND actor_id = 'home-client' AND idempotency_key = $id";
                read.Parameters.AddWithValue("$id", requestId);
                using var rows = read.ExecuteReader();
                if (rows.Read())
                {
                    // CHG 2026-09-26: no row lacks its request text since step 18 stopped
                    // carrying the outcomes written before schema 19 (owner: "like useless").
                    var stored = rows.GetString(0);
                    if (!string.Equals(stored, requestText, StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"request id was already used with different arguments: recorded {Shortened(stored)}, received {Shortened(requestText)}");
                    return rows.GetString(1);
                }
            }
            var result = execute();
            using var write = connection.CreateCommand();
            write.CommandText = """
                INSERT INTO operation_outcomes (operation_scope, actor_id, idempotency_key, operation_name,
                    request_text, outcome_kind, result_json, created_at_ms)
                VALUES ('home-http', 'home-client', $id, $operation, $request, 'completed', $result, $at)
                """;
            write.Parameters.AddWithValue("$id", requestId);
            write.Parameters.AddWithValue("$operation", operation);
            write.Parameters.AddWithValue("$request", requestText);
            write.Parameters.AddWithValue("$result", result);
            write.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            write.ExecuteNonQuery();
            return result;
        });
    }

    /// <summary>A request's text for a refusal: the first 200 characters, and its length when longer.</summary>
    internal static string Shortened(string text)
    {
        const int Shown = 200;
        var oneLine = text.Replace("\n", "\\n", StringComparison.Ordinal);
        return oneLine.Length <= Shown
            ? $"\"{oneLine}\""
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"\"{oneLine[..Shown]}...\" ({text.Length} characters)");
    }

    public void Dispose()
    {
        try { _connection.Dispose(); }
        finally { _access.Dispose(); }
    }
}
