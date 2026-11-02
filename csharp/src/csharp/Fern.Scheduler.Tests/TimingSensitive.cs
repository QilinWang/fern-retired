using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Tests whose assertion is a deadline share one xunit collection, which xunit
/// runs serially. A probe that must refuse within 200 ms and a serve cycle
/// that sleeps through an idle window are both correct and both wrong when run
/// beside each other on a loaded machine: the sleeper starves the prober and
/// the prober reports a timeout it did not earn.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "timing-sensitive";
}
