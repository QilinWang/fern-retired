using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// The Home, served: one process on the machine that holds the Registry file
/// answers every <see cref="IStudyStore"/> operation as JSON over HTTP, so a
/// supervisor on another machine (the pod) works the same loop against the
/// same file without opening it over a network filesystem, which SQLite
/// forbids.
///
/// Why pull (owner ruling 2026-09-11 night, design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md
/// section 10): each machine runs its own supervisor, claims its own cells and
/// beats for them, and the Home keeps only the facts it always kept. The push
/// alternative (a local session driving the pod over SSH, the retired
/// SiteSessionPool) made the local machine's session the pod's heartbeat, so
/// a dropped SSH session lost cells that were still training. Under pull a
/// dropped tunnel is a supervisor that cannot renew: its attempt goes silent,
/// is reconciled as lost after <see cref="RegistryOperations.StaleAfter"/>,
/// and is claimed again, the same rule a local supervisor's crash follows.
/// CHG 2026-09-26 (slice W2): no longer. A silent attempt keeps its cell and
/// its slot, since it may still be training; the Home tells the owner after
/// 30 minutes, and a person gives it up with `retry-cell`.
///
/// The wire: POST /v1/&lt;operation&gt; with the operation's arguments as one
/// JSON object whose keys are the parameter names; the reply is one envelope,
/// <c>{"ok":true,"value":...}</c> or <c>{"ok":false,"refusal":{...}}</c>, so
/// a refusal (fenced, held, not found) crosses as a refusal and never as an
/// HTTP error. An HTTP error is the wire itself: 404 an operation nobody
/// serves, 400 arguments that do not read, 500 the store throwing. Requests
/// are answered one at a time on one thread over one writer, which is the
/// file's own rule. GET /v1/health names the file, for a person checking a
/// tunnel.
///
/// The listener binds a loopback address; the pod reaches it through the SSH
/// tunnel's remote forward (<c>ssh -R 8621:127.0.0.1:8621 pod</c>), so nothing
/// is open on the network and the tunnel is the credential.
///
/// Human CLI and Dashboard routes are served by fern_home on Gleam.
/// This listener retains only the legacy supervisor protocol.
/// </summary>
public sealed class HomeService : IDisposable
{
    public const string Version = "v1";
    public const string Schema = "fern.home_service.v1";
    public const string DefaultListen = "http://127.0.0.1:8621/";

    readonly SqliteStudyStore _store;

    readonly HttpListener _listener = new();
    readonly Thread _thread;
    readonly TextWriter? _log;
    volatile bool _stopping;
    // CHG 2026-09-26 (slices R3, W2, W3): the Home's notices read the same
    // store on their own clock; requests and those reads take turns on it.
    readonly Lock _storeGate = new();

    /// <summary>Use the Home's store between requests, never beside one.</summary>
    public T WithStore<T>(Func<IStudyStore, T> use)
    {
        lock (_storeGate) return use(_store);
    }

    public HomeService(RegistryWriter writer, string listen, TextWriter? log = null)
    {
        _store = new SqliteStudyStore(writer);
        _listener.Prefixes.Add(listen.EndsWith('/') ? listen : listen + "/");
        _log = log;
        _thread = new Thread(Serve) { IsBackground = true, Name = "fern-home-service" };
    }

    /// <summary>The address answered, with its trailing slash.</summary>
    public string Prefix => _listener.Prefixes.First();

    public void Start()
    {
        _listener.Start();
        _thread.Start();
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _listener.Stop();
        _listener.Close();
        if (_thread.IsAlive && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose() => Stop();

    void Serve()
    {
        while (!_stopping)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (_stopping) return;
                _log?.WriteLine($"home: the listener failed: {error.Message}");
                return;
            }
            try
            {
                lock (_storeGate) Handle(context);
            }
            catch (Exception error)
            {
                _log?.WriteLine($"home: {context.Request.Url?.AbsolutePath} threw: {error.Message}");
                try
                {
                    Reply(context.Response, 500, HomeWire.Error(error.Message));
                }
                catch (Exception)
                {
                    // The client is gone; there is nobody to tell.
                }
            }
        }
    }

    void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var head = $"/{Version}/";
        if (request.HttpMethod == "GET" && path == head + "health")
        {
            Reply(context.Response, 200, new JsonObject
            {
                ["schema"] = Schema,
                ["home"] = _store.Writer.Connection.DataSource,
            }.ToJsonString());
            return;
        }
        if (request.HttpMethod != "POST" || !path.StartsWith(head, StringComparison.Ordinal))
        {
            Reply(context.Response, 404, HomeWire.Error($"nothing answers {request.HttpMethod} {path}; operations are POST {head}<operation>"));
            return;
        }
        var operation = path[head.Length..];
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            Reply(context.Response, 400, HomeWire.Error($"the arguments of {operation} are not readable JSON: {error.Message}"));
            return;
        }
        string reply;
        try
        {
            // CHG: claims and supervisor acquisition retain one request identity
            // across HTTP retries, including a reply lost after the commit.
            var requestId = request.Headers["Idempotency-Key"];
            reply = requestId is null ? Dispatch(operation, arguments)
                : _store.Writer.Replay(requestId, operation, body, () => Dispatch(operation, arguments));
        }
        catch (UnknownOperationException error)
        {
            Reply(context.Response, 404, HomeWire.Error(error.Message));
            return;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Reply(context.Response, 400, HomeWire.Error($"{operation}: {error.Message}"));
            return;
        }
        Reply(context.Response, 200, reply);
    }

    /// <summary>One operation of <see cref="IStudyStore"/> by name, its arguments by parameter name.</summary>
    string Dispatch(string operation, JsonElement args) => operation switch
    {
        "ListRunnableCells" => HomeWire.Value(_store.ListRunnableCells(ReadNullableLong(args, "studyId"), ReadLong(args, "maxAttempts"))),
        "ListSites" => HomeWire.Value(_store.ListSites()),
        "PendingRequests" => HomeWire.Value(_store.PendingRequests(ReadString(args, "kind"), ReadStrings(args, "scopes"))),
        "ReadStudy" => HomeWire.Result(_store.ReadStudy(ReadLong(args, "studyId"))),
        "ReadCell" => HomeWire.Value(_store.ReadCell(ReadLong(args, "cellId"))),
        "BusySlots" => HomeWire.Value(_store.BusySlots().Select(pair => new HomeWire.BusySlot(pair.Key, pair.Value)).ToList()),
        "ListUnfinishedCells" => HomeWire.Value(_store.ListUnfinishedCells(ReadNullableLong(args, "studyId"))),
        "ListSiteSupervisors" => HomeWire.Value(_store.ListSiteSupervisors()),
        "ReadDispatchOrder" => HomeWire.Value(_store.ReadDispatchOrder()),
        "ReadStudyCompiler" => HomeWire.Value(_store.ReadStudyCompiler(ReadLong(args, "studyId"))),
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): ListSiteSources and
        // ReadSiteCompiler are retired with the tables they read; a supervisor older than
        // this asks for them every cycle and is refused, so it is replaced before this Home.
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): a source's commit and declared files.
        "ReadSource" => HomeWire.Value(_store.ReadSource(ReadLong(args, "sourceId"))),
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 3): a remote
        // worker asks whether its cell's code can be given, then asks for it.
        "CannotGiveCode" => HomeWire.Value(_store.CannotGiveCode(ReadLong(args, "siteId"), ReadLong(args, "sourceId"))),
        "PackCellCode" => HomeWire.Result(_store.PackCellCode(ReadLong(args, "attemptId"), ReadLong(args, "fencingEpoch"))),
        "ListSiteFailures" => HomeWire.Value(_store.ListSiteFailures()),
        "FindStudy" => HomeWire.Value(_store.FindStudy(ReadString(args, "name"))),
        "StudyAttemptBudget" => HomeWire.Value(_store.StudyAttemptBudget(ReadLong(args, "studyId"))),
        // CHG 2026-09-26 (slice W2, owner: silence never re-dispatches): a supervisor built before W2
        // asks this at the start of every cycle; the Home closes nothing for it, so a silent attempt
        // keeps its cell and card until a person gives it up with retry-cell.
        "ReconcileSilentAttempts" => HomeWire.Result(
            RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>>.Ok([])),
        // CHG 2026-09-26 (slice W1): an older supervisor sends no office epoch, and claims as before.
        "TryStartAttempt" => HomeWire.Result(_store.TryStartAttempt(
            ReadLong(args, "cellId"), ReadLong(args, "siteId"), ReadString(args, "requestJson"), ReadLong(args, "maxAttempts"),
            ReadNullableLong(args, "officeEpoch"))),
        "RenewAttempt" => HomeWire.Result(_store.RenewAttempt(
            ReadLong(args, "attemptId"), ReadLong(args, "fencingEpoch"), ReadNullableString(args, "remoteExecutionId"))),
        "RecordEvent" => HomeWire.Result(_store.RecordEvent(
            ReadLong(args, "attemptId"), ReadLong(args, "fencingEpoch"), ReadString(args, "kind"),
            ReadNullableString(args, "phase"), ReadString(args, "detailJson"))),
        "FinishAttempt" => HomeWire.Result(_store.FinishAttempt(
            ReadLong(args, "attemptId"), ReadLong(args, "fencingEpoch"), ReadString(args, "resultJson"))),
        "FailAttempt" => HomeWire.Result(_store.FailAttempt(
            ReadLong(args, "attemptId"), ReadLong(args, "fencingEpoch"), ReadString(args, "state"), ReadString(args, "detailJson"))),
        "StartSupervisor" => HomeWire.Result(_store.StartSupervisor(
            ReadLong(args, "siteId"), ReadString(args, "host"), ReadLong(args, "pid"), ReadString(args, "binarySha256"),
            ReadLong(args, "leaseSeconds"))),
        "RenewSupervisor" => HomeWire.Result(_store.RenewSupervisor(ReadPermit(args), ReadLong(args, "leaseSeconds"))),
        "EndSupervisor" => HomeWire.Result(_store.EndSupervisor(
            ReadPermit(args), ReadString(args, "outcome"), ReadString(args, "disposition"), ReadLong(args, "exitCode"))),
        "ConsumeStop" => HomeWire.Result(_store.ConsumeStop(ReadPermit(args))),
        _ => throw new UnknownOperationException(operation),
    };

    static JsonElement Field(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value
            : throw new ArgumentException($"the arguments carry no {name}");

    static long ReadLong(JsonElement args, string name) => Field(args, name).GetInt64();

    static long? ReadNullableLong(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt64()
            : null;

    static string ReadString(JsonElement args, string name) =>
        Field(args, name).GetString() ?? throw new ArgumentException($"{name} is null");

    static string? ReadNullableString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    static IReadOnlyList<string> ReadStrings(JsonElement args, string name) =>
        Field(args, name).EnumerateArray().Select(item => item.GetString() ?? throw new ArgumentException($"{name} holds a null")).ToList();

    static RegistryOperations.SupervisorPermit ReadPermit(JsonElement args) =>
        Field(args, "permit").Deserialize<RegistryOperations.SupervisorPermit>(HomeWire.Options)
        ?? throw new ArgumentException("permit is null");

    static void Reply(HttpListenerResponse response, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }

    sealed class UnknownOperationException(string operation)
        : Exception($"no store operation is named {operation}");
}

/// <summary>
/// The envelope both ends agree on. Values are the store's own records
/// serialized by name; a refusal is written field by field so its shape is a
/// schema on the wire and not an accident of reflection.
/// </summary>
public static class HomeWire
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    /// <summary>How many slots one Site holds, as the wire carries a dictionary keyed by Site.</summary>
    public sealed record BusySlot(long SiteId, int Busy);

    public static string Value<T>(T value) =>
        new JsonObject { ["ok"] = true, ["value"] = JsonSerializer.SerializeToNode(value, Options) }.ToJsonString();

    public static string Result<T>(RegistryResult<T> result)
    {
        if (result.TryGetValue(out var value))
        {
            return Value(value);
        }
        result.TryGetRefusal(out var refusal);
        return new JsonObject { ["ok"] = false, ["refusal"] = WriteRefusal(refusal) }.ToJsonString();
    }

    public static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    public static JsonObject WriteRefusal(RegistryRefusal refusal) => new()
    {
        ["outcome_kind"] = refusal.OutcomeKind,
        ["error_category"] = refusal.ErrorCategory,
        ["detail"] = refusal.Detail,
        ["holder_actor"] = refusal.HolderActor,
        ["holder_host"] = refusal.HolderHost,
        ["lease_id"] = refusal.LeaseId,
        ["stale_epoch"] = refusal.StaleEpoch,
        ["successor_epoch"] = refusal.SuccessorEpoch,
    };

    public static RegistryRefusal ReadRefusal(JsonElement refusal) => new(
        Text(refusal, "outcome_kind") ?? "refused",
        Text(refusal, "error_category"),
        Text(refusal, "detail") ?? "",
        Text(refusal, "holder_actor"),
        Text(refusal, "holder_host"),
        Text(refusal, "lease_id"),
        Number(refusal, "stale_epoch"),
        Number(refusal, "successor_epoch"));

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}

/// <summary>The Home did not answer for the whole retry window: the tunnel is down or nothing serves.</summary>
public sealed class HomeUnreachableException(string message) : Exception(message);

/// <summary>The Home answered, but not as this client speaks: a version or argument mismatch, never a transport failure.</summary>
public sealed class HomeProtocolException(string message) : Exception(message);

/// <summary>
/// The store as seen from another machine: every <see cref="IStudyStore"/>
/// operation posted to <see cref="HomeService"/>, the reply's envelope read
/// back into the same records and the same <see cref="RegistryResult{T}"/>
/// the file returns, so the loop and the worker cannot tell the two apart.
///
/// A transport failure is retried, with a pause between tries, for the retry
/// window, because a tunnel flaps and a Home is restarted; after the window
/// it is <see cref="HomeUnreachableException"/>, which the caller turns into
/// a retryable exit. The default window (five minutes) is a quarter of
/// <see cref="RegistryOperations.StaleAfter"/>, so a client that gives up has
/// not yet been declared silent.
/// </summary>
public sealed class HomeClient : IStudyStore, IDisposable
{
    public static readonly TimeSpan DefaultRetryFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    readonly HttpClient _http;
    readonly TimeSpan _retryFor;
    readonly TimeSpan _backoff;
    readonly TextWriter? _log;

    public HomeClient(Uri home, TimeSpan? retryFor = null, TimeSpan? backoff = null, TextWriter? log = null, HttpMessageHandler? handler = null)
    {
        var address = home.AbsoluteUri.EndsWith('/') ? home : new Uri(home.AbsoluteUri + "/");
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = address, Timeout = RequestTimeout };
        _retryFor = retryFor ?? DefaultRetryFor;
        _backoff = backoff ?? DefaultBackoff;
        _log = log;
    }

    public Uri Home => _http.BaseAddress!;

    public void Dispose() => _http.Dispose();

    /// <summary>GET /v1/health: the served file's path, or the exception a tunnel that is down raises.</summary>
    public string HealthHome()
    {
        var envelope = Call("health", null);
        return envelope.TryGetProperty("home", out var home) ? home.GetString() ?? "" : "";
    }

    public IReadOnlyList<RegistryOperations.RunnableCell> ListRunnableCells(long? studyId, long maxAttempts) =>
        Value<List<RegistryOperations.RunnableCell>>(Call("ListRunnableCells", new { studyId, maxAttempts }));

    public IReadOnlyList<RegistryOperations.Site> ListSites() =>
        Value<List<RegistryOperations.Site>>(Call("ListSites", new { }));


    public IReadOnlyList<RegistryOperations.OperatorRequest> PendingRequests(string kind, IReadOnlyList<string> scopes) =>
        Value<List<RegistryOperations.OperatorRequest>>(Call("PendingRequests", new { kind, scopes }));

    public RegistryResult<RegistryOperations.StudyView> ReadStudy(long studyId) =>
        Result<RegistryOperations.StudyView>(Call("ReadStudy", new { studyId }));

    public RegisteredCell? ReadCell(long cellId) =>
        Value<RegisteredCell?>(Call("ReadCell", new { cellId }));

    public IReadOnlyList<UnfinishedCell> ListUnfinishedCells(long? studyId) =>
        Value<List<UnfinishedCell>>(Call("ListUnfinishedCells", new { studyId }));

    public IReadOnlyList<RegistryOperations.SupervisorView> ListSiteSupervisors() =>
        Value<List<RegistryOperations.SupervisorView>>(Call("ListSiteSupervisors", new { }));

    public RegistryOperations.DispatchOrder ReadDispatchOrder() =>
        Value<RegistryOperations.DispatchOrder>(Call("ReadDispatchOrder", new { }));

    public RegistryOperations.StudyCompiler? ReadStudyCompiler(long studyId) =>
        Value<RegistryOperations.StudyCompiler?>(Call("ReadStudyCompiler", new { studyId }));


    public RegistryOperations.SourceDeclaration? ReadSource(long sourceId) =>
        Value<RegistryOperations.SourceDeclaration?>(Call("ReadSource", new { sourceId }));

    public string? CannotGiveCode(long siteId, long sourceId) =>
        Value<string?>(Call("CannotGiveCode", new { siteId, sourceId }));

    public RegistryResult<CellCode.Package> PackCellCode(long attemptId, long fencingEpoch) =>
        Result<CellCode.Package>(Call("PackCellCode", new { attemptId, fencingEpoch }));

    public IReadOnlyList<RegistryOperations.SiteFailure> ListSiteFailures() =>
        Value<List<RegistryOperations.SiteFailure>>(Call("ListSiteFailures", new { }));

    public IReadOnlyDictionary<long, int> BusySlots() =>
        Value<List<HomeWire.BusySlot>>(Call("BusySlots", new { })).ToDictionary(slot => slot.SiteId, slot => slot.Busy);

    public long? FindStudy(string name) => Value<long?>(Call("FindStudy", new { name }));

    public long StudyAttemptBudget(long studyId) => Value<long>(Call("StudyAttemptBudget", new { studyId }));

    public RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>> ReconcileSilentAttempts(long? studyId)
    {
        var envelope = Call("ReconcileSilentAttempts", new { studyId });
        return envelope.GetProperty("ok").GetBoolean()
            ? RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>>.Ok(Value<List<RegistryOperations.ReconciledAttempt>>(envelope))
            : RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>>.Rejected(HomeWire.ReadRefusal(envelope.GetProperty("refusal")));
    }

    public RegistryResult<RegistryOperations.StartedAttempt> TryStartAttempt(
        long cellId, long siteId, string requestJson, long maxAttempts, long? officeEpoch = null) =>
        Result<RegistryOperations.StartedAttempt>(Call("TryStartAttempt", new { cellId, siteId, requestJson, maxAttempts, officeEpoch }));

    public RegistryResult<long> RenewAttempt(long attemptId, long fencingEpoch, string? remoteExecutionId) =>
        Result<long>(Call("RenewAttempt", new { attemptId, fencingEpoch, remoteExecutionId }));

    public RegistryResult<long> RecordEvent(long attemptId, long fencingEpoch, string kind, string? phase, string detailJson) =>
        Result<long>(Call("RecordEvent", new { attemptId, fencingEpoch, kind, phase, detailJson }));

    public RegistryResult<long> FinishAttempt(long attemptId, long fencingEpoch, string resultJson) =>
        Result<long>(Call("FinishAttempt", new { attemptId, fencingEpoch, resultJson }));

    public RegistryResult<long> FailAttempt(long attemptId, long fencingEpoch, string state, string detailJson) =>
        Result<long>(Call("FailAttempt", new { attemptId, fencingEpoch, state, detailJson }));

    public RegistryResult<RegistryOperations.SupervisorPermit> StartSupervisor(long siteId, string host, long pid, string binarySha256, long leaseSeconds) =>
        Result<RegistryOperations.SupervisorPermit>(Call("StartSupervisor", new { siteId, host, pid, binarySha256, leaseSeconds }));

    public RegistryResult<string> RenewSupervisor(RegistryOperations.SupervisorPermit permit, long leaseSeconds) =>
        Result<string>(Call("RenewSupervisor", new { permit, leaseSeconds }));

    public RegistryResult<long> EndSupervisor(RegistryOperations.SupervisorPermit permit, string outcome, string disposition, long exitCode) =>
        Result<long>(Call("EndSupervisor", new { permit, outcome, disposition, exitCode }));

    public RegistryResult<RegistryOperations.OperatorRequest?> ConsumeStop(RegistryOperations.SupervisorPermit permit) =>
        Result<RegistryOperations.OperatorRequest?>(Call("ConsumeStop", new { permit }));

    static T Value<T>(JsonElement envelope) => envelope.GetProperty("value").Deserialize<T>(HomeWire.Options)!;

    static RegistryResult<T> Result<T>(JsonElement envelope) =>
        envelope.GetProperty("ok").GetBoolean()
            ? RegistryResult<T>.Ok(Value<T>(envelope))
            : RegistryResult<T>.Rejected(HomeWire.ReadRefusal(envelope.GetProperty("refusal")));

    /// <summary>
    /// One request, retried over the window. A 5xx is the store throwing (a
    /// busy file, a Home being restarted) and is retried like a dropped
    /// connection; a 4xx is this client speaking wrongly and is not.
    /// </summary>
    JsonElement Call(string operation, object? arguments)
    {
        var body = arguments is null ? null : JsonSerializer.Serialize(arguments, HomeWire.Options);
        var requestId = operation is "TryStartAttempt" or "StartSupervisor" or "FinishAttempt" or "FailAttempt"
            or "RecordEvent" or "EndSupervisor" or "ConsumeStop" ? Guid.NewGuid().ToString("N") : null;
        var deadline = DateTimeOffset.UtcNow + _retryFor;
        var tries = 0;
        while (true)
        {
            tries++;
            string failure;
            try
            {
                using var request = body is null
                    ? new HttpRequestMessage(HttpMethod.Get, $"{HomeService.Version}/{operation}")
                    : new HttpRequestMessage(HttpMethod.Post, $"{HomeService.Version}/{operation}")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                if (requestId is not null) request.Headers.Add("Idempotency-Key", requestId);
                using var response = _http.Send(request);
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.Clone();
                }
                if ((int)response.StatusCode < 500)
                {
                    throw new HomeProtocolException($"the Home at {Home} refused {operation}: HTTP {(int)response.StatusCode} {text}");
                }
                failure = $"HTTP {(int)response.StatusCode} {text}";
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                failure = error.Message;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new HomeUnreachableException(
                    $"the Home at {Home} did not answer {operation} in {tries} tries over {_retryFor.TotalSeconds:F0} s: {failure}");
            }
            _log?.WriteLine($"home: {operation} failed ({failure}); trying again in {_backoff.TotalSeconds:F0} s");
            Thread.Sleep(_backoff);
        }
    }
}
