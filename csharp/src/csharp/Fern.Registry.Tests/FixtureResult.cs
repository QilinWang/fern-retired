namespace Fern.Registry.Tests;

/// <summary>
/// Takes the value out of a <see cref="RegistryResult{T}"/> in a fixture, and
/// says which authority refused when there is no value.
///
/// Fixtures that seed through an authority instead of writing rows by hand all
/// grew the same three lines:
///
/// <code>
/// Assert.True(MatrixAuthority.Declare(
///     writer, "lane", null, [DeclaredCoordinate.Unit("target", "{}")], "test", null)
///     .TryGetValue(out var matrixId));
/// </code>
///
/// Two of those lines are ceremony, and when the seed does fail they report
/// "Assert.True() Failure" with nothing about which rule refused — the refusal
/// detail is inside the result, and the assertion throws it away. This is the
/// part worth extracting: not the seeding, which differs at every call (one
/// coordinate or three, a preset or none, a literal target or a computed one),
/// but the unwrapping, which is identical everywhere.
///
/// <code>
/// var matrixId = MatrixAuthority.Declare(
///     writer, "lane", null, [DeclaredCoordinate.Unit("target", "{}")], "test", null)
///     .Expect("MatrixAuthority.Declare");
/// </code>
///
/// It is deliberately not a seeding wrapper. A helper whose body calls
/// <c>Declare</c> with some arguments filled in would be the banned
/// default-filling chain, and it would fit only the single-coordinate fixtures
/// anyway.
///
/// Linked into Fern.Scheduler.Tests by its project file, so both test assemblies
/// share one definition rather than one name with two bodies.
/// </summary>
internal static class FixtureResult
{
    /// <summary>
    /// The value, or an exception naming the <paramref name="call"/> that was
    /// refused and the authority's own refusal detail. A refused seed is a broken
    /// fixture rather than a failed assertion, so it throws rather than asserting:
    /// the test that called it has not begun testing anything yet.
    /// </summary>
    public static T Expect<T>(this RegistryResult<T> result, string call)
    {
        if (result.TryGetValue(out var value))
        {
            return value;
        }

        throw Refused(call, result.TryGetRefusal(out var refusal) ? refusal : default);
    }

    /// <summary>Same, where the authority returns no value.</summary>
    public static void Expect(this RegistryResult result, string call)
    {
        if (!result.IsOk)
        {
            throw Refused(call, result.TryGetRefusal(out var refusal) ? refusal : default);
        }
    }

    static InvalidOperationException Refused(string call, RegistryRefusal refusal) =>
        new(string.IsNullOrEmpty(refusal.Detail)
            ? $"the fixture's call to {call} returned neither a value nor a refusal"
            : $"the fixture's call to {call} was refused: {refusal.Detail}");
}
