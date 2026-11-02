using System.Globalization;
using System.Text;

namespace Fern.Site.Cli;

/// <summary>
/// The `fern-site` executable: the Worker Site's installed identity, resident
/// session, and worker observation surface.
///
/// <code>
/// fern-site installation-identity
/// fern-site serve --stdio --work-root &lt;dir&gt; --worker-root &lt;dir&gt; --python &lt;path&gt; --timeout-seconds &lt;n&gt;
/// fern-site workers --work-root &lt;dir&gt;
/// </code>
///
/// CHG 2026-09-10: `execute-cell-phase` is retired. It ran one phase by calling
/// VerifyStagePackage, RunCellPhase and PackageStageReturn — the same three
/// functions `serve` calls — but could not move a byte, so it required the
/// package already staged on disk by other means. `serve` performs those three
/// and adds what makes them usable over a wire: content-addressed upload that
/// never re-sends a file the Site holds, an idempotent begin-cell seal, output
/// fetch on the same connection, streamed progress, and one process serving
/// phase after phase instead of a new one each time. Nothing was lost with the
/// verb, so this is a retired entrance rather than a retired capability.
///
/// CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
/// slice 4; owner: the remote installer is retired): `prepare-installation
/// --stdio`, the Site's half of installing a source archive and the control
/// set, is removed with the Home's half. A Site now asks the Home for each
/// attempt's code.
///
/// It decides nothing. Every rule about what a package may contain, what may be
/// spawned and what may be published lives in Fern.Site; this file reads argv,
/// calls three functions in order, and writes one JSON object to stdout.
///
/// Exit status is the contract the scheduler reads: 0 with a report on stdout,
/// or 1 with one line on stderr beginning `fern-site: `. There is no third
/// outcome, and stdout carries nothing but the report.
/// </summary>
public static class Program
{
    const string ExecutionSchema = "fern.remote_attempt_execution.v1";
    const string InstallationIdentitySchema = "fern.installation_identity.v4";
    const string RunPlanProtocol = "fern.run_plan_parent_child.v2";
    const string WorkerSiteRole = "worker-site";
    /// <summary>
    /// The largest whole second that still fits one Go duration, which is what
    /// the Go executable this replaces accepted. Kept exactly so a scheduler
    /// that has been passing a large timeout keeps getting the same answer.
    /// </summary>
    const ulong MaximumTimeoutSeconds = (ulong)(long.MaxValue / 1_000_000_000L);

    /// <summary>The active command surface.</summary>
    const string Usage =
        "usage: fern-site <installation-identity|serve --stdio --work-root <dir> --worker-root <dir> "
        + "--python <path> --timeout-seconds <n>|workers --work-root <dir>>";

    public static int Main(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] == "serve")
        {
            var serveFailure = Serve(arguments);
            if (serveFailure is null) return 0;
            Console.Error.Write("fern-site: " + serveFailure + "\n");
            return 1;
        }
        // One writer for the whole process: the report and any forwarded
        // progress line share a file descriptor, exactly as they did in Go, so
        // they must share a buffer or they can interleave mid-line.
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
        var failure = Run(arguments, stdout);
        stdout.Flush();
        if (failure is null)
        {
            return 0;
        }
        Console.Error.Write("fern-site: " + failure + "\n");
        return 1;
    }

    static readonly UTF8Encoding Utf8 = new(false, false);

    static string? Serve(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 10 || arguments[1] != "--stdio" || arguments[2] != "--work-root"
            || arguments[4] != "--worker-root" || arguments[6] != "--python" || arguments[8] != "--timeout-seconds")
        {
            return "serve requires --stdio --work-root <dir> --worker-root <dir> --python <path> --timeout-seconds <n>";
        }
        if (!ulong.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds == 0 || seconds > MaximumTimeoutSeconds)
        {
            return "--timeout-seconds must fit one positive Go duration";
        }
        var served = CellPhaseSite.ServeSession(
            Console.OpenStandardInput(), Console.OpenStandardOutput(), arguments[3], arguments[5], arguments[7], TimeSpan.FromSeconds(seconds));
        return served.IsOk ? null : served.Failure.Detail;
    }

    /// <summary>Returns null when the command succeeded, or the one-line reason it did not.</summary>
    internal static string? Run(IReadOnlyList<string> arguments, TextWriter stdout)
    {
        if (arguments.Count == 1 && arguments[0] == "installation-identity")
        {
            // One line, the field order of the identity record, a trailing
            // newline: the installer parses these bytes with unknown fields
            // forbidden, so nothing may be added here casually.
            stdout.Write(
                "{\"schema\":\"" + InstallationIdentitySchema + "\",\"role\":\"" + WorkerSiteRole
                + "\",\"run_plan_protocol\":\"" + RunPlanProtocol + "\"}\n");
            return null;
        }
        // CHG 2026-08-29 (Program 18, Site-C): one phase of a cell on this
        // Site's resident worker; the return carries no output bytes.
        // Registry-D: the machine's resident workers, from their folders alone.
        if (arguments.Count > 0 && arguments[0] == "workers")
        {
            if (arguments.Count != 3 || arguments[1] != "--work-root")
            {
                return "workers requires exactly --work-root <dir>";
            }
            foreach (var worker in ResidentPythonWorker.ListWorkers(arguments[2]))
            {
                var who = worker.Identity is null
                    ? "no worker.json"
                    : $"pid {worker.Identity.Pid} ({(worker.Alive ? "alive" : "gone")}) since {worker.Identity.StartedAt} {worker.Identity.Interpreter} in {worker.Identity.WorkingDirectory}";
                stdout.Write(
                    $"{worker.InterpreterFolder[..Math.Min(12, worker.InterpreterFolder.Length)]}/{worker.ImplementationFolder[..Math.Min(12, worker.ImplementationFolder.Length)]}: {who}; "
                    + $"running {worker.RunningCell ?? "nothing"}; {worker.Waiting} waiting; last receipt {worker.LastReceipt ?? "none"}\n");
            }
            return null;
        }
        return Usage;
    }
    /// <summary>
    /// One JSON string with the Go encoder's default escaping, which escapes
    /// the three HTML-significant characters. A Stage path or an Attempt id may
    /// contain one, and the scheduler compares these strings.
    /// </summary>
    static string Quote(string value) => GoJson.QuotedString(value, escapeHtml: true);
}
