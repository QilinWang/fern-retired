namespace Fern.Registry;

/// <summary>
/// How long a reader waits for a writer.
///
/// CHG 2026-09-10: the writer's own bound is gone, because it never applied.
/// It was 60 seconds, set as `PRAGMA busy_timeout`, and Microsoft.Data.Sqlite
/// does not use SQLite's busy handler — it runs its own retry loop bounded by
/// `SqliteCommand.CommandTimeout`, whose default is 30 seconds. A pragma ABOVE
/// that default can never fire, so the writer really waited 30 seconds, or 5
/// wherever an authority sets `CommandTimeout = 5` itself. Deleting the
/// constant changes no behaviour; it stops the code claiming a number that was
/// never in force.
///
/// The reader's bound below is real for the same reason, inverted: 15 seconds
/// is BELOW the provider's default, so the pragma is what expires first.
/// Restoring a writer bound means setting `connection.DefaultTimeout`, not a
/// pragma — `csharp/Fern.Scheduler/CycleInspection.cs` does exactly that at one
/// call site and explains why.
/// </summary>
public static class BusyTimeouts
{
    public const int ReaderWaitsForAWriterMs = 15_000;
}
