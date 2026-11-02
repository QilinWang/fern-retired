using System.CommandLine;
using System.Text.Json;
using Fern.Registry;
using Fern.Install;

namespace Fern.Run.Cli;

/// <summary>
/// The two verbs that answer "what governs this machine": create a new Registry
/// Home from this build's embedded schema, and say which installed binary set
/// that Home was recorded against.
///
/// Everything else this file once held read a pre-2026-09-11 Registry Home or a
/// run campaign inside one — the relocations, the artifact audits, the matrix
/// signatures, the receipt printers and the protocol backfill. Those retired on
/// 2026-09-11 with the rest of the old lane
/// (retired/csharp/old-lane-readers-2026-09-11, design note
/// docs/design/original/by_ai/2026-09-11-the-old-lane-readers-retire.md). Old
/// Homes are archives; nothing opens one.
/// </summary>
public static class RegistryMaintenanceVerbs
{
    /// For the owner's PascalCase records: the wire spelling is snake_case, so a
    /// record rendered without this policy disagrees on every key name.
    static readonly JsonSerializerOptions RecordReport = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(InitializeRegistry());
        parent.Subcommands.Add(InspectRegistryHome());
        // CHG 2026-09-25 (slice P3 of the scheduling loop): what a schema walk would do, on a copy.
        parent.Subcommands.Add(PreviewRegistryWalk());
        parent.Subcommands.Add(InspectInstalledControl());
    }

    /// <summary>
    /// `inspect-registry-home`: what a Home is, without writing to it.
    ///
    /// It exists for the two questions a schema cut has to answer first. Which
    /// schema version is stamped — 0 meaning no stamp at all, a Home written
    /// before 2026-09-10, which the transform machinery will not walk forward —
    /// and what rows the tables hold, table by table, marked by whether anything
    /// in this build can still write one. A table with rows and no writer is
    /// unreachable evidence, and dropping it is irreversible; this is how
    /// somebody reads the list before deciding.
    ///
    /// It opens the file read-only and refuses by name anything that is not a
    /// Home, including a Home this binary would otherwise refuse as not current
    /// — describing that one is the point.
    /// </summary>
    static Command InspectRegistryHome()
    {
        var command = new Command("inspect-registry-home",
            "Read one Registry Home without writing to it: its schema version stamp, whether this build considers it current, and every table with its row count and whether anything still writes it.");
        var registry = Required("--registry", "Path to the Registry Home SQLite database to read.");
        command.Options.Add(registry);
        command.SetAction(parsed =>
        {
            var read = RegistryHomeCensus.Read(parsed.GetValue(registry)!);
            if (!read.TryGetValue(out var home))
            {
                read.TryGetRefusal(out var refusal);
                Console.Error.WriteLine($"inspect-registry-home: {refusal.Detail}");
                return 3;
            }
            Console.Out.Write(RegistryHomeCensus.Render(home));
            return 0;
        });
        return command;
    }

    /// <summary>
    /// `preview-registry-walk`: what walking a Home to this build's schema would do, shown on a
    /// copy. The Home is copied with SQLite's online backup (read-only on the Home, one
    /// consistent moment even while its server writes), the copy is walked, and the command prints
    /// either the refusal, naming every row the walk cannot carry, or each priority as converted and
    /// the offer in the order the claim would make it afterwards. The copy is deleted; the Home is
    /// never written. CHG 2026-09-25 (slice P3 of the scheduling loop).
    /// </summary>
    static Command PreviewRegistryWalk()
    {
        var command = new Command("preview-registry-walk",
            "Read-only on the Home. Copy it, walk the copy to this build's schema, and print what the walk would do: "
            + "the refusal with every row it names, or each priority as converted and the offer order afterwards.");
        var registry = Required("--registry", "Path to the Registry Home SQLite database to preview.");
        var shown = new Option<int>("--offer") { Description = "How many cells of the offer to print.", DefaultValueFactory = _ => 60 };
        // CHG 2026-09-26: keep the walked copy, so other readers of this build (what-stops-work,
        // monitor-line) can be tried on today's Home before the Home itself is walked.
        var keepCopy = new Option<string?>("--keep-copy")
        {
            Description = "Also keep the walked copy at this path (it must not exist); the Home is still not written.",
        };
        command.Options.Add(registry);
        command.Options.Add(shown);
        command.Options.Add(keepCopy);
        command.SetAction(parsed =>
        {
            var home = parsed.GetValue(registry)!;
            if (!File.Exists(home)) return Refuse("preview-registry-walk", $"there is no file at {home}");
            var folder = Directory.CreateTempSubdirectory("fern-registry-walk-preview-").FullName;
            var copy = Path.Combine(folder, "home.db");
            // A copy is as large as the Home; an interrupted preview removes it too.
            void Remove()
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                catch (IOException) { }
            }
            EventHandler onExit = (_, _) => Remove();
            ConsoleCancelEventHandler onCancel = (_, _) => Remove();
            AppDomain.CurrentDomain.ProcessExit += onExit;
            Console.CancelKeyPress += onCancel;
            try
            {
                RegistrySchema.CopyHome(home, copy);
                long before;
                using (var raw = RegistrySchema.OpenReadOnly(copy)) before = RegistrySchemaTransforms.ReadVersion(raw);
                Console.Out.WriteLine(
                    $"copied {home} (schema version {before}) to a temporary file; the Home itself is not written");
                // CHG 2026-09-27: the walk now copies the whole Registry itself before a
                // step that deletes rows; walking the temporary copy makes that copy beside
                // it, in the same temporary folder, and says so. The loop that made the
                // copies ArchivedBeside named, by hand, beside the temporary file is gone.
                var walked = RegistrySchema.Initialize(copy);
                if (walked is RegistryInitializationOutcome.Refused refused)
                {
                    Console.Out.WriteLine($"the walk to schema version {RegistrySchemaTransforms.Current} would refuse:");
                    Console.Out.WriteLine("  " + refused.Detail);
                    return 1;
                }
                if (walked is RegistryInitializationOutcome.Initialized { Receipt.CopiedBeforeWalk: not null })
                {
                    Console.Out.WriteLine(
                        $"the walk would first copy the whole Home beside it, to {home}.before-walk-<date>, which keeps the rows its steps delete");
                }
                using var reader = RegistryHome.OpenReader(copy);
                var names = ScopeNames(reader.Connection);
                var converted = RegistryOperations.ListPriorities(reader.Connection);
                Console.Out.WriteLine(
                    $"the walk to schema version {RegistrySchemaTransforms.Current} succeeds; {converted.Count} priority row(s) after it:");
                foreach (var row in converted)
                {
                    var was = row.ConvertedFrom is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(set as a word)";
                    Console.Out.WriteLine($"  {was,6} -> {row.Priority,-9}  {names.GetValueOrDefault(row.Scope, row.Scope)}");
                }
                var line = Fern.Scheduler.LineProjection.Read(reader.Connection);
                Console.Out.WriteLine($"the offer after the walk ({line.Queued.Count} cell(s), first {Math.Min(line.Queued.Count, parsed.GetValue(shown))}):");
                foreach (var queued in line.Queued.Take(parsed.GetValue(shown)))
                {
                    Console.Out.WriteLine($"  {queued.Position,4}. {queued.Priority,-9} {queued.Study}  {queued.Cell}");
                }
                if (parsed.GetValue(keepCopy) is { } kept)
                {
                    if (File.Exists(kept)) return Refuse("preview-registry-walk", $"--keep-copy {kept} already exists");
                    RegistrySchema.CopyHome(copy, kept);
                    Console.Out.WriteLine($"the walked copy is kept at {kept}");
                }
                return 0;
            }
            catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException
                or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                return Refuse("preview-registry-walk", error.Message);
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= onExit;
                Console.CancelKeyPress -= onCancel;
                Remove();
            }
        });
        return command;
    }

    /// <summary>A person's name for each study and cell scope: the study's name, or study/cell.</summary>
    static Dictionary<string, string> ScopeNames(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM studies";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) names[RegistryOperations.StudyScope(reader.GetInt64(0))] = "study " + reader.GetString(1);
        }
        command.CommandText = "SELECT c.id, s.name, c.name FROM cells c JOIN studies s ON s.id = c.study_id";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                names[RegistryOperations.CellScope(reader.GetInt64(0))] = $"cell {reader.GetString(1)} / {reader.GetString(2)}";
            }
        }
        return names;
    }

    /// <summary>Create a new current-schema Home; an existing old Home must be archived.</summary>
    static Command InitializeRegistry()
    {
        var command = new Command("initialize-registry", "Initialize a new Registry with this build's current schema; refuse an existing old database.");
        var registry = Required("--registry", "Path to the new Registry Home SQLite database.");
        command.Options.Add(registry);
        command.SetAction(parsed =>
        {
            switch (RegistrySchema.Initialize(parsed.GetValue(registry)!))
            {
                case RegistryInitializationOutcome.Refused refused:
                    return Refuse("initialize-registry", refused.Detail);
                case RegistryInitializationOutcome.Initialized initialized:
                    Console.Out.WriteLine(JsonSerializer.Serialize(initialized.Receipt, RecordReport));
                    return 0;
                default:
                    return Refuse("initialize-registry", "the initializer reported no outcome");
            }
        });
        return command;
    }

    /// inspect-installed-control -- which binary set governs one Registry Home here.
    /// Owner: InstalledControlInspectionCli, unchanged.
    static Command InspectInstalledControl()
    {
        var registry = new Option<string>("--registry") { Description = "Registry Home the installed set was recorded against.", Required = true };
        var deploymentRoot = new Option<string>("--deployment-root") { Description = "Root holding the installation receipts.", Required = true };
        var command = new Command(
            Fern.Install.InstalledControlInspectionCli.Verb,
            "Say which installed binary set governs one Registry Home on this machine, from its receipt alone.");
        foreach (var option in new Option[] { registry, deploymentRoot }) command.Options.Add(option);
        command.SetAction(parsed => Fern.Install.InstalledControlInspectionCli.Run(
            ["--registry", parsed.GetValue(registry)!, "--deployment-root", parsed.GetValue(deploymentRoot)!],
            Console.Out,
            Console.Error));
        return command;
    }

    // ---------------------------------------------------------------- shared

    /// Rust's run_or_exit: "<command> failed: <error>" on stderr, exit 1.
    static int Refuse(string command, string detail)
    {
        Console.Error.WriteLine($"{command} failed: {detail}");
        return 1;
    }

    static Option<string> Required(string name, string description) =>
        new(name) { Description = description, Required = true };
}
