using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public void Writer_pragmas_match_the_rust_contract()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.Equal("wal", ReadPragma(writer.Connection, "journal_mode"));
        Assert.Equal("1", ReadPragma(writer.Connection, "foreign_keys"));
        // CHG 2026-09-10: no writer busy_timeout is asserted, because none is
        // set. The 60-second pragma that used to be here was above the provider's
        // 30-second CommandTimeout and so could never expire first; the writer's
        // real bound is the provider's, and this test no longer claims otherwise.
        Assert.Equal(30, writer.Connection.DefaultTimeout);
        Assert.NotEqual("1", ReadPragma(writer.Connection, "query_only"));
    }

    [Fact]
    public void Reader_pragmas_match_the_rust_contract_and_refuse_a_write()
    {
        var path = ProductionHome.CreateRegistry();
        using var reader = RegistryHome.OpenReader(path);
        Assert.Equal("1", ReadPragma(reader.Connection, "query_only"));
        Assert.Equal(BusyTimeouts.ReaderWaitsForAWriterMs.ToString(), ReadPragma(reader.Connection, "busy_timeout"));

        using var command = reader.Connection.CreateCommand();
        command.CommandText = "CREATE TABLE reader_must_fail (value INTEGER)";
        var error = Assert.ThrowsAny<Exception>(() => command.ExecuteNonQuery());
        Assert.Contains("readonly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_opened_outside_RegistryWriter_fails_while_the_writer_holds_the_lock()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        using var started = new ManualResetEventSlim(false);
        using var hold = new ManualResetEventSlim(false);
        var inside = Task.Run(() => writer.WriteImmediate(_ =>
        {
            started.Set();
            hold.Wait();
            return 0;
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        using var outsider = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
        outsider.Open();
        using (var timeout = outsider.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 50";
            timeout.ExecuteNonQuery();
        }

        using var begin = outsider.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        var busy = Assert.Throws<SqliteException>(() => begin.ExecuteNonQuery());
        Assert.Equal(5, busy.SqliteErrorCode);

        hold.Set();
        await inside.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Second_immediate_writer_waits_rather_than_returning_busy()
    {
        var path = ProductionHome.CreateRegistry();
        using var first = RegistryHome.OpenWriter(path);
        using var second = RegistryHome.OpenWriter(path);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = Task.Run(() => first.WriteImmediate(_ =>
        {
            held.Set();
            release.Wait();
            return 0;
        }));
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        var waiter = Task.Run(() => second.WriteImmediate(connection =>
        {
            using var ping = connection.CreateCommand();
            ping.CommandText = "SELECT COUNT(*) FROM registry_schema";
            return Convert.ToInt64(ping.ExecuteScalar());
        }));

        var finishedEarly = await Task.WhenAny(waiter, Task.Delay(400)) == waiter;
        Assert.False(finishedEarly);
        release.Set();
        await holder;
        var count = await waiter;
        Assert.True(count > 0);
    }

    private static string ReadPragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        var value = command.ExecuteScalar();
        return Convert.ToString(value) ?? "";
    }
}
