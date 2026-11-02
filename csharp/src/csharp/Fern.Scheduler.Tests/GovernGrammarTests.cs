using System.Text;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The governance commands as one verb by one scope.
///
/// What is pinned here is the shape rather than the dispatch: that every pair either names a command
/// or says why there is none, that the product covers every verb against every scope so a hole is a
/// hole and not an omission, and that the table a person reads prints both. A layer that quietly
/// dropped a pair would look complete and be wrong.
/// </summary>
public sealed class GovernGrammarTests
{
    static readonly string[] Scopes = ["everything", "site", "study", "cell"];

    [Fact]
    public void EveryVerbIsAnsweredForEveryScope()
    {
        // Twenty-four pairs. A missing one would read as "not applicable" when it is "not written".
        foreach (var verb in Fern.Run.Cli.GovernVerbs.Verbs)
        {
            foreach (var scope in Scopes)
            {
                var pair = Fern.Run.Cli.GovernVerbs.Product.SingleOrDefault(p => p.Verb == verb && p.Scope == scope);
                Assert.True(pair is not null, $"{verb} {scope} is in neither the commands nor the holes");
            }
        }
        Assert.Equal(Fern.Run.Cli.GovernVerbs.Verbs.Length * Scopes.Length, Fern.Run.Cli.GovernVerbs.Product.Length);
    }

    [Fact]
    public void EveryPairEitherNamesACommandOrSaysWhyThereIsNone()
    {
        foreach (var pair in Fern.Run.Cli.GovernVerbs.Product)
        {
            var named = pair.Command is { Length: > 0 };
            var explained = pair.Hole is { Length: > 0 };
            Assert.True(named ^ explained, $"{pair.Verb} {pair.Scope} must name a command or a reason, and not both");
        }
    }

    [Fact]
    public void TheHolesTheDesignNamedAreStillHoles()
    {
        // These are the two the design pointed at, and finding them was the reason to draw the
        // product at all. If either ever gains a command, this test says so by failing.
        Assert.NotNull(Fern.Run.Cli.GovernVerbs.Product.Single(p => p.Verb == "resume" && p.Scope == "everything").Hole);
        Assert.NotNull(Fern.Run.Cli.GovernVerbs.Product.Single(p => p.Verb == "discard" && p.Scope == "cell").Hole);
    }

    [Fact]
    public void EveryCommandBehindAPairIsOneTheControlPlaneActuallyHas()
    {
        // The ten that exist. A pair pointing at a command nobody wrote would refuse at dispatch
        // rather than here, which is a worse place to find out.
        var exist = new[]
        {
            "pause-cells", "hold-cell", "release-request", "disable-site", "enable-site",
            "set-priority", "cancel-study", "retry-cell", "stop-supervisor",
        };
        foreach (var pair in Fern.Run.Cli.GovernVerbs.Product.Where(p => p.Command is not null))
        {
            Assert.Contains(pair.Command![0], exist);
        }
    }

    [Fact]
    public void TheTablePrintsTheCommandsTheSynonymsAndTheHoles()
    {
        var into = new StringWriter();

        Fern.Run.Cli.GovernVerbs.PrintGrammar(into);
        var printed = into.ToString();

        Assert.Contains("pause-cells", printed);
        Assert.Contains("—", printed);   // a hole is drawn as a hole
        // The same act under two names is the thing a flat list of ten commands could not show.
        Assert.Contains("disable-site does the same thing by another name", printed);
        Assert.Contains("no command lifts every standing request at once", printed);
    }

    [Fact]
    public void NoScopeIsSpelledTwoWaysInTheProduct()
    {
        // One vocabulary: a scope is everything, a site, a study or a cell, and nothing else.
        Assert.All(Fern.Run.Cli.GovernVerbs.Product, pair => Assert.Contains(pair.Scope, Scopes));
    }
}
