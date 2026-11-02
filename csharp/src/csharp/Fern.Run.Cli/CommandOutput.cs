using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Fern.Run.Cli;

// Terminal policy only: commands retain their payloads, wording and exit codes.
internal sealed class CommandOutput(string format, bool spectre)
{
    enum Projection { Human, Agent, Json }
    readonly Projection projection = format switch
    {
        "human" => Projection.Human,
        "agent" => Projection.Agent,
        "json" => Projection.Json,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// The column table Rust printed: every column padded to the widest cell it
    /// holds, two spaces between columns, and a dashed rule under the header.
    /// An empty projection prints "(no rows)" rather than a bare header, so a
    /// blank answer cannot be misread as one row that failed to render.
    /// </summary>
    /// Rust's `print_json_table`: left-aligned columns padded to the widest
    /// cell (header included), a dashed rule, two trailing spaces after every
    /// cell, and the literal "(no rows)" when there is nothing to show.
    // CHG: callers retain their existing newline convention explicitly.
    public static string ColumnTable(IReadOnlyList<IReadOnlyList<string>> rows, string[] columns, string newline)
    {
        if (rows.Count == 0)
        {
            return "(no rows)" + newline;
        }
        var widths = columns
            .Select((column, index) => rows.Select(row => row[index].Length).Append(column.Length).Max())
            .ToArray();
        var text = new StringBuilder();
        for (var index = 0; index < columns.Length; index++)
        {
            text.Append(columns[index].PadRight(widths[index])).Append("  ");
        }
        text.Append(newline);
        for (var index = 0; index < columns.Length; index++)
        {
            text.Append('-', widths[index]).Append("  ");
        }
        text.Append(newline);
        foreach (var row in rows)
        {
            for (var index = 0; index < columns.Length; index++)
            {
                text.Append(row[index].PadRight(widths[index])).Append("  ");
            }
            text.Append(newline);
        }
        return text.ToString();
    }

    /// <summary>Text for people, JSON for scripts; the same two words the Rust flag took.</summary>
    public enum Format
    {
        Text,
        Json,
    }

    /// <summary>
    /// A `--format` option that accepts EXACTLY `text` and `json`.
    ///
    /// System.CommandLine parses an enum case-insensitively, so a bare
    /// `Option&lt;Format&gt;` also accepts `Text`, `JSON`, `tExT`. Rust's
    /// `#[arg(value_enum)]` derives kebab-case value names and matches them
    /// case-sensitively, so it took the two lower-case words and nothing else.
    /// The validator restores that, and its error is a parse error, so the exit
    /// code is the 2 clap used.
    /// </summary>
    // CHG: the terminal owner declares the option directly; verbs keep their own descriptions.
    public static Option<Format> ExactFormatOption(string description)
    {
        var option = new Option<Format>("--format")
        {
            Description = description,
            DefaultValueFactory = _ => Format.Text,
        };
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (token.Value is not ("text" or "json"))
                {
                    result.AddError(
                        $"Cannot parse argument '{token.Value}' for option '--format' as expected type "
                        + "'text|json'.");
                    return;
                }
            }
        });
        return option;
    }

    public bool IsJson => projection == Projection.Json;
    public bool IsAgent => projection == Projection.Agent;

    public static Option<string> FormatOption(string description, string defaultValue)
    {
        var option = new Option<string>("--format") { Description = description, DefaultValueFactory = _ => defaultValue };
        option.AcceptOnlyFromAmong("human", "agent", "json");
        return option;
    }

    public void WriteJson(object payload) => Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));

    public void Write(string text, bool failure, bool line, Func<IRenderable>? render = null)
    {
        var human = projection == Projection.Human;
        var writer = human && failure ? Console.Error : Console.Out;
        if (spectre && human && !failure && !Console.IsOutputRedirected)
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Interactive = InteractionSupport.No })
                .Write(render is null ? new Text(line ? text + "\n" : text) : render());
        else if (line) writer.WriteLine(text);
        else writer.Write(text);
    }
}
