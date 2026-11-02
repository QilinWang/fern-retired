using System.Text;
using Fern.Registry;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>
/// The systemd user unit that keeps one Site's supervisor resident.
///
/// CHG 2026-09-11 night (slice 1 of the cell port, design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// rows 26 and 33). It is <see cref="ServiceUnit"/> retargeted from the retired
/// daemon to `fern-run run-study-cells --serve --site <name>`: the same
/// mechanical policy, restated for the process that now minds a machine.
/// systemd owns start, signal, cgroup termination, backoff and restart, and
/// nothing else; exit 2 is a terminal configuration and is never restarted,
/// 1 and 3 are retried after five seconds, 0 stays down. It never acquires or
/// clears an office: the supervisor claims its own on start and the Registry
/// arbitrates.
/// </summary>
/// <param name="SchedulerProgram">
/// CHG 2026-09-26 (slice F7 of how-a-site-is-reached-and-started): the exported
/// Gleam scheduler program the supervisor runs its round and its office lease
/// with. It starts <c>erl</c>, which systemd's own PATH lacks, so
/// <paramref name="ErlangDirectory"/> joins the unit's PATH with it. On
/// 2026-09-26 a hand-made unit without it failed on "erl: not found" every 20 s.
/// </param>
/// <param name="FernCompile">The compiler the supervisor compiles cards with (<c>--fern-compile</c> and <c>FERN_COMPILE_BIN</c>).</param>
/// <param name="Environment">Further environment the supervisor starts with, such as the numerical thread limits.</param>
public sealed record StudySupervisorUnitConfig(
    string FernRunBinary,
    string Registry,
    string SiteName,
    string Python,
    string WorkRoot,
    string? ResultsRoot,
    long LeaseSeconds,
    int IdleSeconds,
    string? SchedulerProgram = null,
    string? ErlangDirectory = null,
    string? FernCompile = null,
    IReadOnlyList<(string Name, string Value)>? Environment = null);

public static class StudySupervisorUnit
{
    public static string UnitName(string siteName) => $"fern-study-supervisor-{siteName}.service";

    public static ServiceUnitRender Render(StudySupervisorUnitConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SiteName) || config.SiteName.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            return new Refused($"a Site name in a unit is letters, digits, '-' or '_', got \"{config.SiteName}\"");
        }
        if (config.LeaseSeconds < 60 || config.IdleSeconds < 1)
        {
            return new Refused($"a supervisor lease is at least 60 s and its idle wait at least 1 s, got {config.LeaseSeconds} and {config.IdleSeconds}");
        }
        var remote = Uri.TryCreate(config.Registry, UriKind.Absolute, out var address)
            && address.Scheme is "http" or "https";
        if (remote && config.Registry.Any(c => char.IsWhiteSpace(c) || c is '%' or '"' or '\\'))
            return new Refused("the Home URL contains characters systemd would reinterpret");
        if ((config.SchedulerProgram is null) != (config.ErlangDirectory is null))
        {
            return new Refused("a scheduler program starts erl: name the program and the directory erl is in together");
        }
        var paths = new List<string> { config.FernRunBinary, config.Python, config.WorkRoot };
        if (config.SchedulerProgram is not null) paths.Add(config.SchedulerProgram);
        if (config.ErlangDirectory is not null) paths.Add(config.ErlangDirectory);
        if (config.FernCompile is not null) paths.Add(config.FernCompile);
        if (!remote) paths.Add(config.Registry);
        if (config.ResultsRoot is not null) paths.Add(config.ResultsRoot);
        foreach (var path in paths)
        {
            if (SystemdPath.Check(path) is Refused refused)
            {
                return new Refused(refused.Detail);
            }
        }
        var results = config.ResultsRoot is null ? "" : $" --results-root {config.ResultsRoot}";
        var environment = new List<(string Name, string Value)>();
        if (config.ErlangDirectory is not null)
        {
            environment.Add(("PATH", $"{config.ErlangDirectory}:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"));
        }
        if (config.FernCompile is not null) environment.Add(("FERN_COMPILE_BIN", config.FernCompile));
        environment.AddRange(config.Environment ?? []);
        foreach (var (name, value) in environment)
        {
            if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))
                || value.Any(c => char.IsWhiteSpace(c) || c is '%' or '"' or '\\'))
            {
                return new Refused($"the environment {name}={value} holds characters systemd would reinterpret");
            }
        }
        var extra = (config.FernCompile is null ? "" : $" --fern-compile {config.FernCompile}")
            + (config.SchedulerProgram is null ? "" : $" --scheduler-program {config.SchedulerProgram}");
        var unit =
            "# Generated by fern-run install-supervisor-service; do not hand-edit.\n"
            + "[Unit]\n"
            + $"Description=Fern study supervisor for Site {config.SiteName} ({config.Registry})\n"
            + (remote ? "" : $"ConditionPathExists={config.Registry}\n")
            + $"ConditionPathExists={config.FernRunBinary}\n\n"
            + "[Service]\n"
            + "Type=simple\n"
            + $"WorkingDirectory={config.WorkRoot}\n"
            + string.Concat(environment.Select(pair => $"Environment={pair.Name}={pair.Value}\n"))
            + $"ExecStart={config.FernRunBinary} run-study-cells --serve {(remote ? "--home" : "--store")} {config.Registry} --site {config.SiteName}"
            // CHG 2026-09-11 night (slice 6, row 31): no --source-root; the
            // root a cell trains from is what install-study-source recorded.
            + $" --python {config.Python} --work-root {config.WorkRoot}"
            + $" --lease-seconds {config.LeaseSeconds} --idle-seconds {config.IdleSeconds}{results}{extra}\n"
            + "Restart=on-failure\n"
            + "RestartPreventExitStatus=2\n"
            + "RestartSec=5s\n"
            + "KillMode=control-group\n\n"
            + "[Install]\n"
            + "WantedBy=default.target\n";
        return new Accepted<byte[]>(Encoding.UTF8.GetBytes(unit));
    }
}
