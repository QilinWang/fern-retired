using System.Text.Json;
using Fern.Experiment;
using Xunit;

namespace Fern.Experiment.Tests;

/// <summary>
/// The specification a compiled study carries names the study (owner 2026-09-26:
/// "get rid of 256 entirely"); a study compiled before carries a digest of its
/// card's bytes and is read as written. Each case stops at the next check (the
/// cell's name), which says the specification itself was accepted.
/// </summary>
public sealed class StudyCellRequestSpecificationTests
{
    static string RefusalFor(string specification)
    {
        using var study = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schema = StudyCellRequest.CompiledStudySchema,
            study = "lorenz-example",
            resolution = new { resolved = new { specification, manifest = new { } } },
        }));
        using var cell = JsonDocument.Parse("{}");
        return StudyCellRequest.Assemble(study.RootElement, cell.RootElement, "cuda").Refusal;
    }

    [Fact]
    public void TheStudysOwnNameIsTheSpecification() =>
        Assert.Equal("compiled study cell must name its cell", RefusalFor("study:lorenz-example"));

    [Fact]
    public void AStudyCompiledBeforeKeepsItsDigestSpecification() =>
        Assert.Equal("compiled study cell must name its cell", RefusalFor("study:sha256:" + new string('a', 64)));

    [Theory]
    [InlineData("study:another-study")]
    [InlineData("study:sha256:short")]
    [InlineData("lorenz-example")]
    public void AnyOtherSpecificationIsRefusedByName(string specification) =>
        Assert.Contains("compiled study specification must be study:<its name>", RefusalFor(specification));
}
