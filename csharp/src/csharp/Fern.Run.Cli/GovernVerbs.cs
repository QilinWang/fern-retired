using System.CommandLine;

namespace Fern.Run.Cli;

/// <summary>Wires the grammar in GovernGrammar to one command that dispatches to the nine.</summary>
internal static partial class GovernWiring
{
    internal static void Add(Command parent)
    {
        var verb = new Argument<string>("verb") { Description = "stop, resume, order, discard, revive or retire." };
        var registry = new Option<string>("--registry") { Description = "Registry Home holding the work." };
        var everything = new Option<bool>("--everything") { Description = "Over every Site and every study." };
        var site = new Option<string?>("--site") { Description = "Over one Site." };
        var study = new Option<string?>("--study") { Description = "Over one study." };
        var cell = new Option<string?>("--cell") { Description = "Over one cell; needs --study." };
        var priority = new Option<string?>("--priority") { Description = "With order: immediate, urgent, normal or slow." };
        var reason = new Option<string?>("--reason") { Description = "Why, in one sentence." };
        var by = new Option<string?>("--by") { Description = "Who asks." };
        var grammar = new Option<bool>("--grammar") { Description = "Print the verb-by-scope table, holes included, and do nothing." };

        var command = new Command("govern",
            "One verb and one scope over the governance commands: stop, resume, order, discard, revive, retire, "
            + "over everything, one Site, one study or one cell. A layer, not a rename: the old spellings still work.");
        command.Arguments.Add(verb);
        foreach (var option in new Option[] { registry, everything, site, study, cell, priority, reason, by, grammar })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            if (parsed.GetValue(grammar))
            {
                GovernVerbs.PrintGrammar(Console.Out);
                return 0;
            }
            var asked = parsed.GetValue(verb) ?? "";
            if (!GovernVerbs.Verbs.Contains(asked, StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"govern: the verbs are {string.Join(", ", GovernVerbs.Verbs)}; not {asked}");
                return 2;
            }
            var named = Scope(parsed.GetValue(everything), parsed.GetValue(site), parsed.GetValue(study), parsed.GetValue(cell));
            if (named is null)
            {
                Console.Error.WriteLine(
                    "govern: name exactly one scope: --everything, --site NAME, --study NAME, or --study NAME --cell NAME");
                return 2;
            }
            var pair = GovernVerbs.Product.FirstOrDefault(p => p.Verb == asked && p.Scope == named);
            if (pair is null || pair.Command is null)
            {
                Console.Error.WriteLine($"govern: nothing {asked}s {(named == "everything" ? "everything" : "one " + named)}: "
                                        + (pair?.Hole ?? "that pair is not in the product"));
                return 2;
            }
            if (asked == "order" && parsed.GetValue(priority) is null)
            {
                Console.Error.WriteLine("govern order: --priority says where in the offer it goes: immediate, urgent, normal or slow");
                return 2;
            }

            var arguments = new List<string>();
            foreach (var piece in pair.Command)
            {
                arguments.Add(piece
                    .Replace("{site}", parsed.GetValue(site) ?? "")
                    .Replace("{study}", parsed.GetValue(study) ?? "")
                    .Replace("{cell}", parsed.GetValue(cell) ?? "")
                    .Replace("{priority}", parsed.GetValue(priority) ?? ""));
            }
            if (parsed.GetValue(registry) is { Length: > 0 } home) { arguments.Add("--registry"); arguments.Add(home); }
            // Only the commands that take them, or the parse refuses; the product knows which.
            var target = parent.Subcommands.FirstOrDefault(c => c.Name == pair.Command[0]);
            if (target is null)
            {
                Console.Error.WriteLine($"govern: this build has no {pair.Command[0]} to dispatch to");
                return 2;
            }
            if (target.Options.Any(option => option.Name == "--reason"))
            {
                arguments.Add("--reason");
                arguments.Add(parsed.GetValue(reason) ?? "");
            }
            else if (parsed.GetValue(reason) is { Length: > 0 } unkept)
            {
                Console.Error.WriteLine(
                    $"govern: {pair.Command[0]} records no reason, so “{unkept}” is kept nowhere; running it anyway");
            }
            if (target.Options.Any(option => option.Name == "--by") && parsed.GetValue(by) is { Length: > 0 } who)
            {
                arguments.Add("--by");
                arguments.Add(who);
            }

            Console.Error.WriteLine("govern → fern-run " + string.Join(" ", arguments));
            return parent.Parse(arguments.ToArray()).Invoke();
        });
        parent.Subcommands.Add(command);
    }

    static string? Scope(bool everything, string? site, string? study, string? cell)
    {
        var named = (everything ? 1 : 0) + (site is null ? 0 : 1) + (study is null ? 0 : 1);
        if (named != 1) return null;
        if (cell is not null) return study is null ? null : "cell";
        return everything ? "everything" : site is not null ? "site" : "study";
    }
}
