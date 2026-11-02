using System.Diagnostics;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// Whether a Site can be reached and can reach the Home: slice S3 of
/// docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started.
///
/// The Gleam flow (<c>site_connection.gleam</c>) decides which question to ask
/// next and what the answers mean; this class asks each question of the Site
/// through the maintained SSH helper and hands the answer back. Every question
/// is read-only on the Site: print a marker, open the Home's health address,
/// test whether a file is executable.
/// </summary>
public static class SiteConnection
{
    /// <summary>Runs one shell command on the Site, with or without asking for the SSH entry's forwards.</summary>
    public interface IRemoteRunner
    {
        (bool Completed, string Output) Run(string sshTarget, bool throughForward, string shellCommand);
    }

    public sealed record Verdict(bool Reached, IReadOnlyList<string> Lines);

    /// <summary>Whether a supervisor holds the Site's office now, from the Registry at <paramref name="registry"/>.</summary>
    public static string Office(string? registry, string site)
    {
        if (registry is null) return "unread";
        using var reader = RegistryHome.OpenReader(registry);
        var row = RegistryOperations.ListSites(reader.Connection).FirstOrDefault(candidate => candidate.Name == site);
        if (row is null) return "unread";
        var supervisor = RegistryOperations.ReadSupervisor(reader.Connection, row.SiteId);
        return supervisor is not null
            && supervisor.HoldsOfficeAt(DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
            ? "held" : "free";
    }

    /// <summary>The runtime binary a loaded unit serving the Site starts: the word before <c>run-study-cells</c>.</summary>
    public static string? RuntimeBinary(IReadOnlyList<SiteFiles.LoadedUnit> loaded, string site)
    {
        foreach (var unit in loaded.Where(unit => unit.ActiveState is "active" or "activating"))
        {
            foreach (var words in unit.ExecStart)
            {
                var at = words.ToList().IndexOf("run-study-cells");
                if (at <= 0) continue;
                var siteAt = words.ToList().IndexOf("--site");
                if (siteAt >= 0 && siteAt + 1 < words.Count && words[siteAt + 1] == site) return words[at - 1];
            }
        }
        return null;
    }

    /// <summary>Ask the flow for the next question until it gives a verdict.</summary>
    public static Verdict Check(
        GleamProgram program, string profileText, string supervisorText, string office, string? binary, IRemoteRunner runner)
    {
        var answers = new JsonArray();
        for (var asked = 0; asked < 8; asked++)
        {
            var reply = program.Ask(new JsonObject
            {
                ["ask"] = "check_site_connection",
                ["profile"] = profileText,
                ["supervisor"] = supervisorText,
                ["office"] = office,
                ["binary"] = binary,
                ["answers"] = answers.DeepClone(),
            }, TimeSpan.FromSeconds(30), null);
            if (!reply.IsOk)
            {
                throw new GleamProgramException("the Gleam connection check did not answer: " + reply.RefusalDetail);
            }
            var answer = reply.Value["answer"] as JsonObject
                ?? throw new GleamProgramException("the Gleam connection check answered with no answer");
            var step = answer["step"] as JsonObject ?? throw new GleamProgramException("the connection check gave no step");
            switch (step["kind"]?.GetValue<string>())
            {
                case "reached":
                    return new Verdict(true, (step["notes"] as JsonArray ?? []).Select(note => note!.GetValue<string>()).ToList());
                case "not_reached":
                    return new Verdict(false, [step["sentence"]!.GetValue<string>()]);
                case "ask":
                    var probe = step["probe"] as JsonObject ?? throw new GleamProgramException("an ask carried no probe");
                    var target = answer["ssh_target"]?.GetValue<string>()
                        ?? throw new GleamProgramException("the connection check named no SSH target");
                    var outcome = Probe(runner, target, probe,
                        answer["home"]!.GetValue<string>(), answer["python"]!.GetValue<string>());
                    answers.Add(new JsonObject { ["probe"] = probe.DeepClone(), ["answer"] = outcome });
                    break;
                default:
                    throw new GleamProgramException("the connection check gave an unknown step: " + step.ToJsonString());
            }
        }
        throw new GleamProgramException("the connection check asked more than eight questions");
    }

    const string Reached = "fern-site-check: reached";
    const string HomeAnswered = "fern-home: answered";
    const string BinaryPresent = "fern-binary: present";

    static JsonObject Probe(IRemoteRunner runner, string target, JsonObject probe, string home, string python)
    {
        var kind = probe["kind"]!.GetValue<string>();
        var (command, marker, forward) = kind switch
        {
            "reach_site" => ($"printf '%s\\n' '{Reached}'", Reached, false),
            "home_from_site" => (HomeCommand(python, home), HomeAnswered, probe["through_forward"]!.GetValue<bool>()),
            "binary_on_site" => (
                $"if test -x '{probe["path"]!.GetValue<string>()}'; then printf '%s\\n' '{BinaryPresent}'; else printf '%s\\n' 'fern-binary: absent'; fi",
                BinaryPresent, false),
            _ => throw new GleamProgramException($"unknown probe {kind}"),
        };
        var (completed, output) = runner.Run(target, forward, command);
        if (completed && output.Contains(marker, StringComparison.Ordinal))
        {
            return new JsonObject { ["kind"] = "answered" };
        }
        var said = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.Length > 0) ?? "no answer";
        return new JsonObject { ["kind"] = "failed", ["detail"] = said };
    }

    /// <summary>
    /// Open the Home's health address from the Site with the Site's own
    /// Python. Any HTTP answer, even an error status, means the Home answered:
    /// the question is whether the address reaches it.
    /// </summary>
    static string HomeCommand(string python, string home) =>
        $$"""
        '{{python}}' - <<'FERN_HOME'
        import urllib.request, urllib.error
        try:
            urllib.request.urlopen('{{home}}v1/health', timeout=10)
            print('{{HomeAnswered}}')
        except urllib.error.HTTPError as error:
            print('{{HomeAnswered}}', error.code)
        except Exception as error:
            print('fern-home: silent', error)
        FERN_HOME
        """;

    /// <summary>
    /// The maintained helper's <c>exec</c>, with a control connection of its
    /// own so it never shares one with a supervisor, and without the SSH
    /// entry's forwards unless asked for them.
    /// </summary>
    public sealed class HelperRunner(string helper) : IRemoteRunner
    {
        static readonly TimeSpan Within = TimeSpan.FromSeconds(90);

        public (bool Completed, string Output) Run(string sshTarget, bool throughForward, string shellCommand)
        {
            var start = new ProcessStartInfo(helper)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            start.Environment["RUNPOD_SSH_TARGET"] = sshTarget;
            start.Environment["RUNPOD_CONTROL_PATH"] = "/tmp/fern-site-check-%C";
            start.Environment["RUNPOD_COMMAND_TIMEOUT_SECONDS"] = "60";
            if (!throughForward) start.Environment["RUNPOD_SSH_NO_FORWARDING"] = "1";
            foreach (var argument in new[] { "exec", "--", "sh", "-c", shellCommand }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{helper} could not be started");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Within))
            {
                process.Kill(entireProcessTree: true);
                return (false, $"no answer within {Within.TotalSeconds:F0} s");
            }
            return (process.ExitCode == 0, output.Result + "\n" + error.Result);
        }
    }
}
