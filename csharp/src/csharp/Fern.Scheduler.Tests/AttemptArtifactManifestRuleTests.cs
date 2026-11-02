using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Two rules the corpus tests prove only by consequence, ported 2026-08-28 from
/// `fern_run/src/results.rs` — `artifact_manifest_refuses_a_nested_symlink` and
/// the stored-spelling half of
/// `canonical_point_forecast_has_a_named_manifest_role`.
///
/// The corpus tests reproduce Rust's receipt digest over 8,994 sealed
/// snapshots, so a wrong role spelling or a followed symlink WOULD change a
/// digest and fail there. That is proof by consequence: it says the bytes came
/// out right, not which rule made them right, and it cannot fire at all for a
/// symlink because no corpus directory contains one. These two assert the
/// rules directly, so the Rust tests can retire without taking the promise
/// with them.
/// </summary>
public sealed class AttemptArtifactManifestRuleTests
{
    /// <summary>The three receipts `Capture` requires before it will sign anything.</summary>
    static string ACompletedAttemptDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("fern-manifest-rule-").FullName;
        File.WriteAllText(Path.Combine(directory, AttemptArtifactManifest.MetricsIpcFile), "metrics");
        File.WriteAllText(Path.Combine(directory, AttemptArtifactManifest.InvocationFile), "{}");
        File.WriteAllText(Path.Combine(directory, AttemptArtifactManifest.ExecutionEnvironmentFile), "{}");
        return directory;
    }

    /// <summary>
    /// A symlink anywhere beneath the directory is refused, not followed.
    ///
    /// Following one would sign bytes that live outside the Attempt, so the
    /// receipt would name a file the Attempt does not own and a later reopen
    /// would read whatever the link then pointed at.
    /// </summary>
    [Fact]
    public void ANonRegularEntryIsRefusedAtAnyDepthRatherThanFollowed()
    {
        var directory = ACompletedAttemptDirectory();
        try
        {
            // Clean first: the same directory must sign without complaint, or
            // the refusal below would prove nothing about the symlink.
            Assert.NotEmpty(AttemptArtifactManifest.Capture(directory).Files);

            var outputs = Directory.CreateDirectory(Path.Combine(directory, "outputs")).FullName;
            var real = Path.Combine(outputs, "point_forecast.safetensors");
            File.WriteAllText(real, "tensors");
            Assert.NotEmpty(AttemptArtifactManifest.Capture(directory).Files);

            File.CreateSymbolicLink(Path.Combine(outputs, "linked.safetensors"), real);

            var refusal = Assert.Throws<AttemptArtifactRefusal>(
                () => AttemptArtifactManifest.Capture(directory));
            Assert.Contains("non-regular entry", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("linked.safetensors", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Every role's stored spelling, written out. These strings are inside the
    /// receipt digest, so renaming one silently re-identifies every artifact
    /// ever sealed under it; that is why they are asserted as literals here
    /// rather than derived from the enum.
    /// </summary>
    [Theory]
    [InlineData(ArtifactFileRole.MetricsReceipt, "metrics_receipt")]
    [InlineData(ArtifactFileRole.Invocation, "invocation")]
    [InlineData(ArtifactFileRole.ExecutionEnvironment, "execution_environment")]
    [InlineData(ArtifactFileRole.Checkpoint, "checkpoint")]
    [InlineData(ArtifactFileRole.PointForecast, "point_forecast")]
    [InlineData(ArtifactFileRole.RawEvaluationTensors, "raw_evaluation_tensors")]
    [InlineData(ArtifactFileRole.SequenceTensors, "sequence_tensors")]
    [InlineData(ArtifactFileRole.ForecastCalibration, "forecast_calibration")]
    [InlineData(ArtifactFileRole.QuantileDiagnostics, "quantile_diagnostics")]
    [InlineData(ArtifactFileRole.Diagnostics, "diagnostics")]
    [InlineData(ArtifactFileRole.Other, "other")]
    public void EveryRoleKeepsTheStoredSpellingTheDigestWasTakenOver(ArtifactFileRole role, string stored) =>
        Assert.Equal(stored, AttemptArtifactManifest.StoredName(role));

    /// <summary>
    /// And the set is closed: a role added without a spelling would fall to
    /// "other" and quietly share an identity with every unclassified file.
    /// </summary>
    [Fact]
    public void NoRoleOtherThanOtherSpellsItselfOther()
    {
        var spelledOther = Enum.GetValues<ArtifactFileRole>()
            .Where(role => AttemptArtifactManifest.StoredName(role) == "other")
            .ToArray();
        Assert.Equal([ArtifactFileRole.Other], spelledOther);
    }
}
