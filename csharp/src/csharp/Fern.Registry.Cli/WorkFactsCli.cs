using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Registry.Cli;

/// <summary>
/// <c>fern-registry work-facts --registry &lt;lifecycle.db&gt; --id &lt;identity&gt; --json</c>.
///
/// One read-only JSON document saying what Registry Home knows about one
/// identity — a Research Design card, a registered plan, an Analysis, or an
/// Evidence Release — so a planning tool can SHOW a result instead of copying
/// its numbers into a second store. An identity nobody registered is a normal
/// answer (<c>found=false</c>, exit 0), not a refusal.
///
/// Its exit codes are the caller's contract, and they are narrower than the
/// private subprocess protocol the rest of this executable speaks: 0 for a
/// printed document found or not, 1 for a usage refusal with one line on
/// stderr, 2 when Registry Home could not be opened or a query failed. Nothing
/// partial is ever written to stdout: the whole document is rendered first.
/// </summary>
static class WorkFactsCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitUnreadable = 2;

    const string Usage =
        "fern-registry: usage: fern-registry work-facts --registry <lifecycle.db> --id <identity> --json";

    public static int Run(string registry, string[] arguments, TextWriter stdout, TextWriter stderr)
    {
        var id = "";
        var json = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--registry":
                    // Already read by the dispatcher; skip its value here.
                    if (index + 1 >= arguments.Length)
                    {
                        stderr.WriteLine(Usage);
                        return ExitUsage;
                    }
                    index++;
                    break;
                case "--id":
                    if (index + 1 >= arguments.Length)
                    {
                        stderr.WriteLine(Usage);
                        return ExitUsage;
                    }
                    id = arguments[++index];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    stderr.WriteLine(Usage);
                    return ExitUsage;
            }
        }

        if (id.Trim().Length == 0 || !json)
        {
            stderr.WriteLine(Usage);
            return ExitUsage;
        }

        string rendered;
        try
        {
            using var reader = RegistryHome.OpenReader(registry);
            rendered = WorkFacts.Render(WorkFacts.Read(reader.Connection, id));
        }
        catch (RegistryFault fault)
        {
            stderr.WriteLine($"fern-registry: work-facts: {registry}: {fault.Message}");
            return ExitUnreadable;
        }
        catch (SqliteException error)
        {
            stderr.WriteLine($"fern-registry: work-facts: {registry}: {error.Message}");
            return ExitUnreadable;
        }

        stdout.Write(rendered);
        return ExitOk;
    }
}
