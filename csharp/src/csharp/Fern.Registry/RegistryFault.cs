namespace Fern.Registry;

/// <summary>
/// Environmental failure: missing file, schema Go never applied, SQLite IO.
/// Not a refusal. Refusals are persisted as operation_outcomes rows.
/// </summary>
public sealed class RegistryFault : Exception
{
    public RegistryFault(string message)
        : base(message)
    {
    }

    public RegistryFault(string message, Exception inner)
        : base(message, inner)
    {
    }
}
