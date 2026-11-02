namespace Fern.Experiment;

/// <summary>
/// Mirrors Python <c>fern_train.zero_shot.Experiment.noise_kind</c> literals.
/// Every non-Gaussian law is variance-matched to one: Laplace uses scale
/// 1/sqrt(2), StudentT3 multiplies by sqrt((nu-2)/nu) = sqrt(1/3), so
/// relative doses stay train-std-scale interventions under every law.
/// </summary>
public enum NoiseKind
{
    Gaussian,
    Laplace,
    StudentT3,

    /// <summary>
    /// Huber's gross-error model: 98% of timestamps carry an ordinary
    /// Gaussian reading and 2% carry one ten times larger, the whole draw
    /// rescaled to unit variance. It is NOT another rung of the tail ladder.
    /// Laplace and Student-t3 make every reading heavier-tailed; contamination
    /// leaves almost every reading clean and makes a few isolated ones
    /// enormous, which is a different failure channel -- whether one outlier
    /// can capture a context window, rather than whether a diffuse tail can be
    /// integrated away. Its fourth moment is finite (kurtosis about 68), so
    /// unlike Student-t3 its squared-error confidence intervals are stable.
    /// </summary>
    Contaminated,
}

public static class NoiseKindWire
{
    static readonly NoiseKind[] All =
    [
        NoiseKind.Gaussian,
        NoiseKind.Laplace,
        NoiseKind.StudentT3,
        NoiseKind.Contaminated,
    ];

    /// <summary>The serde/wire string, identical to the serie <c>noise_kind</c> vocabulary.</summary>
#pragma warning disable CS8524 // unnamed enum values are cast defects; named members stay checked by CS8509
    public static string Value(this NoiseKind kind) => kind switch
    {
        NoiseKind.Gaussian => "gaussian",
        NoiseKind.Laplace => "laplace",
        NoiseKind.StudentT3 => "student_t3",
        NoiseKind.Contaminated => "contaminated",
    };
#pragma warning restore CS8524

    public static TextResult<NoiseKind> Parse(string raw)
    {
        foreach (var kind in All)
        {
            if (kind.Value() == raw)
            {
                return TextResult<NoiseKind>.Ok(kind);
            }
        }
        var valid = string.Join(", ", All.Select(kind => kind.Value()));
        return TextResult<NoiseKind>.Refused($"unknown noise kind \"{raw}\"; valid: {valid}");
    }
}
