using System.Text.Json.Serialization;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The two JSON read rules, against a sample document declared here.
///
/// CHG 2026-09-11: the sample used to be the run-plan wire, which retired with
/// the Plan layer. What is under test is the POLICY -- a caller's JSON is
/// refused by field name when it carries a field the reader does not know, and
/// a stored document keeps its unknown fields and stays readable -- so the
/// sample is now a document that exists only here and cannot retire underneath
/// the test again.
/// </summary>
public sealed class JsonReadPolicyTests
{
    sealed class SampleDocument
    {
        [JsonPropertyName("schema")]
        public required string Schema { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("items")]
        public required IReadOnlyList<SampleItem> Items { get; init; }
    }

    sealed class SampleItem
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }
    }

    const string V1Shape =
        """{"schema":"fern.json_read_policy_sample.v1","name":"v1-json-shape","items":[{"model":"fern"}]}""";

    [Fact]
    public void CallerUnknownFieldIsRefusedByName()
    {
        var json =
            """{"schema":"fern.json_read_policy_sample.v1","name":"caller","items":[],"kind":"handler"}""";
        var read = JsonReadPolicy.ReadCaller<SampleDocument>(json);
        var refused = UnionCase.Of<CallerJson<SampleDocument>.UnknownField>(read);
        Assert.Equal("kind", refused.FieldName);
    }

    [Fact]
    public void StoredUnknownFieldIsRecordedAndTheRecordStaysReadable()
    {
        var json =
            """{"schema":"fern.json_read_policy_sample.v1","name":"frozen","items":[],"kind":"handler"}""";
        var stored = JsonReadPolicy.ReadStored<SampleDocument>(json);
        Assert.Equal("frozen", stored.Value.Name);
        Assert.Equal("kind", Assert.Single(stored.UnknownFields).FieldName);
        Assert.Equal("\"handler\"", stored.UnknownFields[0].RawJson);
    }

    [Fact]
    public void CallerV1ShapeIsAcceptedWithoutUnknownFields()
    {
        var read = JsonReadPolicy.ReadCaller<SampleDocument>(V1Shape);
        var accepted = UnionCase.Of<CallerJson<SampleDocument>.Accepted>(read);
        Assert.Equal("v1-json-shape", accepted.Value.Name);
        Assert.Equal("fern", Assert.Single(accepted.Value.Items).Model);
    }
}
