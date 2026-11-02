namespace Fern.Site;

/// <summary>
/// The operational retry decision attached to a Site refusal.
///
/// The Go owner had one named policy, <c>never</c>, produced by its
/// <c>permanentf</c> helper, and ordinary filesystem errors carried no policy at
/// all. Both cases survive here: <see cref="Unclassified"/> is the plain error
/// with no decision attached, and the scheduler must not read it as permission
/// to retry a package defect.
/// </summary>
public enum SiteRetryPolicy
{
    /// <summary>An error with no operational decision attached, such as a failed read.</summary>
    Unclassified,

    /// <summary>
    /// Never retry. A package defect cannot become valid on another attempt,
    /// because the bytes would be identical.
    /// </summary>
    Never,
}

public enum SiteFailureCategory
{
    Unclassified,
    ConnectionAdmission,
    CredentialConfiguration,
    HelperMissing,
    HostUnreachable,
    HostKeyMismatch,
    AuthenticationRefused,
    SiteUnhealthy,
    /// <summary>
    /// The Site session and resident worker process are still alive, but the
    /// worker did not advance from its admitted starting identity to ready
    /// before the worker-start boundary expired. This is not transport loss.
    /// </summary>
    WorkerAdmissionDeadline,
    SessionLost,
}

/// <summary>One classified Site refusal: what was refused, and whether it may ever be retried.</summary>
public sealed record SiteFailure(SiteRetryPolicy RetryPolicy, string Detail)
{
    public SiteFailureCategory Category { get; init; } = SiteFailureCategory.Unclassified;

    public bool IsBeforeSiteSession => Category is
        SiteFailureCategory.ConnectionAdmission
        or SiteFailureCategory.CredentialConfiguration
        or SiteFailureCategory.HelperMissing
        or SiteFailureCategory.HostUnreachable
        or SiteFailureCategory.HostKeyMismatch
        or SiteFailureCategory.AuthenticationRefused
        or SiteFailureCategory.SiteUnhealthy;

    /// <summary>The refusal a package defect produces: detailed, and never retried.</summary>
    public static SiteFailure Permanent(string detail) => new(SiteRetryPolicy.Never, detail);

    /// <summary>A refusal with no retry decision, such as a filesystem error the Site did not classify.</summary>
    public static SiteFailure Unclassified(string detail) => new(SiteRetryPolicy.Unclassified, detail);

    public static SiteFailure ConnectionAdmission(string detail) =>
        Permanent(detail) with { Category = SiteFailureCategory.ConnectionAdmission };

    public static SiteFailure CredentialConfiguration(string detail) =>
        Permanent(detail) with { Category = SiteFailureCategory.CredentialConfiguration };

    public static SiteFailure HelperMissing(string detail) =>
        Permanent(detail) with { Category = SiteFailureCategory.HelperMissing };

    public static SiteFailure HostUnreachable(string detail) =>
        new(SiteRetryPolicy.Unclassified, detail) { Category = SiteFailureCategory.HostUnreachable };

    public static SiteFailure HostKeyMismatch(string detail) =>
        Permanent(detail) with { Category = SiteFailureCategory.HostKeyMismatch };

    public static SiteFailure AuthenticationRefused(string detail) =>
        Permanent(detail) with { Category = SiteFailureCategory.AuthenticationRefused };

    public static SiteFailure SiteUnhealthy(string detail) =>
        new(SiteRetryPolicy.Unclassified, detail) { Category = SiteFailureCategory.SiteUnhealthy };

    public static SiteFailure WorkerAdmissionDeadline(string detail) =>
        new(SiteRetryPolicy.Unclassified, detail) { Category = SiteFailureCategory.WorkerAdmissionDeadline };

    public static SiteFailure SessionLost(string detail) =>
        new(SiteRetryPolicy.Unclassified, detail) { Category = SiteFailureCategory.SessionLost };

    public string CategoryWire => Category switch
    {
        SiteFailureCategory.Unclassified => "unclassified",
        SiteFailureCategory.ConnectionAdmission => "connection_admission",
        SiteFailureCategory.CredentialConfiguration => "credential_configuration",
        SiteFailureCategory.HelperMissing => "helper_missing",
        SiteFailureCategory.HostUnreachable => "host_unreachable",
        SiteFailureCategory.HostKeyMismatch => "host_key_mismatch",
        SiteFailureCategory.AuthenticationRefused => "authentication_refused",
        SiteFailureCategory.SiteUnhealthy => "site_unhealthy",
        SiteFailureCategory.WorkerAdmissionDeadline => "worker_admission_deadline",
        SiteFailureCategory.SessionLost => "session_lost",
        _ => throw new ArgumentOutOfRangeException(nameof(Category), Category, "unknown Site failure category"),
    };

    public static SiteFailure FromWire(string category, string detail) => category switch
    {
        "unclassified" => Unclassified(detail),
        "connection_admission" => ConnectionAdmission(detail),
        "credential_configuration" => CredentialConfiguration(detail),
        "helper_missing" => HelperMissing(detail),
        "host_unreachable" => HostUnreachable(detail),
        "host_key_mismatch" => HostKeyMismatch(detail),
        "authentication_refused" => AuthenticationRefused(detail),
        "site_unhealthy" => SiteUnhealthy(detail),
        "worker_admission_deadline" => WorkerAdmissionDeadline(detail),
        "session_lost" => SessionLost(detail),
        _ => Permanent($"Site session returned unknown failure category {category}: {detail}"),
    };

    public override string ToString() => Detail;
}

/// <summary>
/// One Site operation's outcome. Refusals are values, not exceptions: every
/// Site boundary can refuse, and a caller that ignores the refusal must fail to
/// compile rather than fail on a machine nobody is watching.
/// </summary>
public readonly struct SiteResult<T>
{
    readonly T? value;
    readonly SiteFailure? failure;

    SiteResult(T? value, SiteFailure? failure)
    {
        this.value = value;
        this.failure = failure;
    }

    public static SiteResult<T> Ok(T value) => new(value, null);

    public static SiteResult<T> Refused(SiteFailure failure) => new(default, failure);

    public static SiteResult<T> Permanent(string detail) => Refused(SiteFailure.Permanent(detail));

    public bool IsOk => failure is null;

    public T Value => IsOk
        ? value!
        : throw new InvalidOperationException("a refused Site result has no value: " + failure!.Detail);

    public SiteFailure Failure => failure
        ?? throw new InvalidOperationException("an accepted Site result has no failure");

    /// <summary>The refusal detail, or null when the result was accepted. For test messages.</summary>
    public string? RefusalDetail => failure?.Detail;

    /// <summary>Carry this refusal into another result type without restating it.</summary>
    public SiteResult<TOther> RefusalAs<TOther>() => SiteResult<TOther>.Refused(
        failure ?? throw new InvalidOperationException("an accepted Site result has no failure to carry"));
}
