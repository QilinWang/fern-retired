using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Fern.Scheduler;

/// <summary>
/// A Site's files, read from disk and from systemd, and checked against the
/// Site's declarations by the Gleam flow in
/// <c>tools/fern-scheduler-gleam/src/fern_scheduler/site_files.gleam</c>.
///
/// A Site's files are the SSH host entry this machine reaches it by
/// (<c>~/.ssh/fern-sites.config</c>, and any <c>Host fern-&lt;site&gt;</c>
/// block kept in <c>~/.ssh/config</c>) and the unit that keeps its supervisor
/// running (<c>fern-study-supervisor-&lt;site&gt;.service</c> with its drop-in
/// directory). Its declarations are its execution profile
/// (<c>profiles/&lt;site&gt;.json</c>) and its supervisor declaration
/// (<c>profiles/&lt;site&gt;.supervisor.json</c>).
///
/// This class reads; it writes nothing, opens no SSH connection and starts,
/// stops or reloads nothing. The flow decides. Slice F1 of
/// docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started.
/// </summary>
public static class SiteFiles
{
    /// <summary>Where the declarations and the files are. Tests point these at temporary directories.</summary>
    public sealed record Paths(string Profiles, string SshDirectory, string UnitDirectory)
    {
        public static Paths ForThisUser(string profiles)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new Paths(profiles, Path.Combine(home, ".ssh"), Path.Combine(home, ".config", "systemd", "user"));
        }
    }

    /// <summary>
    /// A service as systemd has loaded it. <paramref name="Temporary"/> is a
    /// unit created at run time (<c>systemd-run</c>), which has no file under
    /// the unit directory and is found only this way.
    /// </summary>
    /// <param name="Restarts">CHG 2026-09-25: <c>NRestarts</c>, the restarts systemd made by itself since a person last started it.</param>
    /// <param name="DropInPaths">CHG 2026-09-25: every drop-in file systemd read for it, from any of its drop-in folders.</param>
    /// <param name="ExecStartPre">CHG 2026-09-26 (slice F6): the commands run before the start; a rendered unit's checks name this machine's <c>fern-run</c> there.</param>
    public sealed record LoadedUnit(
        string Name, bool Temporary, string ActiveState, string SubState, bool NeedDaemonReload,
        IReadOnlyList<IReadOnlyList<string>> ExecStart, IReadOnlyDictionary<string, string> Environment,
        int Restarts = 0, IReadOnlyList<string>? DropInPaths = null,
        IReadOnlyList<IReadOnlyList<string>>? ExecStartPre = null);

    /// <summary>What systemd has loaded for this user.</summary>
    public interface ISystemdReader
    {
        IReadOnlyList<LoadedUnit> LoadedServices();
    }

    /// <summary>One finding: its kind (<c>matches</c>, <c>differs</c>, …) and the sentence a person reads.</summary>
    public sealed record Finding(string Kind, string Sentence)
    {
        public bool Matches => Kind == "matches";
    }

    public static string SupervisorDeclarationName(string site) => site + ".supervisor.json";

    /// <summary>The Sites that have a supervisor declaration, in name order.</summary>
    public static IReadOnlyList<string> DeclaredSites(string profiles) =>
        Directory.Exists(profiles)
            ? Directory.GetFiles(profiles, "*.supervisor.json")
                .Select(path => Path.GetFileName(path)[..^".supervisor.json".Length])
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>Ask the flow about one Site. A program that does not answer is a <see cref="GleamProgramException"/>.</summary>
    /// <param name="beforeStart">
    /// CHG 2026-09-25 (slice F2): the check the Site's unit runs before it
    /// starts; the unit's own state (starting, so not yet running) is not a finding.
    /// </param>
    public static IReadOnlyList<Finding> Check(
        GleamProgram program, string site, Paths paths, IReadOnlyList<LoadedUnit> loaded, bool beforeStart = false)
    {
        var profile = Path.Combine(paths.Profiles, site + ".json");
        if (!File.Exists(profile))
        {
            return [new Finding("undeclared", $"not checked: no execution profile {profile}")];
        }
        var request = Request(site, paths, loaded);
        request["before_start"] = beforeStart;
        var answered = program.Ask(request, TimeSpan.FromSeconds(30), null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException("the Gleam Site files check did not answer: " + answered.RefusalDetail);
        }
        if (answered.Value["answer"]?["findings"] is not JsonArray findings)
        {
            throw new GleamProgramException("the Gleam Site files check answered without findings: " + answered.Value.ToJsonString());
        }
        return findings
            .Select(node => node as JsonObject
                ?? throw new GleamProgramException("a Site files finding was not an object"))
            .Select(finding => new Finding(
                finding["kind"]?.GetValue<string>() ?? throw new GleamProgramException("a finding had no kind"),
                finding["sentence"]?.GetValue<string>() ?? throw new GleamProgramException("a finding had no sentence")))
            .ToList();
    }

    /// <summary>The request the flow reads: the declarations' text, the files' text, and the loaded units.</summary>
    internal static JsonObject Request(string site, Paths paths, IReadOnlyList<LoadedUnit> loaded)
    {
        var unitName = $"fern-study-supervisor-{site}.service";
        var dropIns = Path.Combine(paths.UnitDirectory, unitName + ".d");
        // CHG 2026-09-25: systemd reads drop-ins from more folders than
        // <unit>.d (a user.control folder, a prefix folder); every one it
        // read for the Site's unit is a drop-in here too.
        var listed = (Directory.Exists(dropIns)
                ? Directory.GetFiles(dropIns, "*.conf").Order(StringComparer.Ordinal)
                : Enumerable.Empty<string>())
            .Concat((loaded.FirstOrDefault(unit => unit.Name == unitName)?.DropInPaths ?? [])
                .Where(File.Exists))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new JsonObject
        {
            ["ask"] = "check_site_files",
            ["site"] = site,
            ["profile"] = File.ReadAllText(Path.Combine(paths.Profiles, site + ".json")),
            ["supervisor"] = ReadIfPresent(Path.Combine(paths.Profiles, SupervisorDeclarationName(site))),
            ["ssh_include"] = FileIfPresent(Path.Combine(paths.SshDirectory, "fern-sites.config")),
            ["ssh_config"] = FileIfPresent(Path.Combine(paths.SshDirectory, "config")),
            ["unit"] = FileIfPresent(Path.Combine(paths.UnitDirectory, unitName)),
            ["drop_ins"] = new JsonArray(listed.Select(path => (JsonNode)FileJson(path)!).ToArray()),
            ["loaded"] = new JsonArray(loaded.Select(unit => (JsonNode)new JsonObject
            {
                ["name"] = unit.Name,
                ["temporary"] = unit.Temporary,
                ["active_state"] = unit.ActiveState,
                ["sub_state"] = unit.SubState,
                ["need_daemon_reload"] = unit.NeedDaemonReload,
                ["exec_start"] = new JsonArray(unit.ExecStart
                    .Select(words => (JsonNode)new JsonArray(words.Select(word => (JsonNode)word).ToArray())).ToArray()),
                ["environment"] = new JsonObject(unit.Environment
                    .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
                ["restarts"] = unit.Restarts,
            }).ToArray()),
        };
    }

    /// <summary>
    /// CHG 2026-09-26 (slice F6): the digests of every binary that a loaded
    /// unit (its start commands, the commands before them, its environment) or
    /// a Site's two declarations name, which the installer's cleanup keeps
    /// whatever their age. The Gleam flow reads the paths (<c>named_paths</c>);
    /// a digest is the 64 hexadecimal characters of a published file's name, so
    /// a unit naming <c>deploy/…</c> and one naming <c>runtime/deploy/…</c> keep
    /// the same copy, and a Site's declaration naming its own copy on the Site
    /// keeps the copy here that was installed there. A declaration the flow
    /// cannot read refuses, so nothing is cleaned without it.
    /// </summary>
    public static IReadOnlySet<string> NamedDigests(
        GleamProgram program, string profiles, IReadOnlyList<LoadedUnit> loaded)
    {
        var declarations = new JsonArray(DeclaredSites(profiles)
            .Where(site => File.Exists(Path.Combine(profiles, site + ".json")))
            .Select(site => (JsonNode)new JsonObject
            {
                ["profile"] = File.ReadAllText(Path.Combine(profiles, site + ".json")),
                ["supervisor"] = File.ReadAllText(Path.Combine(profiles, SupervisorDeclarationName(site))),
            })
            .ToArray());
        var request = new JsonObject
        {
            ["ask"] = "named_paths",
            // Every absolute path: only a published file's digest is kept from it.
            ["roots"] = new JsonArray("/"),
            ["loaded"] = new JsonArray(loaded.Select(unit => (JsonNode)new JsonObject
            {
                ["name"] = unit.Name,
                ["temporary"] = unit.Temporary,
                ["active_state"] = unit.ActiveState,
                ["sub_state"] = unit.SubState,
                ["need_daemon_reload"] = unit.NeedDaemonReload,
                ["exec_start"] = new JsonArray(unit.ExecStart.Concat(unit.ExecStartPre ?? [])
                    .Select(words => (JsonNode)new JsonArray(words.Select(word => (JsonNode)word).ToArray())).ToArray()),
                ["environment"] = new JsonObject(unit.Environment
                    .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
            }).ToArray()),
            ["declarations"] = declarations,
        };
        var answered = program.Ask(request, TimeSpan.FromSeconds(30), null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException("the Gleam flow did not say which paths are named: " + answered.RefusalDetail);
        }
        if (answered.Value["answer"]?["unread"] is JsonArray { Count: > 0 } unread)
        {
            throw new GleamProgramException("a Site declaration could not be read, so what it names is unknown: "
                + string.Join("; ", unread.Select(node => node?.GetValue<string>())));
        }
        if (answered.Value["answer"]?["paths"] is not JsonArray paths)
        {
            throw new GleamProgramException("the Gleam flow answered without paths: " + answered.Value.ToJsonString());
        }
        return paths
            .Select(node => Path.GetFileName(node?.GetValue<string>() ?? ""))
            .Select(name => PublishedDigest.Match(name))
            .Where(match => match.Success)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    static readonly System.Text.RegularExpressions.Regex PublishedDigest =
        new("[0-9a-f]{64}", System.Text.RegularExpressions.RegexOptions.Compiled);

    static string? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    static JsonObject? FileIfPresent(string path) => File.Exists(path) ? FileJson(path) : null;

    static JsonObject FileJson(string path) => new() { ["path"] = path, ["text"] = File.ReadAllText(path) };

    /// <summary>
    /// The services loaded for this user, read with two <c>systemctl --user</c>
    /// calls: one to list them, one to show the properties the flow reads.
    /// Read-only.
    /// </summary>
    public sealed class SystemctlUser : ISystemdReader
    {
        static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

        public IReadOnlyList<LoadedUnit> LoadedServices()
        {
            var listed = Run(["--user", "list-units", "--all", "--type=service", "--no-legend", "--plain"]);
            var names = listed
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2)[0])
                .Where(name => name.EndsWith(".service", StringComparison.Ordinal))
                .ToList();
            if (names.Count == 0) return [];
            var shown = Run([
                "--user", "show", "-p", "Id,ActiveState,SubState,NeedDaemonReload,FragmentPath,Environment,ExecStart,ExecStartPre,NRestarts,DropInPaths",
                .. names]);
            return ParseShow(shown);
        }

        static string Run(IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("systemctl could not be started");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Within))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"systemctl {string.Join(' ', arguments.Take(3))} did not answer within {Within.TotalSeconds:F0} s");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"systemctl {string.Join(' ', arguments.Take(3))} failed: {error.Result.Trim()}");
            }
            return output.Result;
        }
    }

    /// <summary>
    /// Read <c>systemctl show</c> output: blocks separated by a blank line,
    /// one <c>Key=Value</c> per line. A start command is the words after
    /// <c>argv[]=</c> up to the next <c> ;</c>, one per <c>{ … }</c>; a unit
    /// under <c>/run/user/&lt;uid&gt;/systemd/transient/</c> was created at run time.
    /// </summary>
    internal static IReadOnlyList<LoadedUnit> ParseShow(string output)
    {
        var units = new List<LoadedUnit>();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            // systemd prints one ExecStart= line per start command; they are
            // all kept, not only the last.
            var execLines = new List<string>();
            var preLines = new List<string>();
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (line[..equals] == "ExecStart") execLines.Add(line[(equals + 1)..]);
                else if (line[..equals] == "ExecStartPre") preLines.Add(line[(equals + 1)..]);
                else properties[line[..equals]] = line[(equals + 1)..];
            }
            if (!properties.TryGetValue("Id", out var name)) continue;
            var starts = Commands(execLines);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var word in properties.GetValueOrDefault("Environment", "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = word.IndexOf('=');
                if (equals > 0) environment[word[..equals]] = word[(equals + 1)..];
            }
            units.Add(new LoadedUnit(
                name,
                properties.GetValueOrDefault("FragmentPath", "").Contains("/systemd/transient/", StringComparison.Ordinal),
                properties.GetValueOrDefault("ActiveState", ""),
                properties.GetValueOrDefault("SubState", ""),
                properties.GetValueOrDefault("NeedDaemonReload", "") == "yes",
                starts,
                environment,
                int.TryParse(properties.GetValueOrDefault("NRestarts", ""), out var restarts) ? restarts : 0,
                properties.GetValueOrDefault("DropInPaths", "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Commands(preLines)));
        }
        return units;
    }

    /// <summary>The words after each <c>argv[]=</c> up to the next <c> ;</c>, one command per <c>{ … }</c>.</summary>
    static List<IReadOnlyList<string>> Commands(IReadOnlyList<string> lines)
    {
        var commands = new List<IReadOnlyList<string>>();
        var exec = string.Join(" ", lines);
        for (var at = exec.IndexOf("argv[]=", StringComparison.Ordinal); at >= 0;
             at = exec.IndexOf("argv[]=", at + 1, StringComparison.Ordinal))
        {
            var from = at + "argv[]=".Length;
            var end = exec.IndexOf(" ;", from, StringComparison.Ordinal);
            var words = (end < 0 ? exec[from..] : exec[from..end])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commands.Add(words);
        }
        return commands;
    }
}

/// <summary>
/// The unit half of <c>update-site-files --site</c> (slice F2): write the
/// Site's supervisor unit as the Gleam flow renders it, move its drop-ins
/// aside when asked, and reload systemd only when the flow says a reload
/// cannot start a second supervisor. It never starts, stops or restarts a
/// unit. A drop-in is moved into a sibling folder systemd does not read, not
/// deleted, so it can be put back by hand. Owner's word, 2026-09-25: the
/// rendered unit calls the maintained SSH helper (Option A).
/// </summary>
public static class SiteUnitFiles
{
    /// <summary>What the unit runs before it starts, and how it opens the SSH session.</summary>
    /// <param name="Path">The unit's PATH: systemd's own lacks the folder <c>erl</c> is in, and the checks start the Gleam program.</param>
    public sealed record Inputs(
        string Path, string SessionProgram, string WorkingDirectory, IReadOnlyList<IReadOnlyList<string>> ChecksBeforeStart,
        string Provenance);

    /// <summary><c>systemctl --user daemon-reload</c>, and nothing else.</summary>
    public interface IReload
    {
        void DaemonReload();
    }

    /// <param name="Changes">Each fact the update changes: what the unit ran with, and what it runs with now.</param>
    public sealed record Result(
        string? Refused, bool Written, string UnitPath, IReadOnlyList<string> Moved, string? MovedTo, bool Reloaded,
        IReadOnlyList<(string Fact, string Before, string After)> Changes);

    public static Result Update(
        GleamProgram program, string site, SiteFiles.Paths paths, IReadOnlyList<SiteFiles.LoadedUnit> loaded,
        Inputs inputs, bool removeExtra, IReload reload)
    {
        var unitPath = Path.Combine(paths.UnitDirectory, $"fern-study-supervisor-{site}.service");
        if (!File.Exists(Path.Combine(paths.Profiles, site + ".json")))
        {
            return new Result($"Site {site} has no execution profile in {paths.Profiles}", false, unitPath, [], null, false, []);
        }
        var request = SiteFiles.Request(site, paths, loaded);
        request["ask"] = "update_site_unit";
        request["remove_extra"] = removeExtra;
        request["inputs"] = new JsonObject
        {
            ["path"] = inputs.Path,
            ["session_program"] = inputs.SessionProgram,
            ["working_directory"] = inputs.WorkingDirectory,
            ["checks_before_start"] = new JsonArray(inputs.ChecksBeforeStart
                .Select(check => (JsonNode)new JsonArray(check.Select(word => (JsonNode)word).ToArray())).ToArray()),
            ["provenance"] = inputs.Provenance,
        };
        var reply = program.Ask(request, TimeSpan.FromSeconds(30), null);
        if (!reply.IsOk) throw new GleamProgramException("the Gleam unit update did not answer: " + reply.RefusalDetail);
        var answer = reply.Value["answer"] as JsonObject ?? throw new GleamProgramException("the unit update answered with no answer");
        if (answer["kind"]?.GetValue<string>() == "refused")
        {
            return new Result(answer["sentence"]!.GetValue<string>(), false, unitPath, [], null, false, []);
        }
        var text = answer["text"]!.GetValue<string>();
        var write = answer["write"]!.GetValue<bool>();
        var remove = (answer["remove"] as JsonArray ?? []).Select(path => path!.GetValue<string>()).ToList();
        var needsReload = answer["reload"]!.GetValue<bool>();
        var changes = (answer["changes"] as JsonArray ?? [])
            .Select(change => (change!["fact"]!.GetValue<string>(), change["now"]!.GetValue<string>(),
                change["after"]!.GetValue<string>()))
            .ToList();

        // Everything this update replaces goes into one folder systemd does not
        // read: the drop-ins, and the unit file as it was.
        string? movedTo = null;
        if (remove.Count > 0 || (write && File.Exists(unitPath)))
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
            movedTo = unitPath + ".d.fern-removed-" + stamp;
            Directory.CreateDirectory(movedTo);
        }
        if (write)
        {
            Directory.CreateDirectory(paths.UnitDirectory);
            if (File.Exists(unitPath)) File.Copy(unitPath, Path.Combine(movedTo!, Path.GetFileName(unitPath)));
            var temporary = unitPath + ".fern-writing";
            File.WriteAllText(temporary, text);
            File.Move(temporary, unitPath, overwrite: true);
        }
        foreach (var path in remove)
        {
            var target = Path.Combine(movedTo!, Path.GetFileName(path));
            // Two folders may hold drop-ins of one name; the second keeps its folder's name too.
            if (File.Exists(target)) target = Path.Combine(movedTo!, Path.GetFileName(Path.GetDirectoryName(path)) + "-" + Path.GetFileName(path));
            File.Move(path, target);
        }
        if (needsReload) reload.DaemonReload();
        return new Result(null, write, unitPath, remove, movedTo, needsReload, changes);
    }

    /// <summary>The real reload: <c>systemctl --user daemon-reload</c>.</summary>
    public sealed class SystemctlReload : IReload
    {
        public void DaemonReload()
        {
            var start = new ProcessStartInfo("systemctl") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--user");
            start.ArgumentList.Add("daemon-reload");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("systemctl could not be started");
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("systemctl --user daemon-reload did not answer within 30 s");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"systemctl --user daemon-reload failed: {error.Result.Trim()}");
            }
        }
    }
}

/// <summary>
/// The SSH half of <c>update-site-files</c>: render every declared Site's SSH
/// entry into <c>~/.ssh/fern-sites.config</c>, make <c>~/.ssh/config</c> read
/// it first, and remove a hand-kept block for a declared Site only when it
/// sets exactly what the rendered entry sets. The Gleam flow decides the new
/// text; this class backs up, writes, and proves with <c>ssh -G</c> that ssh's
/// settings for every declared Site are the same afterwards, restoring the
/// backup when they are not. Owner's word, 2026-09-25: "yes".
/// </summary>
public static class SiteSshFiles
{
    /// <summary>The settings ssh would use for an alias, for the options the entries set.</summary>
    public interface ISshSettings
    {
        IReadOnlyDictionary<string, string> Effective(string alias);
    }

    public sealed record Result(
        bool Changed, bool IncludeAdded, IReadOnlyList<string> Removed,
        IReadOnlyList<(string Alias, string Reason)> Kept, IReadOnlyList<(string Site, string Detail)> Refused,
        string? Backup, IReadOnlyList<string> Problems);

    static readonly string[] Compared = ["hostname", "user", "port", "compression", "remoteforward", "exitonforwardfailure"];

    public static Result Update(GleamProgram program, SiteFiles.Paths paths, ISshSettings ssh, string provenance)
    {
        var declarations = new JsonArray();
        foreach (var site in SiteFiles.DeclaredSites(paths.Profiles))
        {
            var profile = Path.Combine(paths.Profiles, site + ".json");
            if (!File.Exists(profile)) continue;
            declarations.Add(new JsonObject
            {
                ["site"] = site,
                ["profile"] = File.ReadAllText(profile),
                ["supervisor"] = File.ReadAllText(Path.Combine(paths.Profiles, SiteFiles.SupervisorDeclarationName(site))),
            });
        }
        var configPath = Path.Combine(paths.SshDirectory, "config");
        var includePath = Path.Combine(paths.SshDirectory, "fern-sites.config");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var include = File.Exists(includePath) ? File.ReadAllText(includePath) : null;
        var reply = program.Ask(new JsonObject
        {
            ["ask"] = "update_site_ssh",
            ["declarations"] = declarations,
            ["config"] = config,
            ["provenance"] = provenance,
        }, TimeSpan.FromSeconds(30), null);
        if (!reply.IsOk) throw new GleamProgramException("the Gleam SSH update did not answer: " + reply.RefusalDetail);
        var answer = reply.Value["answer"] as JsonObject ?? throw new GleamProgramException("the SSH update answered with no answer");
        var newInclude = answer["include"]!.GetValue<string>();
        var newConfig = answer["config"]!.GetValue<string>();
        var aliases = (answer["aliases"] as JsonArray ?? []).Select(alias => alias!.GetValue<string>()).ToList();
        var removed = (answer["removed"] as JsonArray ?? []).Select(alias => alias!.GetValue<string>()).ToList();
        var kept = (answer["kept"] as JsonArray ?? [])
            .Select(pair => (pair!["alias"]!.GetValue<string>(), pair["reason"]!.GetValue<string>())).ToList();
        var refused = (answer["refused"] as JsonArray ?? [])
            .Select(pair => (pair!["site"]!.GetValue<string>(), pair["detail"]!.GetValue<string>())).ToList();
        var includeAdded = answer["include_added"]!.GetValue<bool>();
        if (newConfig == config && newInclude == include)
        {
            return new Result(false, false, [], kept, refused, null, []);
        }

        var before = aliases.ToDictionary(alias => alias, ssh.Effective);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        string? backup = null;
        if (File.Exists(configPath))
        {
            backup = configPath + ".fern-backup-" + stamp;
            File.Copy(configPath, backup);
        }
        var includeBackup = include is null ? null : includePath + ".fern-backup-" + stamp;
        if (includeBackup is not null) File.Copy(includePath, includeBackup);

        // The rendered entries are written before the config that reads them,
        // so there is no moment when a hand block is gone and its entry is not.
        WriteReplacing(includePath, newInclude);
        WriteReplacing(configPath, newConfig);

        var problems = new List<string>();
        foreach (var alias in aliases)
        {
            var after = ssh.Effective(alias);
            foreach (var option in Compared)
            {
                var was = before[alias].GetValueOrDefault(option, "(unset)");
                var now = after.GetValueOrDefault(option, "(unset)");
                if (was != now) problems.Add($"ssh's {option} for {alias} changed from {was} to {now}");
            }
        }
        if (problems.Count != 0)
        {
            if (backup is not null) File.Copy(backup, configPath, overwrite: true);
            else File.Delete(configPath);
            if (includeBackup is not null) File.Copy(includeBackup, includePath, overwrite: true);
            else File.Delete(includePath);
            problems.Add("both files were put back as they were");
        }
        return new Result(problems.Count == 0, includeAdded, removed, kept, refused, backup, problems);
    }

    /// <summary>Write through a temporary file in the same directory and rename, readable by the owner alone.</summary>
    static void WriteReplacing(string path, string text)
    {
        var temporary = path + ".fern-writing";
        File.WriteAllText(temporary, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary><c>ssh -G alias</c>: what ssh would use, read without connecting.</summary>
    public sealed class SshG : ISshSettings
    {
        public IReadOnlyDictionary<string, string> Effective(string alias)
        {
            var start = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-G");
            start.ArgumentList.Add(alias);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ssh could not be started");
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"ssh -G {alias} did not answer within 10 s");
            }
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var space = line.IndexOf(' ');
                if (space <= 0) continue;
                // ssh -G spells some names with capitals (`User`).
                var key = line[..space].ToLowerInvariant();
                if (Compared.Contains(key) && !settings.ContainsKey(key)) settings[key] = line[(space + 1)..];
            }
            return settings;
        }
    }
}
