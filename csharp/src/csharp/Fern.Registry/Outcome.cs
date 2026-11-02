namespace Fern.Registry;

/// <summary>
/// The two cases that 27 of this tree's outcome types were each declaring for
/// themselves: an authority accepted the request and hands back one value, or
/// it refused with a reason a later reader can act on.
///
/// Declared once and shared, an outcome type is one line —
/// <c>public union BinarySetActivation(Accepted&lt;BinarySet&gt;, Refused);</c> —
/// instead of a five-line hierarchy, and the unions stay distinct types: an
/// <c>Accepted&lt;BinarySet&gt;</c> cannot be returned where a
/// <c>SystemdPathCheck</c> is expected (CS0029), and a switch that misses
/// <c>Refused</c> names it (CS8509, an error here).
///
/// A success carrying more than one value keeps its own named case rather than
/// growing a tuple — see
/// docs/design/original/2026-09-10-when-an-outcome-becomes-a-union.md, which
/// also records why a union may not be stored.
///
/// <see cref="RegistryResult{T}"/> is the older, larger surface for the same
/// idea, and answers it with <c>IsOk</c> plus a <c>Value</c> that throws. It is
/// not converted here: 640 references, and its analyzer enforces its use.
/// </summary>
public sealed record Accepted<T>(T Value);

/// <inheritdoc cref="Accepted{T}"/>
public sealed record Refused(string Detail);
