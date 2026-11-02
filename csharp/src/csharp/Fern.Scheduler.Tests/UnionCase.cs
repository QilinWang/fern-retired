using System.Runtime.CompilerServices;
using Xunit.Sdk;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Reads the case a union holds, for tests.
///
/// A union is its own runtime type holding one case, so xUnit's
/// <c>Assert.IsType&lt;Accepted<Installation>&gt;(outcome)</c> inspects the union
/// and fails with "Actual: typeof(InstallOutcome)" — it reads the label rather
/// than the contents. Every union in this tree implements
/// <see cref="IUnion"/>, so one method serves all of them. Production code
/// matches with <c>is</c> and <c>switch</c>, which the compiler unwraps, and
/// never reads <c>Value</c>.
/// </summary>
static class UnionCase
{
    public static T Of<T>(IUnion union) where T : class =>
        union.Value as T
        ?? throw new XunitException(
            $"expected the {Name(typeof(T))} case, got {Held(union)}");

    // A union's all-zeros value holds no case at all; say so rather than
    // reporting a null of some expected type. See
    // docs/design/original/2026-09-10-when-an-outcome-becomes-a-union.md.
    static string Held(IUnion union) =>
        union.Value is null ? "a union that was never given a case" : Name(union.Value.GetType());

    static string Name(Type type) =>
        type.DeclaringType is null
            ? type.Name
            : $"{type.DeclaringType.Name.Split('`')[0]}.{type.Name}";
}
