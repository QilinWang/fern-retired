using System.Text;
using Fern.Scheduler;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// The Home-to-Site process boundary formerly owned by
/// fern-cli-rs/src/stage_plan_remote.rs. Pure byte construction remains in
/// StagePackageBuilder; these tests execute the transport and the built C# Site.
/// </summary>
public sealed class StagePackageTransportTests
{
    [Fact]
    public void RemoteStageProgressParserFiltersDuplicateReorderedAndChangedTotalCheckpoints()
    {
        Assert.Null(StagePackageTransport.ParseStageCheckpoint("trainer banner"));
        var epoch10 = Assert.IsType<StageCheckpoint>(StagePackageTransport.ParseStageCheckpoint(
            "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":10,\"total\":100}"));
        var epoch9 = Assert.IsType<StageCheckpoint>(StagePackageTransport.ParseStageCheckpoint(
            "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":9,\"total\":100}"));
        var changedTotal = Assert.IsType<StageCheckpoint>(StagePackageTransport.ParseStageCheckpoint(
            "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":11,\"total\":101}"));
        var earlyStopAt10 = Assert.IsType<StageCheckpoint>(StagePackageTransport.ParseStageCheckpoint(
            "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":10,\"total\":100,\"terminal\":true}"));
        var epoch20 = Assert.IsType<StageCheckpoint>(StagePackageTransport.ParseStageCheckpoint(
            "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":20,\"total\":100}"));

        Assert.Equal(epoch10, StagePackageTransport.AcceptMonotoneCheckpoint(null, epoch10));
        Assert.Null(StagePackageTransport.AcceptMonotoneCheckpoint(epoch10, epoch10));
        Assert.Null(StagePackageTransport.AcceptMonotoneCheckpoint(epoch10, epoch9));
        Assert.Null(StagePackageTransport.AcceptMonotoneCheckpoint(epoch10, changedTotal));
        Assert.Equal(earlyStopAt10, StagePackageTransport.AcceptMonotoneCheckpoint(epoch10, earlyStopAt10));
        Assert.Null(StagePackageTransport.AcceptMonotoneCheckpoint(earlyStopAt10, earlyStopAt10));
        Assert.Equal(epoch20, StagePackageTransport.AcceptMonotoneCheckpoint(epoch10, epoch20));
    }

    [Fact]
    public void RemoteStageStdoutReaderBoundsUnterminatedAndOversizedLines()
    {
        var accepted = new List<string>();
        var oversized = new string('x', SitePaths.MaximumStageProgressLineBytes + 1);
        var bytes = Encoding.UTF8.GetBytes(oversized + "\nsmall\nunterminated");

        StagePackageTransport.ForwardBoundedLines(new MemoryStream(bytes), accepted.Add);

        Assert.Equal(["small", "unterminated"], accepted);
    }
}
