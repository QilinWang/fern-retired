namespace Fern.Run.Cli;

/// <summary>
/// One verb and one scope over the governance commands that already exist.
///
/// The ten looked like ten unrelated verbs and are not: they are a small product of what you want to
/// do by what you want it done to. Writing it as a product is the point, and it shows two things a
/// flat list hid. The same act has three spellings — stopping a study is `pause-cells --study` or
/// `cancel-study --state pause`, and stopping a Site is `pause-cells --site` or `disable-site` — so
/// a person has to know which. And the holes are visible: nothing resumes everything, and nothing
/// discards one cell.
///
/// This is a LAYER, not a rename. Each pair dispatches to the command that already does the work, so
/// the old spellings keep working and no script breaks; renaming a surface people and scripts use is
/// a separate decision with its own cost. A pair with no command behind it refuses by naming what is
/// missing rather than pretending to act.
/// </summary>
internal static class GovernVerbs
{

    /// <summary>What a person wants done.</summary>
    internal static readonly string[] Verbs = ["stop", "resume", "order", "discard", "revive", "retire"];

    /// <summary>One pair of the product: the command behind it, or why there is none.</summary>
    internal sealed record Pair(string Verb, string Scope, string[]? Command, string? Hole);

    /// <summary>
    /// The whole product, holes included.
    ///
    /// `{study}`, `{site}`, `{cell}` and `{priority}` are filled from what the caller named. A pair
    /// whose Command is null has no command behind it, and its Hole says what would be needed.
    /// </summary>
    internal static readonly Pair[] Product =
    [
        new("stop", "everything", ["pause-cells", "--all-sites"], null),
        new("stop", "site", ["pause-cells", "--site", "{site}"], null),
        new("stop", "study", ["pause-cells", "--study", "{study}"], null),
        new("stop", "cell", ["hold-cell", "--study", "{study}", "--cell", "{cell}"], null),

        new("resume", "everything", null,
            "no command lifts every standing request at once; release-request takes one request id, "
            + "and monitor-line lists the ids standing now"),
        new("resume", "site", ["enable-site", "--site", "{site}"], null),
        new("resume", "study", ["cancel-study", "--study", "{study}", "--state", "run"], null),
        new("resume", "cell", null,
            "a held cell is released by the id of the hold, not by its name; monitor-line prints the "
            + "id and release-request lifts it"),

        new("order", "everything", null, "an order is relative to other work, so it is set on a study or a cell"),
        new("order", "site", null, "priority orders the offer, which is about work rather than about a machine"),
        new("order", "study", ["set-priority", "--study", "{study}", "--priority", "{priority}"], null),
        new("order", "cell", ["set-priority", "--study", "{study}", "--cell", "{cell}", "--priority", "{priority}"], null),

        new("discard", "everything", null, "there is no act that sets aside every study at once, and there should not be"),
        new("discard", "site", null, "a Site is taken out of service rather than discarded: stop it"),
        new("discard", "study", ["cancel-study", "--study", "{study}", "--state", "cancel"], null),
        new("discard", "cell", null,
            "no command discards one cell; hold-cell keeps it out of the offer and leaves it revivable"),

        new("revive", "everything", null, "a revival names the cell a person has fixed"),
        new("revive", "site", null, "a Site is put back in service rather than revived: resume it"),
        new("revive", "study", null, "a study is let run again rather than revived: resume it"),
        new("revive", "cell", ["retry-cell", "--study", "{study}", "--cell", "{cell}"], null),

        new("retire", "everything", null, "a supervisor is asked to stand down one Site at a time"),
        new("retire", "site", ["stop-supervisor", "--site", "{site}"], null),
        new("retire", "study", null, "a study is set aside rather than retired: discard it"),
        new("retire", "cell", null, "a cell is held rather than retired: stop it"),
    ];

    /// <summary>Which spellings do the same thing, said once so a person need not discover it.</summary>
    internal static readonly (string Pair, string Also)[] Synonyms =
    [
        ("stop site", "disable-site does the same thing by another name"),
        ("stop study", "cancel-study --state pause does the same thing by another name"),
    ];

    static string Named(string scope) => scope switch
    {
        "everything" => "--everything",
        "site" => "--site NAME",
        "study" => "--study NAME",
        _ => "--study NAME --cell NAME",
    };

    /// <summary>
    /// The product as a table, holes included.
    ///
    /// The holes are the point of printing it. A flat list of ten commands cannot show that nothing
    /// resumes everything or discards one cell; a table shows it at a glance, and shows where the
    /// same act has two names.
    /// </summary>
    internal static void PrintGrammar(TextWriter into)
    {
        // The grid carries the command's NAME only, so six rows by four columns fit on one screen
        // and the holes are visible as holes; the full form of each is spelled beneath it.
        var scopes = new[] { "everything", "site", "study", "cell" };
        into.WriteLine("what the ten governance commands are, as one verb by one scope\n");
        into.WriteLine($"{"",-10}{string.Join("", scopes.Select(s => s.PadRight(18)))}");
        foreach (var asked in Verbs)
        {
            into.Write(asked.PadRight(10));
            foreach (var scope in scopes)
            {
                var pair = Product.FirstOrDefault(p => p.Verb == asked && p.Scope == scope);
                into.Write((pair?.Command is null ? "—" : pair.Command[0]).PadRight(18));
            }
            into.WriteLine();
        }

        into.WriteLine("\nin full:");
        foreach (var pair in Product.Where(p => p.Command is not null))
        {
            into.WriteLine($"  govern {pair.Verb,-8} {Named(pair.Scope),-28} → {string.Join(" ", pair.Command!)}");
        }

        into.WriteLine("\nthe same act by another name:");
        foreach (var (pair, also) in Synonyms) into.WriteLine($"  {pair}: {also}");

        into.WriteLine("\nthe holes, which are holes and not omissions:");
        foreach (var hole in Product.Where(p => p.Hole is not null))
        {
            into.WriteLine($"  {hole.Verb} {hole.Scope}: {hole.Hole}");
        }
    }
}
