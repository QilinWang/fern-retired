using Xunit;

namespace Fern.Registry.Tests;

public sealed class ReceiptSemanticContentTests
{
    // CHG 2026-09-26: the canonical text itself, where these compared its SHA-256.
    static string Content(string json) => ReceiptSemanticContent.Canonical(
        json, ReceiptSemanticContent.ObservationMetadataFields);

    [Fact]
    public void KeyOrderAtAnyDepthDoesNotChangeTheContent()
    {
        var a = Content("""{"b": {"y": 1, "x": [1, 2]}, "a": "v"}""");
        var b = Content("""{"a": "v", "b": {"x": [1, 2], "y": 1}}""");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ObservationMetadataDoesNotChangeTheContent()
    {
        var a = Content("""{"proof": "x", "created_at_ms": 1000, "timings_ms": {"forward": 7.7}, "pid": 41}""");
        var b = Content("""{"proof": "x", "created_at_ms": 2000, "timings_ms": {"forward": 9.9}, "pid": 42}""");
        Assert.Equal(a, b);
    }

    [Fact]
    public void AnEvidenceFieldChangeChangesTheContent()
    {
        var a = Content("""{"proof": "x", "output_digest": "sha256:aaa"}""");
        var b = Content("""{"proof": "x", "output_digest": "sha256:bbb"}""");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ExclusionAppliesOnlyAtTheTopLevel()
    {
        // A nested field that happens to share an excluded name is evidence
        // content and stays in the preimage; only top-level occasion fields
        // are occasion metadata.
        var a = Content("""{"evidence": {"created_at_ms": 1}}""");
        var b = Content("""{"evidence": {"created_at_ms": 2}}""");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ArrayOrderRemainsPartOfTheProof()
    {
        var a = Content("""{"phases": ["train", "score"]}""");
        var b = Content("""{"phases": ["score", "train"]}""");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ReplayClassificationIsFirstThenIdempotentThenConflict()
    {
        var content = Content("""{"proof": "x"}""");
        Assert.Equal(ReceiptReplayVerdict.FirstAcceptance, ReceiptSemanticContent.Classify(null, content));
        Assert.Equal(ReceiptReplayVerdict.IdempotentReplay, ReceiptSemanticContent.Classify(content, content));
        Assert.Equal(
            ReceiptReplayVerdict.ContentConflict,
            ReceiptSemanticContent.Classify(content, Content("""{"proof": "y"}""")));
    }
}
