using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slice 7 of the cell port (design note row 12): the order queued work is
/// offered in is a priority a person sets over a study or a cell, and a
/// running cell is never moved because a cell with an open attempt is not in
/// the offer. CHG 2026-09-25 (slice P2 of the scheduling loop): the priority
/// is one of four ranks, a cell's own overriding its study's, and a person may
/// move work to the back of its rank once.
/// </summary>
public sealed class DispatchPriorityTests
{
    static (RegistryWriter Writer, RegistryOperations.RegisteredStudy First, RegistryOperations.RegisteredStudy Second) Home()
    {
        var path = ProductionHome.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        static RegistryOperations.CompiledCell Cell(long ordinal, string name) => new(
            ordinal, name, "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
            """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""");
        var first = RegistryOperations.RegisterStudy(
            writer, "first", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, [Cell(0, "a"), Cell(1, "b")]);
        var second = RegistryOperations.RegisterStudy(
            writer, "second", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, [Cell(0, "c"), Cell(1, "d")]);
        Assert.True(first.TryGetValue(out var one));
        Assert.True(second.TryGetValue(out var two));
        return (writer, one, two);
    }

    static IReadOnlyList<string> Order(RegistryWriter writer) =>
        RegistryOperations.ListRunnableCells(writer.Connection).Select(cell => cell.Name).ToList();

    [Fact]
    public void WithoutAPriorityStudiesComeInRegistrationOrderAndCellsInCompiledOrder()
    {
        var (writer, _, _) = Home();
        using var _ = writer;
        Assert.Equal(["a", "b", "c", "d"], Order(writer));
    }

    [Fact]
    public void AStudyRankMovesItsCellsAheadAndACellsOwnRankOverridesItsStudys()
    {
        var (writer, first, second) = Home();
        using var _ = writer;

        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.StudyScope(second.StudyId), "urgent", "the paper needs it first", "qilin").IsOk);
        Assert.Equal(["c", "d", "a", "b"], Order(writer));

        // A cell's own rank is its rank, whatever its study's: b immediate goes ahead of every urgent cell.
        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.CellScope(first.CellIds[1]), "immediate", "b is the reference", "qilin").IsOk);
        Assert.Equal(["b", "c", "d", "a"], Order(writer));

        // Slow sinks below every normal cell, including a study registered later.
        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.CellScope(first.CellIds[1]), "slow", "b can wait", "qilin").IsOk);
        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.StudyScope(second.StudyId), "normal", "never mind", "qilin").IsOk);
        Assert.Equal(["a", "c", "d", "b"], Order(writer));

        // Set again: replaced in place, one row per scope, and the same rank again moves nothing.
        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.StudyScope(second.StudyId), "normal", "still normal", "qilin").IsOk);
        Assert.Equal(["a", "c", "d", "b"], Order(writer));
        var read = RegistryOperations.ReadPriority(writer.Connection, RegistryOperations.StudyScope(second.StudyId));
        Assert.NotNull(read);
        Assert.Equal("normal", read.Priority);
        Assert.Equal("still normal", read.Reason);
        Assert.Null(read.ConvertedFrom);
    }

    [Fact]
    public void AMoveToLastIsOnceAndStudiesRegisteredLaterStillQueueBehindIt()
    {
        var (writer, first, second) = Home();
        using var _ = writer;

        var moved = RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(first.StudyId), "it can wait", "qilin");
        Assert.True(moved.TryGetValue(out var move));
        Assert.Equal(second.StudyId, move.BehindStudyId);
        Assert.Equal(["c", "d", "a", "b"], Order(writer));

        // A third study registered afterwards queues behind the moved one: the move was once.
        var third = RegistryOperations.RegisterStudy(
            writer, "third", 1, """{"schema":"fern.compiled-study.v1"}""", SourceOf(writer, first.StudyId),
            [new RegistryOperations.CompiledCell(0, "e", "rossler_param_shock.series", 42,
                """{"schema":"fern.training.v2","model":{"type":"fern"}}""", 
                """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")]);
        Assert.True(third.IsOk);
        Assert.Equal(["c", "d", "a", "b", "e"], Order(writer));

        // A move does not change the rank: an urgent study moved last is behind the urgent ones only.
        Assert.True(RegistryOperations.SetPriority(
            writer, RegistryOperations.StudyScope(first.StudyId), "urgent", "back up", "qilin").IsOk);
        Assert.Equal(["a", "b", "c", "d", "e"], Order(writer));
        Assert.Equal(second.StudyId,
            RegistryOperations.ReadLastMove(writer.Connection, RegistryOperations.StudyScope(first.StudyId))!.BehindStudyId);
    }

    [Fact]
    public void TwoMovesBehindTheSameStudyKeepTheOrderTheyWereMadeIn()
    {
        var (writer, first, second) = Home();
        using var _ = writer;

        // The second study is moved first, then the first: the first ends up last.
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(second.StudyId), "one", "qilin").IsOk);
        Thread.Sleep(1100);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(first.StudyId), "two", "qilin").IsOk);
        Assert.Equal(["c", "d", "a", "b"], Order(writer));
    }

    [Fact]
    public void APriorityIsOneOfTheFourWordsAndLastIsAMove()
    {
        var (writer, first, _) = Home();
        using var _ = writer;
        var scope = RegistryOperations.StudyScope(first.StudyId);

        var last = RegistryOperations.SetPriority(writer, scope, "last", "why", "qilin");
        Assert.True(last.TryGetRefusal(out var refusal));
        Assert.Contains("last is a move, not a priority", refusal.Detail, StringComparison.Ordinal);
        Assert.False(RegistryOperations.SetPriority(writer, scope, "10", "why", "qilin").IsOk);
        Assert.False(RegistryOperations.SetPriority(writer, scope, "Urgent", "why", "qilin").IsOk);
        Assert.Null(RegistryOperations.ReadPriority(writer.Connection, scope));
    }

    [Fact]
    public void APriorityNamesARegisteredStudyOrCellAndAReason()
    {
        var (writer, first, _) = Home();
        using var _ = writer;

        Assert.False(RegistryOperations.SetPriority(writer, "plan:7", "urgent", "why", "qilin").IsOk);
        Assert.False(RegistryOperations.SetPriority(writer, RegistryOperations.StudyScope(first.StudyId), "urgent", "", "qilin").IsOk);
        var missing = RegistryOperations.SetPriority(writer, RegistryOperations.CellScope(999), "urgent", "why", "qilin");
        Assert.True(missing.TryGetRefusal(out var refusal));
        Assert.Contains("cell:999 is not registered", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(RegistryOperations.ReadPriority(writer.Connection, RegistryOperations.StudyScope(first.StudyId)));
        Assert.False(RegistryOperations.MoveLast(writer, "site:1", "why", "qilin").IsOk);
        Assert.False(RegistryOperations.MoveLast(writer, RegistryOperations.CellScope(999), "why", "qilin").IsOk);
    }

    static long SourceOf(RegistryWriter writer, long studyId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT source_id FROM studies WHERE id = $id";
        command.Parameters.AddWithValue("$id", studyId);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
