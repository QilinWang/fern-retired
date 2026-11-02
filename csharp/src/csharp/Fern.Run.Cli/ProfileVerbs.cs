using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Fern.Scheduler;
using Spectre.Console;

namespace Fern.Run.Cli;

/// <summary>
/// CLI Pass 1 — <c>profile init</c> writes an explicit local or SSH execution
/// profile template at <c>profiles/&lt;profile-id&gt;.json</c>; <c>profile validate</c>
/// sends one typed request to the Scheduler validation owner and projects its
/// typed report for a person, an agent, or a JSON reader.
///
/// The init bytes were ported 2026-08-28 from fern-cli-rs
/// experiment_front_door.rs init_profile and fern-execution-profile's
/// local_template / ssh_template. The serde field order and pretty layout are
/// reproduced by hand because the template is a declaration the operator
/// edits, not a record a reader produces.
/// </summary>
public static class ProfileVerbs
{
    const string Schema = "fern.execution_profile.v5";

    sealed class ProfileRefusal(string detail) : Exception(detail);

    public static void Add(Command parent)
    {
        var profile = new Command("profile", "Execution profiles: the declaration of one worker machine.");
        profile.Subcommands.Add(Init());
        profile.Subcommands.Add(Validate());
        parent.Subcommands.Add(profile);
    }

    /// <summary>
    /// `profile validate PROFILE --observer host|codex-sandbox [--format]`:
    /// read a profile and perform its no-write machine and storage checks. The
    /// observer is required because the Codex sandbox has no stable environment
    /// variable that tells it apart from the host.
    /// </summary>
    static Command Validate()
    {
        var profileId = new Argument<string>("profile-id") { Description = "Registered execution profile id, for example local-4070." };
        var observer = new Option<string>("--observer")
        {
            Description = "Where this command is running: host or codex-sandbox.",
            Required = true,
        };
        observer.AcceptOnlyFromAmong("host", "codex-sandbox");
        var format = new Option<string>("--format")
        {
            Description = "Output projection: human, agent, or json.",
            DefaultValueFactory = _ => "human",
        };
        format.AcceptOnlyFromAmong("human", "agent", "json");
        var command = new Command("validate", "Read a profile and perform its no-write machine and storage checks.");
        command.Arguments.Add(profileId);
        command.Options.Add(observer);
        command.Options.Add(format);
        command.SetAction(parsed =>
        {
            try
            {
                var request = new ProfileValidationRequest(
                    WorkspaceRoot(),
                    parsed.GetValue(profileId)!,
                    parsed.GetValue(observer) == "codex-sandbox"
                        ? ObserverContext.CodexSandbox
                        : ObserverContext.Host,
                    BinaryVersionReport.FromThisBinary().CrateVersion);
                var report = Preflight.ValidateProfile(request);
                var projection = parsed.GetValue(format) switch
                {
                    "human" => ProfileValidationFormat.Human,
                    "agent" => ProfileValidationFormat.Agent,
                    "json" => ProfileValidationFormat.Json,
                    _ => throw new InvalidOperationException("the parser accepted an unknown profile validation format"),
                };
                return ProfileValidationProjection.Write(report, projection, Console.Out);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"profile validate failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    /// <summary>
    /// The checkout root: the nearest ancestor holding the profiles directory, as Rust resolved it.
    /// CHG 2026-09-26: that directory is config/profiles/ (Fern.Registry.RepositoryLayout).
    /// </summary>
    static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !Directory.Exists(WorktreeControlPaths.Profiles(directory.FullName)))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    static Command Init()
    {
        var profileId = new Argument<string>("profile-id") { Description = "New profile id, for example local-4070." };
        var executor = new Option<string>("--executor") { DefaultValueFactory = _ => "local", Description = "How Fern reaches the worker machine: local or ssh." };
        var sshDestination = new Option<string>("--ssh-destination") { Description = "SSH user/host or configured alias. Required only with --executor ssh." };
        var registryHome = new Option<string>("--registry-home") { Description = "Local execution profile that owns the one Registry and Artifact Home." };
        var workspaceRoot = new Option<string>("--workspace-root") { Description = "Workspace root on the worker machine. Defaults to the current directory for a local profile." };
        var homeRoot = new Option<string>("--home-root") { Description = "Home directory on the worker machine. Defaults to $HOME for a local profile." };
        var command = new Command("init", "Write an explicit local or SSH template at config/profiles/<profile-id>.json.");
        command.Arguments.Add(profileId);
        foreach (var option in new Option[] { executor, sshDestination, registryHome, workspaceRoot, homeRoot })
        {
            command.Options.Add(option);
        }
        command.SetAction(parse =>
        {
            try
            {
                var kind = parse.GetValue(executor)!;
                if (kind is not ("local" or "ssh"))
                {
                    Console.Error.WriteLine($"error: unknown executor {Quote(kind)}; valid values are local and ssh");
                    return 2;
                }
                var id = parse.GetValue(profileId)!;
                var path = DefaultPath(id);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new ProfileRefusal($"{path} already exists; edit it deliberately or choose a new profile id");
                }
                string json;
                if (kind == "local")
                {
                    if (parse.GetValue(sshDestination) is not null || parse.GetValue(registryHome) is not null)
                    {
                        throw new ProfileRefusal(
                            "--ssh-destination and --registry-home belong only to --executor ssh; no profile was written");
                    }
                    var workspace = parse.GetValue(workspaceRoot) ?? Directory.GetCurrentDirectory();
                    var home = parse.GetValue(homeRoot) ?? Environment.GetEnvironmentVariable("HOME")
                        ?? throw new ProfileRefusal("HOME is not set; provide --home-root so Fern can choose the local Python path");
                    json = Template(id, workspace, home, ssh: null);
                }
                else
                {
                    var destination = parse.GetValue(sshDestination)
                        ?? throw new ProfileRefusal("--executor ssh requires --ssh-destination; no profile was written");
                    var registry = parse.GetValue(registryHome)
                        ?? throw new ProfileRefusal("--executor ssh requires --registry-home; no profile was written");
                    var workspace = parse.GetValue(workspaceRoot)
                        ?? throw new ProfileRefusal("--executor ssh requires the remote --workspace-root; Fern will not copy the controller's path by guess");
                    var home = parse.GetValue(homeRoot)
                        ?? throw new ProfileRefusal("--executor ssh requires the remote --home-root; Fern will not guess whether the worker uses /root or /home/<user>");
                    json = Template(id, workspace, home, ssh: (destination, registry));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteDurably(path, json);
                Console.Out.WriteLine($"wrote {path}");
                Console.Out.WriteLine($"next: fern-run profile validate {id} --observer host");
                return 0;
            }
            catch (ProfileRefusal refusal)
            {
                Console.Error.WriteLine($"profile failed: {refusal.Message}");
                return 1;
            }
        });
        return command;
    }

    /// <summary>fern-execution-profile validate_profile_id + default_path.</summary>
    static string DefaultPath(string profileId)
    {
        var valid = profileId.Length is > 0 and <= 80
            && !profileId.StartsWith('-') && !profileId.EndsWith('-')
            && profileId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
        if (!valid)
        {
            throw new ProfileRefusal("must be 1–80 lowercase letters, digits, and interior hyphens; this keeps its file path predictable");
        }
        return Path.Combine(Fern.Registry.RepositoryLayout.ProfilesDirectory, profileId + ".json");
    }

    /// <summary>local_template, with ssh_template's four overrides when ssh is given; serde field order and 2-space layout.</summary>
    static string Template(string id, string workspace, string home, (string Destination, string Registry)? ssh)
    {
        static string J(string path, string tail) => Quote(Path.Join(path, tail));
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"schema\": {Quote(Schema)},\n");
        sb.Append($"  \"profile_id\": {Quote(id)},\n");
        sb.Append($"  \"executor\": {(ssh is null ? "\"local\"" : "\"ssh\"")},\n");
        if (ssh is { } s)
        {
            sb.Append($"  \"ssh_destination\": {Quote(s.Destination)},\n");
            sb.Append($"  \"registry_home_profile_id\": {Quote(s.Registry)},\n");
        }
        sb.Append("  \"device\": \"cuda\",\n");
        sb.Append($"  \"source_root\": {Quote(workspace)},\n");
        sb.Append($"  \"python\": {J(home, ".venv/bin/python")},\n");
        sb.Append("  \"worker\": {\n    \"module\": \"fern_train.worker\",\n    \"slots\": 1\n  },\n");
        sb.Append("  \"supervisor\": {\n    \"max_restarts\": 3,\n    \"restart_delay_seconds\": 5,\n    \"max_target_seconds\": 3600,\n    \"max_sweep_seconds\": 86400\n  },\n");
        sb.Append("  \"accelerator\": {\n    \"min_memory_gib\": 1\n  },\n");
        sb.Append($"  \"known_data_root\": {J(home, ".cache/serie/known")},\n");
        // CHG: omitted local registry_path follows the selected installed context.
        sb.Append($"  \"campaign_root\": {J(workspace, "results/paper_campaign")},\n");
        sb.Append($"  \"artifact_root\": {J(workspace, "results/runs")},\n");
        sb.Append($"  \"work_root\": {J(workspace, Fern.Registry.RepositoryLayout.WorkerWorkRoot)},\n"); // CHG 2026-09-26: was tmp/fern-run-worker
        sb.Append("  \"warn_free_gib\": 50,\n  \"warn_free_percent\": 10\n}\n");
        return sb.ToString();
    }

    static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Rust write_file_durably: write beside, fsync, rename.</summary>
    static void WriteDurably(string path, string content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: false);
    }
}

enum ProfileValidationFormat
{
    Human,
    Agent,
    Json,
}

/// <summary>
/// The three terminal projections of one complete validation report. These
/// methods receive report data only: they cannot reread a profile, observe a
/// machine, inspect a Registry, or change the validation verdict.
/// </summary>
static class ProfileValidationProjection
{
    const string Command = "profile validate";
    const string JsonSchema = "fern.profile_validation.v1";

    /// <summary>
    /// The one process mapping for this operation: ready 0, needs_host_check 3,
    /// any other Fern verdict 2. An unexpected exception is mapped to 1 by the
    /// command adapter before a projection is attempted.
    /// </summary>
    public static int Write(ProfileValidationResult report, ProfileValidationFormat format, TextWriter writer)
    {
        switch (format)
        {
            case ProfileValidationFormat.Human:
                var console = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Ansi = AnsiSupport.Detect,
                    ColorSystem = ColorSystemSupport.Detect,
                    Interactive = InteractionSupport.No,
                    Out = new AnsiConsoleOutput(writer),
                });
                // Spectre otherwise uses the redirected process's default
                // width and inserts terminal-dependent line breaks into bytes
                // that tests and operators treat as one report.
                var human = RenderHuman(report);
                console.Profile.Width = human.Split('\n').Max(line => line.Length) + 1;
                console.Write(new Text(human));
                break;
            case ProfileValidationFormat.Agent:
                writer.Write(RenderAgent(report));
                break;
            case ProfileValidationFormat.Json:
                writer.WriteLine(RenderJson(report));
                break;
            default:
                throw new InvalidOperationException("profile validation gained an output format with no projection");
        }

        return report.Status switch
        {
            ProfileValidationStatus.Ready => 0,
            ProfileValidationStatus.NeedsHostCheck => 3,
            _ => 2,
        };
    }

    /// <summary>One concise, complete problem list for an agent.</summary>
    static string RenderAgent(ProfileValidationResult report)
    {
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append('\n');

        Line($"{Command}: {Wire(report.Status)}; profile={report.ProfileId}; observer={Preflight.Label(report.Observer)}; "
            + $"facts={(report.ExecutionFactsFrom is { } source ? Preflight.Label(source) : "none")}; "
            + $"gpu_work_started=no; problems={report.Problems.Count}");
        foreach (var problem in report.Problems)
        {
            Line($"{Preflight.Wire(problem.Code)}: {problem.Meaning}");
            Line($"fix: {problem.FixCommand}");
        }

        Line($"next: {report.NextStep}");
        return text.ToString();
    }

    /// <summary>Rust print_text_report, line for line.</summary>
    static string RenderHuman(ProfileValidationResult report)
    {
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append('\n');

        Line($"{Command}: {Wire(report.Status)}");
        Line("GPU work started: no");
        Line($"observer: {Preflight.Label(report.Observer)}");
        Line("execution facts from: "
            + (report.ExecutionFactsFrom is { } source
                ? Preflight.Label(source)
                : "none (the probe did not reach an execution environment)"));
        Line($"profile: {report.ProfileId} ({report.ProfilePath})");
        if (report.ProfileRevision is { } revision)
        {
            Line($"profile revision: {revision}");
        }

        if (report.Executor is { } executor && report.Device is { } device)
        {
            Line($"execution: {executor} / {device}");
        }

        if (report.ObservedExecution is { } observed)
        {
            Line($"Python: {observed.Python.RequestedExecutable} (reported {observed.Python.SysExecutable}, torch "
                + $"{observed.Torch.Version}, CUDA available: {(observed.CudaAvailable ? "true" : "false")})");
            Line($"observed environment content: {ObservedExecutionProbe.EnvironmentContentDigest(observed)}");
            var current = observed.CudaDevices
                .FirstOrDefault(device => observed.CudaCurrentDevice is { } index && device.Index == index);
            if (current is not null)
            {
                Line($"GPU: {current.Name} ({current.TotalMemoryBytes.ToString(CultureInfo.InvariantCulture)} bytes, "
                    + $"UUID {current.Uuid ?? "unavailable"}, driver {current.DriverVersion ?? "unavailable"})");
            }
        }

        foreach (var storage in report.Storage)
        {
            Line(string.Format(
                CultureInfo.InvariantCulture,
                "storage: {0} has {1:F1} GiB / {2:F1}% free{3}",
                storage.Filesystem,
                storage.AvailableGib,
                storage.FreePercent,
                storage.Warning ? " — warning" : ""));
        }

        foreach (var problem in report.Problems)
        {
            Line("");
            Line($"{(problem.Code == PreflightProblemCode.HostCheckRequired ? "NEEDS HOST CHECK" : "BLOCKED")}: {problem.Meaning}");
            Line($"  fix: {problem.FixCommand}");
        }

        Line("");
        Line($"next: {report.NextStep}");
        return text.ToString();
    }

    /// <summary>
    /// Rust checked_pretty_json: serde_json::Value, whose objects are B-tree
    /// maps, so every object's keys come out sorted at every depth.
    /// </summary>
    static string RenderJson(ProfileValidationResult report) => ReportJson.SortedPretty(ToJsonObject(report));

    static JsonObject ToJsonObject(ProfileValidationResult report)
    {
        var storage = new JsonArray();
        foreach (var check in report.Storage)
        {
            storage.Add(new JsonObject
            {
                ["inspected_path"] = check.InspectedPath,
                ["filesystem"] = check.Filesystem,
                ["available_gib"] = ReportJson.Number(check.AvailableGib),
                ["free_percent"] = ReportJson.Number(check.FreePercent),
                ["warning"] = check.Warning,
                ["hard_stop"] = check.HardStop,
            });
        }

        var drift = new JsonArray();
        foreach (var item in report.ProfileDrift)
        {
            drift.Add(new JsonObject
            {
                ["field"] = item.Field,
                ["expected"] = item.Expected,
                ["observed"] = item.Observed,
            });
        }

        var problems = new JsonArray();
        foreach (var problem in report.Problems)
        {
            problems.Add(new JsonObject
            {
                ["code"] = Preflight.Wire(problem.Code),
                ["meaning"] = problem.Meaning,
                ["fix_command"] = problem.FixCommand,
            });
        }

        var typed = new JsonArray();
        foreach (var failure in report.TypedFailures)
        {
            typed.Add(failure.DeepClone());
        }

        return new JsonObject
        {
            ["schema"] = JsonSchema,
            ["status"] = Wire(report.Status),
            ["gpu_work_started"] = report.GpuWorkStarted,
            ["observer"] = Preflight.Wire(report.Observer),
            ["execution_facts_from"] = report.ExecutionFactsFrom is { } source ? Preflight.Wire(source) : null,
            ["profile_id"] = report.ProfileId,
            ["profile_path"] = report.ProfilePath,
            ["profile_revision"] = report.ProfileRevision,
            ["executor"] = report.Executor,
            ["device"] = report.Device,
            ["campaign_root"] = report.CampaignRoot,
            ["observed_execution"] = report.ObservedExecution is { } observed
                ? JsonNode.Parse(ObservedExecutionProbe.Render(observed))
                : null,
            ["profile_drift"] = drift,
            ["storage"] = storage,
            ["problems"] = problems,
            ["typed_failures"] = typed,
            ["next_step"] = report.NextStep,
        };
    }

    static string Wire(ProfileValidationStatus status) => status switch
    {
        ProfileValidationStatus.Ready => "ready",
        ProfileValidationStatus.NeedsHostCheck => "needs_host_check",
        ProfileValidationStatus.Blocked => "blocked",
        _ => throw new InvalidOperationException("profile validation gained a status with no wire name"),
    };
}
