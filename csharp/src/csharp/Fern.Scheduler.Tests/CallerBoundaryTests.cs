using System.Text.Json.Serialization;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Pass 5: the real JSON caller path, separate from in-process operation tests.
/// Caller JSON is strict per Pass 2 addendum
/// reference:7930679b4dc5ecbdeff33f81d7a6c99cd92024da0d1c57d0bc708e169e34c7b3.
/// CHG: Plan control uses the public argv boundary, tested in
/// FernRunHostContractTests. Stored outcomes and the remaining JSON readers stay here.
/// </summary>
public sealed class CallerBoundaryTests
{
    /// <summary>
    /// A stored outcome written by an older binary keeps the field this one does
    /// not know and still reads back. The sample shape is declared here: it used
    /// to be the cancel-plan outcome, which retired with the Plan layer, and the
    /// rule is about STORED JSON rather than about any one document.
    /// </summary>
    sealed class SampleStoredOutcome
    {
        [JsonPropertyName("AlreadyInactive")]
        public SampleStoredSubject? AlreadyInactive { get; init; }
    }

    sealed class SampleStoredSubject
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    [Fact]
    public void AStoredOutcomeWithAnUnknownFieldStaysReadable()
    {
        var frozen =
            """{"AlreadyInactive":{"name":"frozen"},"kind":"handler"}""";
        var stored = JsonReadPolicy.ReadStored<SampleStoredOutcome>(frozen);
        Assert.Equal("frozen", stored.Value.AlreadyInactive!.Name);
        Assert.Equal("kind", Assert.Single(stored.UnknownFields).FieldName);
    }

    [Fact]
    public void SiteCallerJsonRefusesUnknownFields()
    {
        var site = JsonReadPolicy.ReadCaller<RegisterSiteRequestWire>(
            """{"site_id":"local-4070","execution_profile_id":"local-4070","execution_profile_revision":"rev","executor":"local","max_concurrent_stages":1,"max_gpu_stages":1,"allowed_handlers":["fern.stage.train.v1"],"enabled":true,"actor":{"kind":"cli","id":"r","owner_host":"h","owner_pid":1},"kind":"handler"}""");
        Assert.Equal("kind", UnionCase.Of<CallerJson<RegisterSiteRequestWire>.UnknownField>(site).FieldName);
    }
}
