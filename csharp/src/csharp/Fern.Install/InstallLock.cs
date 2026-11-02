using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Experiment;

namespace Fern.Install;

/// <summary>
/// One installation per machine at a time.
///
/// WHAT IT PORTS. The retired `EnvironmentInstallLeaseAuthority` held a fenced
/// lease over installing one environment, with a deadline and a reconciliation
/// path for a holder that died. Its MEANING is what comes back here, not its
/// table: two installs must not interleave in one deployment root, because the
/// second one publishes over bytes the first is still writing and the receipt
/// then names a set that never existed as a set. `install-control-plane` has
/// carried a step saying this was missing — "nothing excludes a second writer of
/// the deployment root" — since the installer was written; this is that step.
///
/// WHY A FILE AND NOT A ROW. An install runs before there is a Registry to
/// write to, and on a machine whose Registry may be the thing being installed.
/// The exclusion has to live where the install lives, so it is a file in the
/// deployment root held open for the life of the install with no sharing: the
/// operating system, not a timestamp, is what says another process holds it. A
/// holder that dies releases the handle immediately, so there is no deadline to
/// guess and no stale lease to reconcile — the crash case recovers by itself,
/// which is the one thing the retired lease had to work hardest for.
///
/// The file's CONTENT is for the person who gets refused: who holds it, on which
/// host, since when, and for what. It is written for reading, never for locking.
/// </summary>
public static class InstallLock
{
    /// <summary>The file held open, in the deployment root it protects.</summary>
    public const string FileName = "install.lock";

    /// <summary>
    /// The note beside it, saying who holds the machine. It is a second file
    /// because the lock file itself is held with NO sharing — that is what
    /// excludes a second install — and a file nobody may open is a file nobody
    /// can read the holder from. The lock excludes; the note explains.
    /// </summary>
    public const string NoteName = "install.lock.json";
    public const string Schema = "fern.install_lock.v1";

    /// <summary>A held lock. Disposing it releases the machine for the next install.</summary>
    public sealed class Held(FileStream stream, string path, string notePath) : IDisposable
    {
        public string Path => path;

        public void Dispose()
        {
            stream.Dispose();
            foreach (var file in new[] { notePath, path })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Another install may already be taking it; the handle is
                    // what excluded them, and it is closed.
                }
            }
        }
    }

    /// <summary>Take the machine for one install, or say who has it.</summary>
    public static TextResult<Held> Acquire(string deploymentRoot, string purpose)
    {
        string path;
        try
        {
            Directory.CreateDirectory(deploymentRoot);
            path = System.IO.Path.Combine(deploymentRoot, FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return TextResult<Held>.Refused($"the deployment root {deploymentRoot} cannot be opened: {error.Message}");
        }

        FileStream stream;
        try
        {
            // FileShare.None is the exclusion — an exclusive lock on the open
            // file, which a second process (or a second call in this one) cannot
            // take while this handle lives. Sharing it for reading would take a
            // SHARED lock instead, which excludes nobody; that is why the note
            // for humans is a separate file.
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            return TextResult<Held>.Refused(
                $"another install already holds {path}: {Describe(deploymentRoot)}. One install per machine at a "
                + "time — wait for it, or make sure it is really gone before removing the file by hand");
        }
        catch (UnauthorizedAccessException error)
        {
            return TextResult<Held>.Refused($"{path} cannot be written: {error.Message}");
        }

        var notePath = System.IO.Path.Combine(deploymentRoot, NoteName);
        try
        {
            var note = new JsonObject
            {
                ["schema"] = Schema,
                ["purpose"] = purpose,
                ["pid"] = System.Environment.ProcessId,
                ["host"] = System.Environment.MachineName,
                ["started_at"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(notePath, note.ToJsonString() + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The handle is what excludes; a note nobody could write costs the
            // refusal its detail and nothing else.
        }
        return TextResult<Held>.Ok(new Held(stream, path, notePath));
    }

    /// <summary>Who holds the machine, read from the note beside the lock; a sentence either way.</summary>
    public static string Describe(string deploymentRoot)
    {
        try
        {
            using var note = JsonDocument.Parse(
                File.ReadAllText(System.IO.Path.Combine(deploymentRoot, NoteName)));
            var root = note.RootElement;
            return $"pid {Text(root, "pid")} on {Text(root, "host")} since {Text(root, "started_at")} "
                 + $"({Text(root, "purpose")})";
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return "the lock file names no holder";
        }
    }

    /// <summary>Whether a machine is free to install right now, without taking it.</summary>
    public static bool IsHeld(string deploymentRoot)
    {
        var path = System.IO.Path.Combine(deploymentRoot, FileName);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    static string Text(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "?" : value.ToString()
            : "?";
}
