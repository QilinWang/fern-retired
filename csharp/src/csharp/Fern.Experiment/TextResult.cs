namespace Fern.Experiment;

/// <summary>
/// Success or a plain-text refusal: the C# form of the Rust
/// <c>Result&lt;T, String&gt;</c> used by the vocabulary and replica APIs whose
/// errors are sentences rather than field-addressed records. Families with a
/// typed error keep their own result types; this one exists so a plain-text
/// refusal is still a value a caller must look at, never an exception.
/// </summary>
public readonly struct TextResult<T>
{
    readonly T _value;
    readonly string? _refusal;

    TextResult(bool isOk, T value, string? refusal)
    {
        IsOk = isOk;
        _value = value;
        _refusal = refusal;
    }

    public bool IsOk { get; }

    public static TextResult<T> Ok(T value) => new(true, value, null);

    public static TextResult<T> Refused(string refusal) => new(false, default!, refusal);

    /// <summary>The success value. Throws when the result is a refusal (Rust <c>unwrap</c>).</summary>
    public T Value =>
        IsOk ? _value : throw new InvalidOperationException($"refused result read as a value: {_refusal}");

    /// <summary>The refusal text. Throws when the result is a success (Rust <c>unwrap_err</c>).</summary>
    public string Refusal =>
        IsOk ? throw new InvalidOperationException("ok result read as a refusal") : _refusal!;
}
