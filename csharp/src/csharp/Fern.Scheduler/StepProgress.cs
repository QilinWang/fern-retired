using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;
using Fern.Site;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

[JsonConverter(typeof(JsonStringEnumConverter<StepState>))]
public enum StepState { NotStarted, Running, Succeeded, Reused, Skipped, Refused, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<StepWorkUnit>))]
public enum StepWorkUnit { Files, Bytes }

public sealed record StepWork(StepWorkUnit Unit, long Completed, long Total);

/// <summary>A bounded observation of one node in one preparation invocation; never a Cell outcome.</summary>
public sealed record StepObservation(
    string InvocationId, string Path, string? ParentPath, int Position,
    StepState State, string Detail, long? StartedAtMs, long ObservedAtMs)
{
    public string Schema => "fern.preparation_step.v1";
    public string? StagePlanInstanceId { get; init; }
    public string? CellAttemptId { get; init; }
    public string? SiteId { get; init; }
    public StepWork? Work { get; init; }
}

/// <summary>An immutable name and ordered children for observation; contains no execution behavior.</summary>
public sealed class StepDefinition
{
    public string Name { get; }
    public IReadOnlyList<StepDefinition> Children { get; }

    public StepDefinition(string name, params StepDefinition[] children)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(children);
        var copied = children.ToArray();
        if (copied.Any(child => child is null))
            throw new ArgumentException($"step {name} contains a null child", nameof(children));
        if (copied.Select(child => child.Name).Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException($"step {name} repeats a child name", nameof(children));
        Name = name;
        Children = Array.AsReadOnly(copied);
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Contains('/') || name.Contains('\\')
            || name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException($"step name must be one nonempty path segment: {name}", nameof(name));
    }
}

/// <summary>Select a full preparation path in retained observations; never a declaration of work.</summary>
public sealed record PreparationStepSelection
{
    [JsonPropertyName("path")]
    public string Path { get; }
    [JsonPropertyName("invocation_id")]
    public string? InvocationId { get; }

    public PreparationStepSelection(string path, string? invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (var segment in path.Split('/')) StepDefinition.ValidateName(segment);
        if (invocationId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
            if (invocationId.Any(char.IsControl))
                throw new ArgumentException("preparation invocation must not contain control characters", nameof(invocationId));
        }
        Path = path;
        InvocationId = invocationId;
    }

    public IReadOnlyList<StepObservation> Select(IReadOnlyList<StepObservation> nodes)
    {
        var invocations = nodes.Where(node => node.Path == Path
            && (InvocationId is null || node.InvocationId == InvocationId))
            .Select(node => node.InvocationId).ToHashSet(StringComparer.Ordinal);
        if (invocations.Count == 0)
            throw new MonitorRefusal($"no path {Path}"
                + (InvocationId is null ? "" : $" in invocation {InvocationId}")
                + " in retained preparation observations (historical events retain at most 512 events and eight invocations; operation snapshots have a separate bound); "
                + "this does not establish whether the step ran; read monitor without the selection to inspect retained paths");
        // CHG: keep only calls containing the exact selected node. Preserve
        // retained ancestors for context; a matching display name is insufficient.
        return nodes.Where(node => invocations.Contains(node.InvocationId)
            && (node.Path == Path || node.Path.StartsWith(Path + "/", StringComparison.Ordinal)
                || Path.StartsWith(node.Path + "/", StringComparison.Ordinal))).ToArray();
    }
}

/// <summary>
/// Records a concrete Step's linear substeps. The supplied writer records
/// observations; a monitor reader never supplies this callback or controls work.
/// </summary>
public sealed class StepProgress
{
    readonly Action<StepObservation>? _observe;
    readonly Dictionary<string, StepObservation> _nodes;
    readonly Dictionary<string, long> _lastWorkEmission = new(StringComparer.Ordinal);
    bool _begun;
    public long MutationCallsStarted { get; private set; }
    public SiteFailure? LastRefusal { get; private set; }
    public IReadOnlyList<StepObservation> Snapshot => _nodes.Values.OrderBy(node => node.Position).ToArray();

    public StepProgress(Action<StepObservation>? observe, string invocation, IReadOnlyList<StepDefinition> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocation);
        ArgumentNullException.ThrowIfNull(steps);
        _observe = observe;
        _nodes = new(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // CHG: capture the complete declared tree before Begin can emit a fact.
        // The stack preserves preorder without making depth a CLR stack limit.
        var pending = new Stack<(StepDefinition Step, string? Parent)>();
        foreach (var step in steps.ToArray().Reverse()) pending.Push((step, null));
        while (pending.TryPop(out var node))
        {
            if (node.Step is null) throw new ArgumentException("step declaration contains a null node", nameof(steps));
            var path = node.Parent is null ? node.Step.Name : node.Parent + "/" + node.Step.Name;
            var observation = new StepObservation(invocation, path, node.Parent, _nodes.Count,
                StepState.NotStarted, "waiting for preceding preparation", null, now);
            if (!_nodes.TryAdd(path, observation))
                throw new ArgumentException($"step path was declared more than once: {path}", nameof(steps));
            for (var index = node.Step.Children.Count - 1; index >= 0; index--)
                pending.Push((node.Step.Children[index], path));
        }
    }

    public void Begin()
    {
        if (_begun) return;
        foreach (var node in _nodes.Values.OrderBy(node => node.Position)) _observe?.Invoke(node);
        _begun = true;
    }

    public void Set(string path, StepState state, string detail)
    {
        var previous = RequireNode(path);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bytes = Encoding.UTF8.GetBytes(detail);
        if (bytes.Length > 2048) detail = Encoding.UTF8.GetString(bytes.AsSpan(0, 2048)) + " [truncated]";
        var next = previous with
        {
            State = state, Detail = detail, ObservedAtMs = now,
            StartedAtMs = previous.StartedAtMs ?? (state == StepState.Running ? now : null)
        };
        _observe?.Invoke(next);
        _nodes[path] = next;
    }

    public SiteResult<T> Run<T>(string path, Func<SiteResult<T>> operation, bool changesFiles)
    {
        Set(path, StepState.Running, "running");
        if (changesFiles) MutationCallsStarted++;
        var result = operation();
        if (!result.IsOk) LastRefusal = result.Failure;
        Set(path, result.IsOk ? StepState.Succeeded : FailureState(result.Failure),
            result.IsOk ? "completed and checked" : result.Failure.Detail);
        return result;
    }

    /// <summary>Report measured work; coalesce rapid updates, while Set always publishes the latest count.</summary>
    public void ReportWork(string path, StepWorkUnit unit, long completed, long total)
    {
        var previous = RequireNode(path);
        if (previous.State != StepState.Running || !Enum.IsDefined(unit) || completed < 0 || total < completed)
            throw new ArgumentException("work requires a running step and 0 <= completed <= total");
        if (previous.Work is { } work && (work.Unit != unit || work.Total != total || completed < work.Completed))
            throw new ArgumentException("work unit and total are fixed, and completed work cannot decrease");
        var tick = Environment.TickCount64;
        var next = previous with { Work = new(unit, completed, total), ObservedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (!_lastWorkEmission.TryGetValue(path, out var last) || tick - last >= 250 || completed == total)
        {
            _observe?.Invoke(next);
            _lastWorkEmission[path] = tick;
        }
        _nodes[path] = next;
    }

    public void Stop(string path, SiteFailure failure)
    {
        LastRefusal = failure;
        Set(path, FailureState(failure), failure.Detail);
        foreach (var node in _nodes.Values.Where(node => node.State == StepState.NotStarted).ToArray())
            Set(node.Path, StepState.NotStarted, $"not executed: preparation stopped at {path}: {failure.Detail}");
    }

    public void StopRunning(SiteFailure failure)
    {
        foreach (var node in _nodes.Values.Where(node => node.State == StepState.Running).ToArray())
            Stop(node.Path, failure);
    }

    public void SkipUnstarted(string path, string reason)
    {
        _ = RequireNode(path);
        foreach (var node in _nodes.Values.Where(node => node.State == StepState.NotStarted
            && (node.Path == path || node.Path.StartsWith(path + "/", StringComparison.Ordinal))).ToArray())
            Set(node.Path, StepState.Skipped, reason);
    }

    StepObservation RequireNode(string path) => _nodes.TryGetValue(path, out var node) ? node
        : throw new ArgumentException($"preparation step {path} was not declared", nameof(path));

    static StepState FailureState(SiteFailure failure) =>
        failure.Category == SiteFailureCategory.HostUnreachable || failure.RetryPolicy == SiteRetryPolicy.Unclassified
            ? StepState.Unknown : StepState.Refused;

    /// <summary>Read only the latest bounded preparation invocations from the existing observation stream.</summary>
    public static IReadOnlyList<StepObservation> Read(SqliteConnection connection, string planId)
    {
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT detail_json FROM plan_watch_events
            WHERE plan_id = $plan
              AND json_extract(CASE WHEN json_valid(detail_json) THEN detail_json ELSE '{}' END, '$.Schema') = 'fern.preparation_step.v1'
            ORDER BY event_seq DESC LIMIT 512
            """;
        query.Parameters.AddWithValue("$plan", planId);
        using var rows = query.ExecuteReader();
        var latest = new Dictionary<(string Invocation, string Path), StepObservation>();
        var invocations = new List<string>();
        while (rows.Read())
        {
            var observation = JsonSerializer.Deserialize<StepObservation>(rows.GetString(0))
                ?? throw new RegistryFault("preparation observation is empty");
            if (!invocations.Contains(observation.InvocationId)) invocations.Add(observation.InvocationId);
            if (invocations.Count > 8) break;
            latest.TryAdd((observation.InvocationId, observation.Path), observation);
        }
        return latest.Values.OrderBy(node => invocations.IndexOf(node.InvocationId))
            .ThenBy(node => node.Position).ToArray();
    }

    public static string Render(IReadOnlyList<StepObservation> nodes)
        => string.Join('\n', nodes.GroupBy(node => node.InvocationId).Select(group =>
            $"Preparation {group.Key} | Site {group.First().SiteId} | Cell {group.First().StagePlanInstanceId} | Attempt {group.First().CellAttemptId}\n" +
            string.Join('\n', group.OrderBy(node => node.Position).Select(node =>
                $"{new string(' ', 2 * node.Path.Count(character => character == '/'))}{node.Path.Split('/')[^1]}: {node.State} — {node.Detail} "
                + (node.Work is { } work ? $"[{work.Completed}/{work.Total} {work.Unit}] " : "")
                + $"(observed {DateTimeOffset.FromUnixTimeMilliseconds(node.ObservedAtMs):O})"))));
}
