using System.Text.Json;
using Fern.RunData;

namespace Fern.Registry.Cli;

/// <summary>
/// The list-runs command: a bounded, read-only projection of the per-lane
/// <c>runs</c> table. Same flags as the Go `fern-home list-runs` it replaces:
/// `--db` OR `--campaign-root` (exactly one), `--dataset`, `--model`,
/// `--status` (default `done`, and one of a closed five-name vocabulary), and
/// `--limit` applied AFTER every lane has been read, so a campaign limit never
/// silently truncates one lane's contribution.
///
/// One JSON report on stdout and exit 0; any refusal prints on stderr and
/// exits 1, which is the exit the Go verb gave for every one of these. The
/// report's two members are the same names and values Go emitted
/// (`schema` = fern.run_list.v1, `runs`); only their order in the object
/// differs, and every caller parses rather than diffs it.
///
/// The projection itself is NOT reimplemented here. Fern.RunData's
/// RunViewsReader already owns it — it and the Go package are two ports of the
/// same retired Rust reader — and a second copy inside Fern.Registry would be
/// a second owner of one authority answer.
/// </summary>
static class RunListCli
{
    /// <summary>The closed status vocabulary; Go refused anything else before opening a database.</summary>
    static readonly string[] KnownStatuses = ["pending", "running", "done", "failed", "stale"];

    public static int Run(string[] arguments, TextWriter stdout, TextWriter stderr)
    {
        string? database = null;
        string? campaignRoot = null;
        string? dataset = null;
        string? model = null;
        var status = "done";
        ulong limit = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var flag = arguments[index];
            switch (flag)
            {
                case "--db":
                case "--campaign-root":
                case "--dataset":
                case "--model":
                case "--status":
                case "--limit":
                    if (index + 1 >= arguments.Length)
                    {
                        stderr.WriteLine($"fern-registry: list-runs: flag {flag} needs a value");
                        return RegistryCli.ExitFault;
                    }

                    index++;
                    var value = arguments[index];
                    switch (flag)
                    {
                        case "--db": database = value; break;
                        case "--campaign-root": campaignRoot = value; break;
                        // An explicitly supplied empty filter stays a filter,
                        // the way Go's flags.Visit kept it; only an absent flag
                        // means "every dataset".
                        case "--dataset": dataset = value; break;
                        case "--model": model = value; break;
                        case "--status": status = value; break;
                        case "--limit":
                            if (!ulong.TryParse(value, out limit))
                            {
                                stderr.WriteLine($"fern-registry: list-runs: --limit {value} is not a count");
                                return RegistryCli.ExitFault;
                            }

                            break;
                    }

                    break;
                default:
                    stderr.WriteLine(flag.StartsWith("--", StringComparison.Ordinal)
                        ? $"fern-registry: list-runs: unknown flag {flag}"
                        : "fern-registry: list-runs accepts flags only");
                    return RegistryCli.ExitFault;
            }
        }

        if (!KnownStatuses.Contains(status, StringComparer.Ordinal))
        {
            stderr.WriteLine("fern-registry: list-runs --status must be one of pending, running, done, failed, stale");
            return RegistryCli.ExitFault;
        }

        try
        {
            var report = RunViewsReader.List(new RunListRequest(
                DatabasePath: database,
                CampaignRoot: campaignRoot,
                Dataset: dataset,
                Model: model,
                Status: status,
                Limit: limit));
            stdout.WriteLine(JsonSerializer.Serialize(report, RunDataJson.Options));
            return RegistryCli.ExitOk;
        }
        catch (RunDataFault fault)
        {
            stderr.WriteLine($"fern-registry: {fault.Message}");
            return RegistryCli.ExitFault;
        }
    }
}
