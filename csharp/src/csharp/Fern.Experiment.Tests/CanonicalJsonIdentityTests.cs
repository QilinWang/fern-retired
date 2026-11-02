using System.Text.Json;
using CsCheck;
using Fern.Experiment;
using Xunit;

namespace Fern.Experiment.Tests;

/// <summary>
/// The canonicalizer checked against identities RUST published, not against
/// values chosen here.
///
/// Both cases are copied verbatim from a real
/// <c>deploy/implementations/&lt;id&gt;/manifest.json</c>, and the expected digest is
/// the directory name -- which Rust chose, months before this file existed. That
/// is the only party to the comparison that cannot be edited into agreement with
/// the implementation.
///
/// They are inline rather than read from disk on purpose: <c>deploy/</c> is
/// gitignored, so a test that opened it would fail in a clean checkout, and a
/// check that cannot run in CI is a sentence pretending to be a proof.
/// </summary>
public class CanonicalJsonIdentityTests
{
    [Fact]
    public void RobotDynamicsInferenceSamplerIsPartOfConfigurationIdentity()
    {
        const string config = """{"model":{"type":"robot_dynamics_diffusion","knobs":{"backbone":"diffuser","diffusion_steps":100,"sampler":"ddpm","sampling_steps":100}}}""";
        using var original = JsonDocument.Parse(config);
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(original.RootElement));
        var knobs = canonical.RootElement.GetProperty("model").GetProperty("knobs");
        Assert.Equal(100, knobs.GetProperty("diffusion_steps").GetInt32());
        Assert.Equal("ddpm", knobs.GetProperty("sampler").GetString());
        foreach (var changed in new[]
        {
            config.Replace("\"sampler\":\"ddpm\"", "\"sampler\":\"ddim\"", StringComparison.Ordinal),
            config.Replace("\"sampling_steps\":100", "\"sampling_steps\":20", StringComparison.Ordinal),
            config.Replace("\"diffusion_steps\":100", "\"diffusion_steps\":50", StringComparison.Ordinal),
        })
        {
            using var other = JsonDocument.Parse(changed);
            Assert.NotEqual(CanonicalJson.DigestHex(original.RootElement), CanonicalJson.DigestHex(other.RootElement));
        }
    }

    [Fact]
    public void TemporalSplineConditioningAndCoordinateRangeChangeIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"encoder_coupling":"temporal_odd_even","temporal_spline":{"auxiliary_lags":[0],"num_bins":8,"tail_bound":3.0,"min_bin_width":0.001,"min_bin_height":0.001,"min_derivative":0.001}}}}""";
        using var original = JsonDocument.Parse(config);
        foreach (var changed in new[] {
            config.Replace("\"auxiliary_lags\":[0]", "\"auxiliary_lags\":[0,1,2,3,4,5,6,7]", StringComparison.Ordinal),
            config.Replace("temporal_odd_even", "temporal_odd_even_z", StringComparison.Ordinal),
            config.Replace("\"tail_bound\":3.0", "\"tail_bound\":5.0", StringComparison.Ordinal),
        })
        {
            using var other = JsonDocument.Parse(changed);
            Assert.NotEqual(CanonicalJson.DigestHex(original.RootElement), CanonicalJson.DigestHex(other.RootElement));
        }
    }

    [Fact]
    public void ChannelHistoryModeLagsAndScaleBoundRemainInIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"channel_history":{"kind":"affine_coupling","auxiliary_lags":[0],"log_scale_bound":1.0}}},"task":{"seq_len":192,"pred_len":192,"channels":3}}""";
        using var source = JsonDocument.Parse(config);
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(source.RootElement));
        Assert.Equal(192, canonical.RootElement.GetProperty("task").GetProperty("seq_len").GetInt32());
        foreach (var changed in new[]
        {
            config.Replace("\"auxiliary_lags\":[0]", "\"auxiliary_lags\":[2]", StringComparison.Ordinal),
            config.Replace("\"log_scale_bound\":1.0", "\"log_scale_bound\":2.0", StringComparison.Ordinal),
            config.Replace("\"kind\":\"affine_coupling\",\"auxiliary_lags\":[0],\"log_scale_bound\":1.0", "\"kind\":\"concat\",\"auxiliary_lags\":[0]", StringComparison.Ordinal),
        })
        {
            using var other = JsonDocument.Parse(changed);
            Assert.NotEqual(CanonicalJson.DigestHex(source.RootElement), CanonicalJson.DigestHex(other.RootElement));
        }
    }

    [Fact]
    public void AngularConditioningTailPowerAndRadiusUnitsChangeIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"angular":"vmf","angular_conditioning":"radius_uniform_origin","radial_spline":{"num_bins":8,"tail_bound":3.0,"bound_scale":"source_rms","tail_power":1.0,"min_bin_width":0.001,"min_bin_height":0.001,"min_derivative":0.001}}},"loss":{"train":[{"name":"energy_score","role":"active","weight":1.0}]}}""";
        using var source = JsonDocument.Parse(config);
        foreach (var changed in new[]
        {
            config.Replace("radius_uniform_origin", "radius", StringComparison.Ordinal),
            config.Replace("source_rms", "source_radius", StringComparison.Ordinal),
            config.Replace("\"tail_power\":1.0", "\"tail_power\":1.5", StringComparison.Ordinal),
            config.Replace("energy_score", "crps", StringComparison.Ordinal),
        })
        {
            using var other = JsonDocument.Parse(changed);
            Assert.NotEqual(CanonicalJson.DigestHex(source.RootElement), CanonicalJson.DigestHex(other.RootElement));
        }
    }

    [Fact]
    public void RadialAngularChoicesAndSplineBoundsRemainInConfigurationIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"angular":"uniform","radial_spline":{"num_bins":8,"tail_bound":12.0,"min_bin_width":0.001,"min_bin_height":0.001,"min_derivative":0.001}}}}""";
        using var source = JsonDocument.Parse(config);
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(source.RootElement));
        var knobs = canonical.RootElement.GetProperty("model").GetProperty("knobs");
        Assert.Equal("uniform", knobs.GetProperty("angular").GetString());
        Assert.Equal(8, knobs.GetProperty("radial_spline").GetProperty("num_bins").GetInt32());
        Assert.Equal(12.0, knobs.GetProperty("radial_spline").GetProperty("tail_bound").GetDouble());
        foreach (var changed in new[] { config.Replace("uniform", "vmf", StringComparison.Ordinal), config.Replace("12.0", "8.0", StringComparison.Ordinal) })
        {
            using var other = JsonDocument.Parse(changed);
            Assert.NotEqual(CanonicalJson.DigestHex(source.RootElement), CanonicalJson.DigestHex(other.RootElement));
        }
    }

    [Fact]
    public void ProcessorFeedbackRetainsTheDeclaredChoiceAndChangesIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"processor_shifts":true,"processor_feedback":false}}}""";
        using var noFeedback = JsonDocument.Parse(config);
        using var feedback = JsonDocument.Parse(config.Replace("\"processor_feedback\":false", "\"processor_feedback\":true", StringComparison.Ordinal));
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(noFeedback.RootElement));
        Assert.False(canonical.RootElement.GetProperty("model").GetProperty("knobs").GetProperty("processor_feedback").GetBoolean());
        Assert.NotEqual(CanonicalJson.DigestHex(noFeedback.RootElement), CanonicalJson.DigestHex(feedback.RootElement));
    }

    [Fact]
    public void GaussianRadiusAndLeakAblationsKeepDistinctConfigurationIdentity()
    {
        const string config = """{"model":{"type":"fern","knobs":{"fern_density":"single_gaussian","innovation_radius":"per_channel","positive_scale_clip":"icml_no_leak"}}}""";
        using var source = JsonDocument.Parse(config);
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(source.RootElement));
        var knobs = canonical.RootElement.GetProperty("model").GetProperty("knobs");
        Assert.Equal("per_channel", knobs.GetProperty("innovation_radius").GetString());
        Assert.Equal("icml_no_leak", knobs.GetProperty("positive_scale_clip").GetString());
        Assert.False(knobs.TryGetProperty("random_radial", out _));
        using var leaky = JsonDocument.Parse(config.Replace("icml_no_leak", "icml", StringComparison.Ordinal));
        Assert.NotEqual(CanonicalJson.DigestHex(source.RootElement), CanonicalJson.DigestHex(leaky.RootElement));
        using var noRadius = JsonDocument.Parse("""{"model":{"type":"fern","knobs":{"fern_density":"single_gaussian","positive_scale_clip":"icml_no_leak"}}}""");
        Assert.NotEqual(CanonicalJson.DigestHex(source.RootElement), CanonicalJson.DigestHex(noRadius.RootElement));
    }

    [Fact]
    public void CanonicalObjectBytesIgnoreInsertionOrderAndAreIdempotent()
    {
        // CHG: explicit C# generation; keep the former 250 cases and bounded input size.
        Gen.UInt.Array[0, 60].Sample(scalars =>
        {
            // Generate valid Unicode scalars, including non-BMP keys. Keep the
            // recorded UTF-8/UTF-16 ordering counterexample in every generated set.
            var keys = scalars.Select(value =>
            {
                var scalar = (int)(value % 0x10F800);
                return char.ConvertFromUtf32(scalar < 0xD800 ? scalar : scalar + 0x800);
            }).Append("\uE000").Append("\U00010000").Distinct().ToArray();
            var values = keys.ToDictionary(key => key, key => new { text = key, nested = new[] { 0, -1, int.MaxValue } });
            using var first = JsonDocument.Parse(JsonSerializer.Serialize(values));
            using var reordered = JsonDocument.Parse(JsonSerializer.Serialize(values.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value)));
            var bytes = CanonicalJson.Bytes(first.RootElement);
            Assert.Equal(bytes, CanonicalJson.Bytes(reordered.RootElement));
            using var canonical = JsonDocument.Parse(bytes);
            Assert.Equal(bytes, CanonicalJson.Bytes(canonical.RootElement));
            Assert.Equal(keys.OrderBy(key => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(key)), StringComparer.Ordinal),
                canonical.RootElement.EnumerateObject().Select(property => property.Name));
            foreach (var key in keys) Assert.Equal(key, canonical.RootElement.GetProperty(key).GetProperty("text").GetString());
        }, iter: 250, threads: 1);
    }

    /// <summary>
    /// An Implementation that HAS an entrypoint contract digest.
    /// </summary>
    const string WithEntrypointContract = """
    {
      "schema": "fern.source-implementation.v2",
      "component_content_ids": ["source-content:3f3b7ba5ffc52cbff00d62bc0f9313034540107eb76e517ea4d3473bc5b76fb6"],
      "entrypoints": {"python_module": "fr_tr", "rust_binary": "fern-stage-runner"},
      "capability_digest": "sha256:6e1b4c1986252d11415e1993ebbf18bb712ace73f94c7675a57c7170a738756c",
      "entrypoint_contract_digest": "sha256:df96b41f7969813f59490f10ea0e51f6c74a8132c6fc480e55204c638b357e98",
      "build_contract_digest": "sha256:ae2b5c6cc07a5a498a4aa48c461372219b5c80706ff96eff2fd0d449f81142aa"
    }
    """;

    /// <summary>
    /// An Implementation that does NOT. This is the case that matters: Rust
    /// omits the key from the preimage rather than writing null, so that
    /// Implementations frozen before the field existed keep hashing the bytes
    /// they originally hashed. A port that writes null produces a different
    /// identity for every one of them, and nothing anywhere complains.
    /// </summary>
    const string WithoutEntrypointContract = """
    {
      "schema": "fern.source-implementation.v2",
      "component_content_ids": ["source-content:5aaab708e30e7f9ea1082898e091e789cbfa8dd923399a1e5f4e6aa149bd5016"],
      "entrypoints": {"python_module": "fr_tr", "rust_binary": "fern-stage-runner"},
      "capability_digest": "sha256:df96b41f7969813f59490f10ea0e51f6c74a8132c6fc480e55204c638b357e98",
      "build_contract_digest": "sha256:e3e3869e902fd679be01464c246343f3878ce0eaf097f032ac31b85febc9ef82"
    }
    """;

    [Theory]
    [InlineData(WithEntrypointContract, "005fdcc2de8e99d57fde433cd4f8afb82f72c5fc89940e022090ffe3f3765e21")]
    [InlineData(WithoutEntrypointContract, "3a50fe06649d9e121dbae7c82281bea262e15fb878ef4c727c8adadabc5a4870")]
    public void ReproducesAnImplementationIdentityRustPublished(string preimage, string expected)
    {
        using var document = JsonDocument.Parse(preimage);
        Assert.Equal(expected, CanonicalJson.DigestHex(document.RootElement));
    }

    /// <summary>
    /// Pins the three System.Text.Json defaults that are wrong for identity.
    /// The expected bytes are serde_json's, measured on 2026-08-26.
    /// </summary>
    [Fact]
    public void CanonicalBytesAreSerdesAndNotSystemTextJsonsDefaults()
    {
        // Written with the keys OUT of order, so a canonicalizer that preserves
        // document order rather than sorting fails here rather than silently.
        using var document = JsonDocument.Parse("""{"path":"a<b>c&d","note":"ünïcode"}""");
        var bytes = System.Text.Encoding.UTF8.GetString(CanonicalJson.Bytes(document.RootElement));
        Assert.Equal("""{"note":"ünïcode","path":"a<b>c&d"}""", bytes);
    }

    /// <summary>
    /// A number keeps the text it was written with. Parsing to double and
    /// reformatting turns 1.0 into 1 and 1e-7 into 1E-07, and either changes a
    /// digest while looking like the same number.
    /// </summary>
    [Fact]
    public void NumbersKeepTheirWrittenForm()
    {
        using var document = JsonDocument.Parse("""{"a":1.0,"b":1e-7,"c":0.30000000000000004}""");
        var bytes = System.Text.Encoding.UTF8.GetString(CanonicalJson.Bytes(document.RootElement));
        Assert.Equal("""{"a":1.0,"b":1e-7,"c":0.30000000000000004}""", bytes);
    }

    /// <summary>
    /// Rust compares String keys by their UTF-8 bytes. C# ordinal comparison
    /// uses UTF-16 code units and reverses this pair: U+10000 begins with D800,
    /// while the BMP private-use character is E000. Identity order follows the
    /// Rust bytes, not the host string representation.
    /// </summary>
    [Fact]
    public void CanonicalKeysUseRustUtf8ByteOrder()
    {
        using var document = JsonDocument.Parse("""{"\uD800\uDC00":2,"\uE000":1}""");
        using var canonical = JsonDocument.Parse(CanonicalJson.Bytes(document.RootElement));
        Assert.Equal(
            ["\uE000", "\U00010000"],
            canonical.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

}
