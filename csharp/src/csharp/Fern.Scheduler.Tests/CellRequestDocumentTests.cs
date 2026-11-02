using Fern.Scheduler;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The bytes of the document a resident worker reads, pinned.
///
/// Slice 3 of docs/programs/2026-09-23-cells-as-pipelines puts a typed pipeline in front of
/// this document: the pipeline is what is written, and the document is what it
/// is projected into. The claim is that nothing on the wire moved, and this is
/// one half of its proof — the other half is the Gleam test
/// `a_one_phase_pipeline_renders_the_document_the_route_writes_today_test`,
/// which asserts the same literal. If either side changes, one of the two
/// fails and somebody has to say why.
/// </summary>
public sealed class CellRequestDocumentTests
{
    [Fact]
    public void TheOnePhaseCellRequestIsExactlyThisDocument()
    {
        Assert.Equal(
            "{\"schema\":\"fern.cell_request.v1\",\"cell_id\":\"attempt-41\","
            + "\"artifact_prefix\":\"attempt-41.\","
            + "\"workspace\":\"/home/user/study/tmp/fern-run-worker\","
            + "\"phases\":[{\"ordinal\":0,\"handler\":\"fern.study.run_cell\","
            + "\"request\":\"study-7.cell-19.attempt-41.epoch-1.request.stage_runner_request.v1.json\"}]}",
            LocalCellWorker.CellRequestDocument(
                "attempt-41",
                "/home/user/study/tmp/fern-run-worker",
                "study-7.cell-19.attempt-41.epoch-1.request.stage_runner_request.v1.json"));
    }
}
