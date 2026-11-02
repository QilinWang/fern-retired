using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// What stops work, and running trainings a person should know about, told to
/// the owner once and again while they last: slices R1, R3, W2 and W3 of
/// docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-25-what-stops-work-and-who-lifts-it and
/// docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-leases-and-watching-a-running-cell.
///
/// The Home reads its Registry as every Site's supervisor reads it, asks the
/// Gleam flow (<c>what_stops_work</c> in
/// <c>tools/fern-scheduler-gleam/src/fern_scheduler/stuck.gleam</c>) what
/// stands and what is due to be told, and shows what is due as one desktop
/// notification. The flow decides what is due; this class reads, sends and
/// remembers what it sent. It stops, releases and claims nothing.
///
/// Why this lives in the Home: the Home is the one process that runs while
/// the Sites come and go, and it reads every Site's cells from one Registry.
/// A new file, rather than a part of <see cref="HomeService"/>, because the
/// HTTP service answers requests and this runs on its own clock.
/// </summary>
public static class HomeNotices
{
    /// <summary>One thing the flow says, as a person and a page read it.</summary>
    public sealed record Item(
        string Key, string Sentence, IReadOnlyList<long> CellIds, string WaitsFor, string? By, long? Since);

    /// <summary>Everything the flow said of one reading: what stands, what is due now with the notices to remember, and the oldest request.</summary>
    /// <param name="GiveUp">CHG 2026-09-26 (owner): attempts silent for an hour that nobody answered, with the sentence that tells it.</param>
    public sealed record Summary(
        IReadOnlyList<Item> Items, IReadOnlyList<(string Key, IReadOnlyList<string> Notices)> ToTell, JsonObject? OldestRequest,
        JsonObject Answer, IReadOnlyList<(long AttemptId, string Sentence)> GiveUp);

    /// <summary>
    /// The Registry as a reading of every Site. No Site is this process's own,
    /// so each Site's contact is what its supervisor last recorded, and no
    /// source is prepared: this reads only.
    /// </summary>
    public static JsonObject ReadingOfEverySite(IStudyStore store, DateTimeOffset now)
    {
        var sites = store.ListSites();
        var cells = store.ListUnfinishedCells(null);
        return GleamStudyRound.Reading(
            store, cells, store.ReadDispatchOrder(), sites,
            store.ListSiteSupervisors(), [],
            RegistryOperations.DefaultMaxAttempts, now, store.ListSiteFailures());
    }

    /// <summary>
    /// Ask the flow. <paramref name="told"/> holds, per item, the notices already sent while it
    /// stood; <paramref name="letRun"/> item keys a person answered "let it run"; <paramref name="seen"/>
    /// since when (seconds) each item has been in every reading.
    /// </summary>
    public static Summary Ask(
        GleamProgram program, JsonObject reading, IReadOnlyDictionary<string, HashSet<string>> told,
        IReadOnlyCollection<string> letRun, IReadOnlyDictionary<string, long> seen)
    {
        var answered = program.Ask(new JsonObject
        {
            ["ask"] = "what_stops_work",
            ["observation"] = reading,
            ["told"] = new JsonArray(told.Select(pair => (JsonNode)new JsonObject
            {
                ["key"] = pair.Key,
                ["notices"] = new JsonArray(pair.Value.Order(StringComparer.Ordinal).Select(notice => (JsonNode)notice).ToArray()),
            }).ToArray()),
            ["let_run"] = new JsonArray(letRun.Select(key => (JsonNode)key).ToArray()),
            ["seen"] = new JsonArray(seen.Select(pair => (JsonNode)new JsonObject { ["key"] = pair.Key, ["since"] = pair.Value }).ToArray()),
        }, TimeSpan.FromSeconds(60), null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException("the Gleam summary of what stops work did not answer: " + answered.RefusalDetail);
        }
        var answer = answered.Value["answer"] as JsonObject
            ?? throw new GleamProgramException("the summary of what stops work had no answer: " + answered.Value.ToJsonString());
        var items = (answer["items"] as JsonArray ?? []).Select(node =>
        {
            var item = node as JsonObject ?? throw new GleamProgramException("an item of the summary was not an object");
            var waits = item["waits_for"] as JsonObject;
            return new Item(
                Text(item, "key"),
                Text(item, "sentence"),
                (item["cell_ids"] as JsonArray ?? []).Select(id => id!.GetValue<long>()).ToList(),
                waits?["kind"]?.GetValue<string>() ?? "",
                waits?["by"]?.GetValue<string>(),
                item["since"] is JsonValue since ? since.GetValue<long>() : null);
        }).ToList();
        var toTell = (answer["to_tell"] as JsonArray ?? [])
            .Select(node => node as JsonObject ?? throw new GleamProgramException("a notice of the summary was not an object"))
            .Select(notice => (Text(notice, "key"),
                (IReadOnlyList<string>)(notice["notices"] as JsonArray ?? []).Select(one => one!.GetValue<string>()).ToList()))
            .ToList();
        var giveUp = (answer["give_up"] as JsonArray ?? [])
            .Select(node => node as JsonObject ?? throw new GleamProgramException("a give-up of the summary was not an object"))
            .Select(one => (one["attempt_id"]!.GetValue<long>(), Text(one, "sentence")))
            .ToList();
        return new Summary(items, toTell, answer["oldest_request"] as JsonObject, answer, giveUp);
    }

    /// <summary>
    /// CHG 2026-09-26 (owner: "通知后30分钟通知我，30分钟我不管你就通知，然后重新派发"): give up
    /// these attempts in the Registry, each still open at its current epoch, recording why.
    /// Returns the attempts given up; one that reported again or ended meanwhile is left.
    /// </summary>
    public static IReadOnlySet<long> GiveUpSilent(IStudyStore store, IReadOnlyList<(long AttemptId, string Sentence)> which)
    {
        var done = new HashSet<long>();
        if (which.Count == 0) return done;
        var open = store.ListUnfinishedCells(null)
            .Where(cell => cell.AttemptId is not null && cell.FencingEpoch is not null
                && cell.AttemptState is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
            .ToDictionary(cell => cell.AttemptId!.Value);
        foreach (var (attemptId, sentence) in which)
        {
            if (!open.TryGetValue(attemptId, out var cell)) continue;
            var detail = new JsonObject
            {
                ["detail"] = sentence,
                ["given_up_by"] = "the Home: no heartbeat for an hour, and no answer to the notice at half an hour",
            }.ToJsonString();
            if (store.FailAttempt(attemptId, cell.FencingEpoch!.Value, RegistryOperations.AttemptLost, detail).IsOk) done.Add(attemptId);
        }
        return done;
    }

    static string Text(JsonObject node, string name) =>
        node[name]?.GetValue<string>() ?? throw new GleamProgramException($"the summary lacks {name}: {node.ToJsonString()}");

    /// <summary>Where a notice is shown on this machine.</summary>
    public interface IDesktop
    {
        void Show(string title, string body);
    }

    /// <summary>
    /// A notification on the Windows desktop this WSL machine runs under,
    /// shown by Windows PowerShell's own toast. The title and the text travel
    /// in environment variables (passed through <c>WSLENV</c>), so no sentence
    /// is ever quoted into a command line.
    /// </summary>
    public sealed class WindowsToast(string powershell) : IDesktop
    {
        public const string DefaultPowershell = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";

        const string Script =
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; "
            + "$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); "
            + "$x = $t.GetElementsByTagName('text'); "
            + "$x.Item(0).AppendChild($t.CreateTextNode($env:FERN_TOAST_TITLE)) | Out-Null; "
            + "$x.Item(1).AppendChild($t.CreateTextNode($env:FERN_TOAST_BODY)) | Out-Null; "
            + "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("
            + "'{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show("
            + "[Windows.UI.Notifications.ToastNotification]::new($t))";

        public void Show(string title, string body)
        {
            var start = new ProcessStartInfo(powershell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", Script }) start.ArgumentList.Add(argument);
            start.Environment["FERN_TOAST_TITLE"] = title;
            start.Environment["FERN_TOAST_BODY"] = body;
            start.Environment["WSLENV"] = "FERN_TOAST_TITLE/u:FERN_TOAST_BODY/u";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe could not be started");
            // CHG 2026-09-26 (review): both outputs are read in the background, so a
            // powershell.exe that hangs is ended at the bound below, not waited on forever.
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("the desktop notification did not finish within 30 s");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("the desktop notification failed: " + error.Result.Trim());
            }
        }
    }

    /// <summary>
    /// What was told and what a person answered, kept in one directory beside
    /// the Registry so a restarted Home does not tell everything again. Two
    /// files, each with one writer: <c>home-notices-told.json</c> is the Home's;
    /// <c>home-notices-let-run.txt</c> takes one item key per line from
    /// <c>fern-run let-it-run</c>.
    /// </summary>
    /// <param name="giveUp">Gives the listed attempts up under the Home's store; see <see cref="GiveUpSilent"/>. Without it nothing is given up.</param>
    public sealed class Notifier(string directory, IDesktop? desktop, TextWriter log,
        Func<IReadOnlyList<(long AttemptId, string Sentence)>, IReadOnlySet<long>>? giveUp = null)
    {
        public const string ToldFile = "home-notices-told.json";
        public const string LetRunFile = "home-notices-let-run.txt";

        /// <summary>Most notices a desktop notification lists by name; the rest are counted.</summary>
        public const int ListedInOneNotification = 4;

        // What was told, per item, while it stood; and since when each item has been
        // in every reading. Both are forgotten for an item once it is gone from a
        // reading, so a problem that comes back is told again (review, 2026-09-26).
        readonly Dictionary<string, HashSet<string>> _told = ReadState(Path.Combine(directory, ToldFile)).Told;
        readonly Dictionary<string, long> _seen = ReadState(Path.Combine(directory, ToldFile)).Seen;

        /// <summary>The last summary, for a page to show.</summary>
        public Summary? Latest { get; private set; }

        /// <summary>
        /// Ask about one reading, show what is due as one notification, and
        /// remember it. The reading is taken by the caller (the Home takes it
        /// between requests); nothing here touches the Registry.
        /// </summary>
        public Summary Tick(JsonObject reading, GleamProgram program)
        {
            var at = reading["at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var summary = Ask(program, reading, _told, ReadLetRun(directory), _seen);
            Latest = summary;
            var present = summary.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _told.Keys.Where(key => !present.Contains(key)).ToList()) _told.Remove(gone);
            foreach (var gone in _seen.Keys.Where(key => !present.Contains(key)).ToList()) _seen.Remove(gone);
            foreach (var key in present) _seen.TryAdd(key, at);
            IReadOnlyList<string> givenUp = [];
            if (giveUp is not null && summary.GiveUp.Count != 0)
            {
                var done = giveUp(summary.GiveUp);
                givenUp = summary.GiveUp.Where(one => done.Contains(one.AttemptId)).Select(one => one.Sentence).ToList();
            }
            if ((summary.ToTell.Count != 0 || givenUp.Count != 0) && Show(summary, givenUp))
            {
                foreach (var (key, notices) in summary.ToTell)
                {
                    if (!_told.TryGetValue(key, out var set)) _told[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.UnionWith(notices);
                }
            }
            WriteState(Path.Combine(directory, ToldFile), _told, _seen);
            return summary;
        }

        /// <summary>Show what is due as one notification; false when the desktop did not, so it is due again.</summary>
        bool Show(Summary summary, IReadOnlyList<string> givenUp)
        {
            var due = givenUp
                .Concat(summary.ToTell.Select(notice => summary.Items.First(item => item.Key == notice.Key).Sentence))
                .ToList();
            var body = string.Join("\n", due.Take(ListedInOneNotification).Select(sentence => "• " + sentence))
                + (due.Count > ListedInOneNotification ? $"\n…and {due.Count - ListedInOneNotification} more: fern-run what-stops-work" : "");
            var title = due.Count == 1 ? "Fern: one thing needs you" : $"Fern: {due.Count} things need you";
            if (desktop is not null)
            {
                try
                {
                    desktop.Show(title, body);
                }
                // CHG 2026-09-26 (review): whatever the desktop throws (a Windows program WSL
                // cannot start throws Win32Exception) is logged; it never ends the Home.
                catch (Exception error)
                {
                    log.WriteLine($"home notices: the desktop did not show them: {error.Message}");
                    return false;
                }
            }
            log.WriteLine($"home notices: {title}\n{body}");
            return true;
        }

        static (Dictionary<string, HashSet<string>> Told, Dictionary<string, long> Seen) ReadState(string path)
        {
            var told = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var seen = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!File.Exists(path)) return (told, seen);
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            foreach (var (key, notices) in node?["told"] as JsonObject ?? [])
            {
                told[key] = (notices as JsonArray ?? []).Select(one => one!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            }
            foreach (var (key, since) in node?["seen"] as JsonObject ?? [])
            {
                if (since is JsonValue value) seen[key] = value.GetValue<long>();
            }
            return (told, seen);
        }

        static void WriteState(string path, Dictionary<string, HashSet<string>> told, Dictionary<string, long> seen)
        {
            var temporary = path + ".writing";
            File.WriteAllText(temporary, new JsonObject
            {
                ["told"] = new JsonObject(told.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray(pair.Value.Order(StringComparer.Ordinal).Select(one => (JsonNode)one).ToArray())))),
                ["seen"] = new JsonObject(seen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>The item keys a person answered "let it run": the first field of each line.</summary>
        public static IReadOnlyList<string> ReadLetRun(string directory)
        {
            var path = Path.Combine(directory, LetRunFile);
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(line => line.Split('\t')[0].Trim()).Where(key => key.Length > 0).ToList()
                : [];
        }

        /// <summary>Record that a person answered "let it run" to one item: its key, who, and when, on one line.</summary>
        public static void LetRun(string directory, string key, string by, DateTimeOffset at)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, LetRunFile), $"{key}\t{by}\t{at:O}\n");
        }
    }
}
