using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;
using Fern.Site;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>
/// What one execution profile says about a machine, read once at registration.
/// </summary>
/// <remarks>
/// These are the fields the old route read to place and launch work, and no
/// others: a profile also carries storage warnings, supervisor budgets and an
/// accelerator floor, which are the launcher's business rather than the
/// store's. <c>AdmittedModels</c> is empty when the profile declares none,
/// which the scheduler reads as "this machine admits work by no model" — a
/// profile that means "any model" says so with a list.
/// </remarks>
public sealed record DeclaredSite(
    string ProfileId,
    string Executor,
    string? SshDestination,
    string SourceRoot,
    string Python,
    string? WorkRoot,
    string Device,
    long Slots,
    IReadOnlyList<string> AdmittedModels,
    IReadOnlyList<string> Devices,
    string? CodeFrom = null,
    string? Repository = null);

/// <summary>
/// Mints the <c>sites</c> row the scheduler places cells on.
///
/// A Site is registered from its execution profile because that file already
/// is the machine's declaration — executor, address, roots, interpreter — and
/// a second place to write those facts is a second place for them to disagree.
/// </summary>
public static class SiteRegistration
{
    /// <summary>Read one execution profile. Opens no connection and reaches no machine.</summary>
    public static SiteResult<DeclaredSite> Read(string profilePath)
    {
        string text;
        try
        {
            text = File.ReadAllText(profilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath} cannot be read: {error.Message}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException invalid)
        {
            return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath} is not valid JSON: {invalid.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath} is not a JSON object");
            }

            var executor = Text(root, "executor");
            if (executor is null)
            {
                return SiteResult<DeclaredSite>.Permanent(
                    $"execution profile {profilePath} names no executor; a Site that does not say how it is reached cannot be placed on");
            }
            if (executor is not ("local" or "ssh"))
            {
                return SiteResult<DeclaredSite>.Permanent(
                    $"execution profile {profilePath} declares executor \"{executor}\", which is neither local nor ssh");
            }

            var sshDestination = Text(root, "ssh_destination");
            if (executor == "ssh" && sshDestination is null)
            {
                return SiteResult<DeclaredSite>.Permanent(
                    $"execution profile {profilePath} is an ssh Site with no ssh_destination");
            }

            var sourceRoot = Text(root, "source_root");
            if (sourceRoot is null)
            {
                return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath} names no source_root");
            }

            var python = Text(root, "python");
            if (python is null)
            {
                return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath} names no python");
            }

            var slots = 1L;
            if (root.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object
                && worker.TryGetProperty("slots", out var declaredSlots) && declaredSlots.TryGetInt64(out var value))
            {
                slots = value;
            }
            if (slots <= 0)
            {
                return SiteResult<DeclaredSite>.Permanent(
                    $"execution profile {profilePath} declares {slots} worker slot(s); a Site with no slot runs nothing");
            }

            var admitted = new List<string>();
            if (root.TryGetProperty("admitted_models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString()))
                    {
                        admitted.Add(model.GetString()!);
                    }
                }
            }

            // CHG 2026-09-11 night (slice 3 of the cell port, rows 4 and 29): the
            // cards a machine declares, by GPU UUID, one cell per card. A profile
            // that names none is counted by slots, as before; `register-site
            // --observe-cards` fills the list from nvidia-smi on the machine
            // itself, and `--device` names one by hand.
            var devices = new List<string>();
            if (root.TryGetProperty("devices", out var cards) && cards.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in cards.EnumerateArray())
                {
                    if (card.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(card.GetString()))
                    {
                        devices.Add(card.GetString()!);
                    }
                }
            }
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): where
            // this Site's cells take their code from, and the repository for the
            // two answers that use one. Checked here with the same reader the
            // claim uses, so a profile the claim would refuse is not registered.
            var codeFrom = Text(root, "code_from");
            var repository = Text(root, "repository");
            if (codeFrom is not null)
            {
                var declaredCode = new JsonObject { ["code_from"] = codeFrom, ["repository"] = repository };
                var reading = RegistryOperations.CodeOf(declaredCode.ToJsonString());
                if (!reading.IsOk)
                {
                    reading.TryGetRefusal(out var refusal);
                    return SiteResult<DeclaredSite>.Permanent($"execution profile {profilePath}: {refusal.Detail}");
                }
            }
            return SiteResult<DeclaredSite>.Ok(new DeclaredSite(
                Text(root, "profile_id") ?? Path.GetFileNameWithoutExtension(profilePath),
                executor,
                sshDestination,
                sourceRoot,
                python,
                Text(root, "work_root"),
                Text(root, "device") ?? "cuda",
                slots,
                admitted,
                devices,
                codeFrom,
                repository));
        }
    }

    /// <summary>
    /// The cards this machine holds, as nvidia-smi names them: one UUID per
    /// line. Run on the machine being registered, which under the pull design
    /// is where register-site runs. A machine without nvidia-smi, or with none,
    /// declares no card and is counted by slots.
    /// </summary>
    public static SiteResult<IReadOnlyList<string>> ObserveCards(WorkerEnvironmentPreparation.IHost host, string nvidiaSmi = "nvidia-smi")
    {
        var program = nvidiaSmi;
        if (!Path.IsPathRooted(program))
        {
            var found = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, program))
                .FirstOrDefault(File.Exists);
            if (found is null)
            {
                return SiteResult<IReadOnlyList<string>>.Permanent($"{program} is not on PATH; declare the cards with --device or leave the Site counted by slots");
            }
            program = found;
        }
        var outcome = host.Run(program, ["--query-gpu=uuid", "--format=csv,noheader"], 20_000, 65_536,
            Path.GetTempPath(), new Dictionary<string, string>());
        if (outcome is not BoundedProbe.Outcome.Completed completed || completed.ExitCode != 0)
        {
            return SiteResult<IReadOnlyList<string>>.Permanent($"{program} did not list the cards: {outcome}");
        }
        var uuids = System.Text.Encoding.UTF8.GetString(completed.Stdout)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("GPU-", StringComparison.Ordinal))
            .ToList();
        return SiteResult<IReadOnlyList<string>>.Ok(uuids);
    }

    /// <summary>
    /// The capabilities keys registration writes from the profile, and the only
    /// ones a re-registration may replace or remove. Every other key on the row
    /// was recorded by another command and is kept.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnedKeys =
        ["profile_id", "executor", "source_root", "python", "work_root", "device", "ssh_destination", "admitted_models", "devices",
         "code_from", "repository"];

    /// <summary>Write the row. Registering the same name twice updates what it declares and keeps what others recorded.</summary>
    public static RegistryResult<RegistryOperations.RegisteredSite> Register(
        RegistryWriter writer,
        string name,
        DeclaredSite declared)
    {
        var capabilities = new JsonObject
        {
            ["profile_id"] = declared.ProfileId,
            ["executor"] = declared.Executor,
            ["source_root"] = declared.SourceRoot,
            ["python"] = declared.Python,
            // CHG 2026-09-11 night (slice 6, row 32): the Site's work root, so
            // install-study-source --all-sites reaches an ssh Site from the row.
            ["work_root"] = declared.WorkRoot,
            ["device"] = declared.Device,
        };
        if (declared.SshDestination is { Length: > 0 } destination)
        {
            capabilities["ssh_destination"] = destination;
        }
        if (declared.CodeFrom is { Length: > 0 } codeFrom)
        {
            capabilities["code_from"] = codeFrom;
        }
        if (declared.Repository is { Length: > 0 } repository)
        {
            capabilities["repository"] = repository;
        }
        var admitted = new JsonArray();
        foreach (var model in declared.AdmittedModels)
        {
            admitted.Add(model);
        }
        capabilities["admitted_models"] = admitted;
        if (declared.Devices.Count != 0)
        {
            var cards = new JsonArray();
            foreach (var card in declared.Devices)
            {
                cards.Add(card);
            }
            capabilities["devices"] = cards;
        }

        var capacity = new JsonObject { ["slots"] = declared.Slots };
        return RegistryOperations.RegisterSite(writer, name, capabilities.ToJsonString(), capacity.ToJsonString(), OwnedKeys);
    }

    static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var found)
        && found.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(found.GetString())
            ? found.GetString()
            : null;
}
