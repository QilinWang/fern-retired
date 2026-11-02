using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// Scheduler opens the selected Home through Registry's current-schema owner.
/// RegistryHome checks its embedded schema before returning either capability;
/// Scheduler does not interpret a second schema ledger.
/// </summary>
public static class ProductionRegistry
{
    public static RegistryOpenOutcome Open(string registryPath)
    {
        try
        {
            return new RegistryOpenOutcome.OpenedReader(RegistryHome.OpenReader(registryPath));
        }
        catch (RegistryFault fault)
        {
            return MapOpenFault(fault);
        }
        catch (SqliteException)
        {
            return new RegistryOpenOutcome.Refused(
                RegistrySchemaRefusalKind.InvalidSchema,
                "Registry file is not a readable current SQLite Home");
        }
    }

    public static RegistryOpenOutcome OpenWriter(string registryPath)
    {
        try
        {
            return new RegistryOpenOutcome.OpenedWriter(RegistryHome.OpenWriter(registryPath));
        }
        catch (RegistryFault fault)
        {
            return MapOpenFault(fault);
        }
        catch (SqliteException)
        {
            return new RegistryOpenOutcome.Refused(
                RegistrySchemaRefusalKind.InvalidSchema,
                "Registry file is not a readable current SQLite Home");
        }
    }

    static RegistryOpenOutcome.Refused MapOpenFault(RegistryFault fault) =>
        new(fault.Message.Contains("is not a regular file", StringComparison.Ordinal)
            ? RegistrySchemaRefusalKind.NotARegularFile
            : RegistrySchemaRefusalKind.InvalidSchema, fault.Message);
}

public abstract record RegistryOpenOutcome
{
    public sealed record OpenedWriter(RegistryWriter Writer) : RegistryOpenOutcome, IDisposable
    {
        public SqliteConnection Connection => Writer.Connection;

        public void Dispose() => Writer.Dispose();
    }

    public sealed record OpenedReader(RegistryReader Reader) : RegistryOpenOutcome, IDisposable
    {
        public SqliteConnection Connection => Reader.Connection;

        public void Dispose() => Reader.Dispose();
    }

    public sealed record Refused(RegistrySchemaRefusalKind Kind, string Detail) : RegistryOpenOutcome;
}

public enum RegistrySchemaRefusalKind
{
    NotARegularFile,
    InvalidSchema,
}
