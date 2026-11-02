namespace Fern.Registry;

/// <summary>
/// Typed refusal. Serialises into operation_outcomes.outcome_kind and
/// error_category. Unobserved RegistryResult values fail the build.
/// </summary>
public readonly record struct RegistryRefusal(
    string OutcomeKind,
    string? ErrorCategory,
    string Detail,
    string? HolderActor = null,
    string? HolderHost = null,
    string? LeaseId = null,
    long? StaleEpoch = null,
    long? SuccessorEpoch = null)
{
    public const string NoLongerCurrentSubstring = "no longer current";

    /// <summary>
    /// The only prose path. Scheduler consumers key on
    /// <see cref="NoLongerCurrentSubstring"/>.
    /// </summary>
    public string Render()
    {
        if (StaleEpoch is long stale)
        {
            var lease = LeaseId ?? "unknown";
            return $"scheduler lease {lease} at epoch {stale} is {NoLongerCurrentSubstring}; a successor was elected — stop this scheduler";
        }

        return Detail;
    }
}
