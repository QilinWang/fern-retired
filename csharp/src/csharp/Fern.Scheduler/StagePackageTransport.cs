using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using Fern.Site;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>
/// One accepted training checkpoint read off a Worker Site's stdout: the epoch
/// reached, how many there are, and whether it is the last one.
/// </summary>
/// <remarks>
/// Parsing only recovers a fact from the wire. <see cref="StagePackageTransport.AcceptMonotoneCheckpoint"/>
/// separately decides whether that fact advances semantic progress; ordinary
/// process liveness never refreshes a scientific epoch.
/// </remarks>
public readonly record struct StageCheckpoint(long Current, long Total, bool Terminal, string? TrainingRun = null,
    StagePhaseObservation? Phase = null);

/// <summary>
/// Installs immutable runtime, control, source, and store content on one Worker
/// Site, then opens the resident framed Site session used by whole-Cell
/// execution. It does not open, execute, or return one Stage Attempt.
///
/// CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
/// slice 4; owner: the remote installer and control set are retired): the
/// install, the remote .NET runtime check, the resident `fern-site serve`
/// session and the process runner they shared are removed. What remains is the
/// progress wire (a training checkpoint read from a Site's line) and the checks
/// of the maintained SSH helper.
///
/// Fern never constructs an ssh command of its own. The helper is a maintained
/// script whose path is checked before use; if it is missing, the transport
/// refuses rather than inventing an equivalent invocation.
/// </summary>
public static class StagePackageTransport
{
    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4; owner: the remote control set is retired): SiteMachineSession and
    // OpenMachineSession, a remote `fern-site serve` kept open across cells,
    // are removed with the installer that put fern-site on a Site.

    /// <summary>
    /// The maintained RunPod SSH helper, relative to the reviewed source root.
    /// </summary>
    public const string MaintainedSshHelperRelativePath = "scripts/connect_runpod_ssh.sh";

    // ---- the progress wire ----

    /// <summary>
    /// Recover one training checkpoint from a line of a Site's stdout, or null.
    ///
    /// Null is the answer for everything that is not this protocol: a trainer
    /// banner, a half-written line, another schema, a checkpoint numbered zero
    /// or past its own total. A Site is allowed to print whatever it likes;
    /// only the typed protocol is read.
    /// </summary>
    public static StageCheckpoint? ParseStageCheckpoint(string line)
    {
        if (!line.StartsWith(SitePaths.StageProgressPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var body = line[SitePaths.StageProgressPrefix.Length..];
        if (StagePhaseObservation.Parse(body) is { } phase)
            return new StageCheckpoint(0, 0, false, Phase: phase);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (ReadString(document.RootElement, "schema") != "fern.training_progress.v1"
                || ReadString(document.RootElement, "kind") != "training_epoch")
            {
                return null;
            }
            if (!TryReadUnsigned(document.RootElement, "current", out var current)
                || !TryReadUnsigned(document.RootElement, "total", out var total))
            {
                return null;
            }
            if (current == 0 || current > total)
            {
                return null;
            }
            var terminal = document.RootElement.TryGetProperty("terminal", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            return new StageCheckpoint(current, total, terminal, ReadString(document.RootElement, "training_run"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Accept only a monotone semantic checkpoint from one running Stage.
    ///
    /// A repeated or reordered epoch, a changed total, or terminal becoming
    /// false is not progress. The one same-epoch transition that is progress is
    /// non-terminal to terminal: early stopping may close the current epoch
    /// without incrementing it. This is the former Rust
    /// <c>classify_remote_stage_progress</c> rule, expressed as a pure function
    /// so the transport and its falsifier share one meaning.
    /// </summary>
    public static StageCheckpoint? AcceptMonotoneCheckpoint(
        StageCheckpoint? previous,
        StageCheckpoint observed)
    {
        if (previous is null)
        {
            return observed;
        }

        var prior = previous.Value;
        if (observed.Phase is { } phase)
            return prior.Phase is not { } previousPhase || phase.OperationId != previousPhase.OperationId
                || phase.Sequence > previousPhase.Sequence ? observed : null;
        if (prior.Phase is not null) return observed; // Cell authority retains independent epoch ordering.
        if (observed.Total != prior.Total)
        {
            return null;
        }

        if (observed.Current > prior.Current
            || (observed.Current == prior.Current && observed.Terminal && !prior.Terminal))
        {
            return observed;
        }

        return null;
    }

    /// <summary>
    /// Read a child's stdout into whole lines, bounded in both directions.
    ///
    /// A line longer than the cap is dropped entirely, including the rest of
    /// it after the cap is reached, so a program that prints a megabyte without
    /// a newline cannot make Home hold a megabyte. The last line is delivered
    /// even without a closing newline, because a runner killed at its final
    /// epoch still reported that epoch. A dropped oversized line at the end is
    /// not delivered: it was never a whole line.
    ///
    /// Bounding here is not redundant with the Site bounding its own runner's
    /// output. Each reader bounds itself, because the reader that trusts an
    /// upstream bound is the one that fails when the upstream changes.
    /// </summary>
    public static void ForwardBoundedLines(Stream reader, Action<string> sink)
    {
        var chunk = new byte[1024];
        var pending = new List<byte>(1024);
        var droppingLine = false;
        while (true)
        {
            int count;
            try
            {
                count = reader.Read(chunk, 0, chunk.Length);
            }
            catch (IOException)
            {
                // The pipe died with the process. Whatever was already whole
                // has been delivered; a partial line is not a fact.
                return;
            }
            if (count == 0)
            {
                break;
            }
            for (var index = 0; index < count; index++)
            {
                var value = chunk[index];
                if (value == (byte)'\n')
                {
                    if (!droppingLine)
                    {
                        sink(Line(pending));
                    }
                    pending.Clear();
                    droppingLine = false;
                }
                else if (!droppingLine)
                {
                    if (pending.Count < SitePaths.MaximumStageProgressLineBytes)
                    {
                        pending.Add(value);
                    }
                    else
                    {
                        pending.Clear();
                        droppingLine = true;
                    }
                }
            }
        }
        if (!droppingLine && pending.Count > 0)
        {
            sink(Line(pending));
        }
    }

    static string Line(List<byte> pending) =>
        Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r');

    // ---- the maintained helper's process ----

    /// <summary>
    /// The maintained SSH helper, or a refusal. Fern will not construct an
    /// ad-hoc ssh command when it is missing.
    ///
    /// CHG 2026-08-29: takes the helper's own path. It used to take a reviewed
    /// source root and append the relative path, which made every SSH Stage
    /// depend on a worktree; the installed set now carries the helper's
    /// absolute path in its receipt. A caller holding a worktree combines the
    /// root with <see cref="MaintainedSshHelperRelativePath"/> itself.
    /// </summary>
    public static SiteResult<string> MaintainedSshHelper(string helperPath)
    {
        var helper = Path.GetFullPath(helperPath);
        if (!File.Exists(helper))
        {
            return SiteResult<string>.Refused(SiteFailure.HelperMissing(
                $"maintained RunPod SSH helper {helper} is missing; "
                + "Fern will not construct an ad-hoc ssh command"));
        }
        return SiteResult<string>.Ok(helper);
    }

    // ---- one child, one deadline ----

    public static SiteFailure ClassifyMaintainedSshHelperFailure(string detail, int exitStatus, string standardError)
    {
        // CHG: the helper's missing completion evidence outranks diagnostic
        // text emitted before disconnect. It cannot establish a permanent
        // source/auth refusal; exit status alone is not sufficient either.
        // CHG: classify completion before display truncation can discard its header.
        if (exitStatus == 22 && System.Text.RegularExpressions.Regex.IsMatch(standardError,
            @"runpod-ssh: (interactive gateway handshake|remote command completion) failed \((EOF|TIMEOUT)\); remote experiment state is UNKNOWN",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return SiteFailure.HostUnreachable(detail.Contains("remote experiment state is UNKNOWN", StringComparison.Ordinal)
                ? detail : "remote experiment state is UNKNOWN; " + detail);
        if (detail.Contains("runpod-ssh: source verification refused:", StringComparison.Ordinal))
            return SiteFailure.Permanent(detail);
        var lower = detail.ToLowerInvariant();
        if (lower.Contains("host key verification failed", StringComparison.Ordinal)
            || lower.Contains("remote host identification has changed", StringComparison.Ordinal)
            || lower.Contains("known_hosts", StringComparison.Ordinal))
        {
            return SiteFailure.HostKeyMismatch(detail);
        }
        if (lower.Contains("permission denied", StringComparison.Ordinal)
            || lower.Contains("authentication refused", StringComparison.Ordinal)
            || lower.Contains("authentication failed", StringComparison.Ordinal)
            || lower.Contains("too many authentication failures", StringComparison.Ordinal)
            || lower.Contains("publickey", StringComparison.Ordinal))
        {
            return SiteFailure.AuthenticationRefused(detail);
        }
        if (lower.Contains("identity file", StringComparison.Ordinal)
            || lower.Contains("runpod_ssh_key", StringComparison.Ordinal)
            || lower.Contains("account-authorized", StringComparison.Ordinal)
            || lower.Contains("bad permissions", StringComparison.Ordinal)
            || lower.Contains("unprotected private key file", StringComparison.Ordinal))
        {
            return SiteFailure.CredentialConfiguration(detail);
        }
        if (lower.Contains("connection timed out", StringComparison.Ordinal)
            || lower.Contains("operation timed out", StringComparison.Ordinal)
            || lower.Contains("could not resolve hostname", StringComparison.Ordinal)
            || lower.Contains("temporary failure in name resolution", StringComparison.Ordinal)
            || lower.Contains("name or service not known", StringComparison.Ordinal)
            || lower.Contains("network is unreachable", StringComparison.Ordinal)
            || lower.Contains("no route to host", StringComparison.Ordinal)
            || lower.Contains("connection refused", StringComparison.Ordinal))
        {
            return SiteFailure.HostUnreachable(detail);
        }
        if (lower.Contains("site session", StringComparison.Ordinal)
            || lower.Contains("fern-site", StringComparison.Ordinal)
            || lower.Contains("serve --stdio", StringComparison.Ordinal))
        {
            return SiteFailure.SiteUnhealthy(detail);
        }
        return SiteFailure.ConnectionAdmission(detail);
    }

    static bool IsMaintainedSshHelper(string fileName) =>
        string.Equals(Path.GetFileName(fileName), Path.GetFileName(MaintainedSshHelperRelativePath), StringComparison.Ordinal);

    /// <summary>Keep the END of a child's standard error, within the Site's cap.</summary>
    /// <summary>
    /// How many whole stdout lines are held for the caller's tail. The byte cap
    /// in <see cref="Tail"/> is the real bound; this keeps memory flat for a
    /// runner that streams for an hour.
    /// </summary>
    const int RetainedOutputLines = 256;

    static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static bool TryReadUnsigned(JsonElement element, string name, out long number)
    {
        number = 0;
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number)
            && number >= 0;
    }
}

/// <summary>
/// Stable identity of the one remote computation owned by a cell attempt.
/// It survives endpoint/session replacement and is never a PID or transport id.
/// </summary>
[JsonConverter(typeof(RemoteExecutionIdJsonConverter))]
public readonly record struct RemoteExecutionId
{
    public string Value { get; }

    public RemoteExecutionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("remote-execution-", StringComparison.Ordinal))
            throw new ArgumentException("remote execution id needs the remote-execution- prefix", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}

public sealed class RemoteExecutionIdJsonConverter : JsonConverter<RemoteExecutionId>
{
    public override RemoteExecutionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("remote execution id is null"));

    public override void Write(Utf8JsonWriter writer, RemoteExecutionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

