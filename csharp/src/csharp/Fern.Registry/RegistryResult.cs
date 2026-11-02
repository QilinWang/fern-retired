namespace Fern.Registry;

/// <summary>
/// Success or a typed refusal. Discarding or ignoring the return fails the
/// build through UnusedRegistryResultAnalyzer plus warnings-as-errors.
/// </summary>
public readonly struct RegistryResult<T>
{
    private readonly T _value;
    private readonly RegistryRefusal _refusal;

    private RegistryResult(T value)
    {
        IsOk = true;
        _value = value;
        _refusal = default;
    }

    private RegistryResult(RegistryRefusal refusal)
    {
        IsOk = false;
        _value = default!;
        _refusal = refusal;
    }

    public bool IsOk { get; }

    public static RegistryResult<T> Ok(T value) => new(value);

    public static RegistryResult<T> Rejected(RegistryRefusal refusal) => new(refusal);

    public bool TryGetValue(out T value)
    {
        value = _value;
        return IsOk;
    }

    public bool TryGetRefusal(out RegistryRefusal refusal)
    {
        refusal = _refusal;
        return !IsOk;
    }
}

/// <summary>Same contract when the success value is empty.</summary>
public readonly struct RegistryResult
{
    private readonly RegistryRefusal _refusal;

    private RegistryResult(bool ok, RegistryRefusal refusal)
    {
        IsOk = ok;
        _refusal = refusal;
    }

    public bool IsOk { get; }

    public static RegistryResult Ok() => new(true, default);

    public static RegistryResult Rejected(RegistryRefusal refusal) => new(false, refusal);

    public bool TryGetRefusal(out RegistryRefusal refusal)
    {
        refusal = _refusal;
        return !IsOk;
    }
}
