using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Round 3 of docs/design/original/by_ai/2026-09-12-one-stage-vocabulary-at-three-levels.md:
/// a person reading the study is told where its cells differ.
///
/// The Go compiler splits a configuration into its top-level sections and mints
/// one digest each, beside the identity. Two cells share a section exactly when
/// they share its digest. That makes "this sweep varied one thing -- did it?" a
/// grouping of strings, needing no vocabulary at all: this host never learns
/// what `model` or `train` means, only that two cells wrote different digests
/// under that name.
///
/// Before this, the digests existed in the compiled study on disk and stopped
/// at the Registry's door, so the question could only be answered by finding
/// two files.
///
/// CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "the
/// card diff section supersedes this, so this should be retired"): the digests
/// are gone. The same question is answered from each cell's stored
/// configuration, compared field by field, which also names the fields. The
/// file was StudyRegionDifferenceTests.
/// </summary>
public sealed class StudyConfigurationDifferenceTests
{
    static (RegistryWriter Writer, long StudyId) Home(params string[] configPerCell)
    {
        var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-14') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        var cells = configPerCell.Select((config, ordinal) => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42, config,
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(
            writer, "swept", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, cells);
        Assert.True(registered.TryGetValue(out var value));
        return (writer, value.StudyId);
    }

    /// <summary>A configuration with four sections: schema, model, train and task.</summary>
    static string Config(int hidden = 64, int epochs = 50, int seqLen = 24, string extraKnob = "") =>
        """{"schema":"fern.training.v2","model":{"type":"fern","knobs":{"dim_hidden":"""
        + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + extraKnob
        + """}},"train":{"epochs":""" + epochs.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + """},"task":{"seq_len":""" + seqLen.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

    /// <summary>The design's own falsifier, stated in its own words.</summary>
    [Fact]
    public void TwoCellsWhoseArchitecturesDifferShowIt_AndTheirOtherSectionsMatch()
    {
        var (writer, studyId) = Home(Config(hidden: 64), Config(hidden: 128));
        using var _ = writer;

        var text = StudyProjection.ConfigurationDifferences(writer.Connection, studyId);

        Assert.Contains("cells differ in: model (2 versions)\n", text, StringComparison.Ordinal);
        Assert.Contains("fields that differ: model.knobs.dim_hidden\n", text, StringComparison.Ordinal);
        Assert.Contains("and agree in: schema, task, train", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep over seeds or data is the common case and must read as such: the
    /// configuration is one configuration, and the line says so rather than
    /// staying silent, because silence would read as "not carried".
    /// </summary>
    [Fact]
    public void CellsThatShareEverySectionAreSaidToDifferOnlyInDataOrSeed()
    {
        var (writer, studyId) = Home(Config(), Config());
        using var _ = writer;

        var text = StudyProjection.ConfigurationDifferences(writer.Connection, studyId);

        Assert.Contains("every cell declares the same configuration in all 4 sections", text, StringComparison.Ordinal);
        Assert.Contains("they differ only in data or seed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeArchitecturesReadAsThreeVersionsNotAsADifference()
    {
        var (writer, studyId) = Home(Config(hidden: 64), Config(hidden: 128), Config(hidden: 256));
        using var _ = writer;

        Assert.Contains("model (3 versions)", StudyProjection.ConfigurationDifferences(writer.Connection, studyId),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSectionsThatMoveAreBothNamedWithTheirFields()
    {
        var (writer, studyId) = Home(Config(hidden: 64, epochs: 50), Config(hidden: 128, epochs: 80));
        using var _ = writer;

        var text = StudyProjection.ConfigurationDifferences(writer.Connection, studyId);

        Assert.Contains("cells differ in: model (2 versions), train (2 versions)\n", text, StringComparison.Ordinal);
        Assert.Contains("fields that differ: model.knobs.dim_hidden, train.epochs\n", text, StringComparison.Ordinal);
        Assert.Contains("and agree in: schema, task", text, StringComparison.Ordinal);
    }

    /// <summary>A field one cell states and another omits is a difference, not an agreement.</summary>
    [Fact]
    public void AFieldOneCellStatesAndAnotherOmitsIsADifference()
    {
        var (writer, studyId) = Home(Config(), Config(extraKnob: ""","act":"elu" """.Trim()));
        using var _ = writer;

        var text = StudyProjection.ConfigurationDifferences(writer.Connection, studyId);

        Assert.Contains("fields that differ: model.knobs.act\n", text, StringComparison.Ordinal);
    }

    /// <summary>One cell has nothing to compare.</summary>
    [Fact]
    public void AStudyOfOneCellSaysNothing()
    {
        var (writer, studyId) = Home(Config());
        using var _ = writer;

        Assert.Equal("", StudyProjection.ConfigurationDifferences(writer.Connection, studyId));
    }

    /// <summary>
    /// A compiled study written before 2026-09-26 carries config_regions, well
    /// formed or not; registration no longer reads them, so none of them refuses.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(""" "config_regions": null,""")]
    [InlineData(""" "config_regions": {},""")]
    [InlineData(""" "config_regions": {"model": "aaaaaaaaaaaaaaaa", "train": "1111111111111111"},""")]
    [InlineData(""" "config_regions": {"model": "not-sixteen-hex"},""")]
    [InlineData(""" "config_regions": ["model"],""")]
    public void AnyConfigRegionsACompiledCellStillCarriesAreIgnored(string regions)
    {
        var read = StudyRegistration.ReadCell(CellWith(regions), 0, "rossler_param_shock.series", "cuda");
        Assert.True(read.IsOk);
    }

    static System.Text.Json.JsonElement CellWith(string regions) =>
        System.Text.Json.JsonDocument.Parse(
            """
            {
              "name": "fern-a", "ordinal": 0,
              "config": {"schema": "fern.training.v2", "model": {"type": "fern"}},
              "config_hash": "0123456789abcdef",REGIONS
              "coordinates": {"seed": 7, "dataset": "rossler_param_shock.series"},
              "unit_outputs": {"unit_results": "results.json"}
            }
            """.Replace("REGIONS", regions, StringComparison.Ordinal)).RootElement;
}
