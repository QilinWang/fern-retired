using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slice 11 of the cell port: the accepted cells of a study are read from
/// the rows that hold them, <c>cells.accepted_attempt_id</c> and the
/// attempt's <c>result_json</c>, which is what the accepted-matrix export
/// answered over the Matrix tables. The claim: a cell is listed once its
/// attempt was accepted, by name, with the attempt's ordinal and the digest
/// of the result bytes the store holds; a cell with an open or no attempt is
/// not listed; the count says how many of the study's cells that is.
/// </summary>
public sealed class ListAcceptedCellsTests
{
    const string Request = """{"schema":"fern.cell_request.v2","cell":"x"}""";

    [Fact]
    public void AnAcceptedCellIsListedWithItsAttemptOrdinalAndTheDigestOfItsResultBytes()
    {
        var (writer, studyId, cells, siteId) = Home();
        using var _ = writer;
        Assert.Empty(RegistryOperations.AcceptedCells(writer.Connection, studyId));
        Assert.Equal(3, RegistryOperations.CellCount(writer.Connection, studyId));

        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request).TryGetValue(out var started));
        const string result = """{"schema":"fern.cell_result.v1","loss":0.125}""";
        Assert.True(RegistryOperations.FinishAttempt(writer, started.AttemptId, started.FencingEpoch, result).IsOk);
        // An open attempt on another cell is not an accepted one.
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[2], siteId, Request).IsOk);

        var accepted = Assert.Single(RegistryOperations.AcceptedCells(writer.Connection, studyId));
        Assert.Equal("cell-1", accepted.Cell);
        Assert.Equal(started.Ordinal, accepted.AttemptOrdinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result))), accepted.ResultSha256);
        Assert.Equal(3, RegistryOperations.CellCount(writer.Connection, studyId));
    }

    static (RegistryWriter Writer, long StudyId, IReadOnlyList<long> CellIds, long SiteId) Home()
    {
        var path = ProductionHome.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId, siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('two-cards', '{"device":"cuda","devices":["GPU-aaaa","GPU-bbbb"]}', '{"slots":2}', '2026-09-11') RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var declared = Enumerable.Range(0, 3).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(writer, "accepted-cells", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(registered.TryGetValue(out var study));
        ProductionHome.DeclareCodeFrom(writer, siteId);
        return (writer, study.StudyId, study.CellIds, siteId);
    }
}
