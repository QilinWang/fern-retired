using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fern.Site;

/// <summary>
/// One phase of a cell on this Site's resident Python worker — Program 18,
/// Site-C. The Home issues the phase as an ordinary Stage package (request
/// plus the inputs the Site does not already hold) with one extra file,
/// <c>phase/cell_phase.v1.json</c>, naming the cell, the ordinal and which
/// earlier phases feed which input ports. The Site materializes the package
/// into the phase runtime, makes sure a worker serves the instructions'
/// installed source root, submits a one-phase cell request, tails the cell log
/// for epoch lines, and when the worker's receipt appears reopens the sealed
/// outputs itself and writes its reading beside the runner report. The return
/// package then carries the report and the two readings; the bytes stay here.
/// </summary>
public static class CellPhaseSite
{
    // The worker may outpace transport. Keep one complete validated line,
    // not an unbounded queue of progress history.
    sealed class LatestTrainingProgressWriter : TextWriter
    {
        string? latest;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value)
        {
            if (value is not null && value.Length <= 8192)
                Interlocked.Exchange(ref latest, value.TrimEnd('\r', '\n'));
        }
        public string? Take() => Interlocked.Exchange(ref latest, null);
    }

    public const string SessionSchema = "fern.site_session.v1";
    public const int MaximumSessionHeaderBytes = 64 * 1024;
    public const long MaximumSessionBodyBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>
    /// One binary-clean, length-framed machine session. The stream stays open
    /// across content probes, uploads, cells and explicit fetches. A body is
    /// never encoded as JSON or copied through a PTY.
    /// </summary>
    public static SiteResult<bool> ServeSession(
        Stream input,
        Stream output,
        string workRoot,
        string workerRoot,
        string python,
        TimeSpan cellTimeout)
    {
        var storeRoot = Path.Combine(workRoot, "store");
        Directory.CreateDirectory(storeRoot);
        while (true)
        {
            var envelope = ReadSessionEnvelope(input);
            if (!envelope.IsOk)
            {
                return envelope.RefusalAs<bool>();
            }
            if (envelope.Value is null)
            {
                return SiteResult<bool>.Ok(true);
            }
            var header = envelope.Value.Header;
            var operation = header["operation"]?.GetValue<string>() ?? string.Empty;
            var requestId = header["request_id"]?.GetValue<string>() ?? string.Empty;
            if (requestId.Length == 0)
            {
                return SiteResult<bool>.Permanent("Site session message needs request_id");
            }
            switch (operation)
            {
                case "hello":
                    WriteSessionEnvelope(output, new JsonObject
                    {
                        ["schema"] = SessionSchema,
                        ["operation"] = "hello",
                        ["request_id"] = requestId,
                        ["capabilities"] = new JsonArray("content_store", "begin_cell", "run_phase", "fetch_output"),
                    });
                    break;
                case "probe_content":
                {
                    var digest = RequiredDigest(header, "sha256");
                    if (!digest.IsOk) { WriteSessionRefusal(output, requestId, digest.Failure.Detail); break; }
                    var path = Path.Combine(storeRoot, digest.Value);
                    WriteSessionEnvelope(output, new JsonObject
                    {
                        ["schema"] = SessionSchema,
                        ["operation"] = "content_presence",
                        ["request_id"] = requestId,
                        ["sha256"] = digest.Value,
                        ["present"] = File.Exists(path) && HashFile(path) == digest.Value,
                    });
                    break;
                }
                case "put_content":
                {
                    var digest = RequiredDigest(header, "sha256");
                    if (!digest.IsOk) { WriteSessionRefusal(output, requestId, digest.Failure.Detail); break; }
                    var installed = PublishSessionBody(storeRoot, digest.Value, envelope.Value.Body);
                    if (!installed.IsOk) { WriteSessionRefusal(output, requestId, installed.Failure.Detail); break; }
                    WriteSessionEnvelope(output, new JsonObject
                    {
                        ["schema"] = SessionSchema,
                        ["operation"] = "content_stored",
                        ["request_id"] = requestId,
                        ["sha256"] = digest.Value,
                    });
                    break;
                }
                case "begin_cell":
                {
                    var begun = BeginSessionCell(header, workRoot);
                    if (!begun.IsOk) { WriteSessionRefusal(output, requestId, begun.Failure.Detail); break; }
                    begun.Value["request_id"] = requestId;
                    WriteSessionEnvelope(output, begun.Value);
                    break;
                }
                case "run_phase":
                {
                    SiteResult<JsonObject> run;
                    try
                    {
                        run = RunSessionPhase(
                            header, workRoot, workerRoot, storeRoot, python, cellTimeout,
                            phaseEvent =>
                            {
                                phaseEvent["request_id"] = requestId;
                                WriteSessionEnvelope(output, phaseEvent);
                            });
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        run = SiteResult<JsonObject>.Permanent("cell submission is not readable: " + error.Message);
                    }
                    if (!run.IsOk) { WriteSessionRefusal(output, requestId, run.Failure); break; }
                    run.Value["request_id"] = requestId;
                    WriteSessionEnvelope(output, run.Value);
                    break;
                }
                case "fetch_output":
                {
                    var relative = header["relative_path"]?.GetValue<string>() ?? string.Empty;
                    var contained = ContainedRelativePath(relative);
                    if (!contained.IsOk) { WriteSessionRefusal(output, requestId, contained.Failure.Detail); break; }
                    var path = Path.Combine(workRoot, "cells", StageAttemptSite.FromSlash(relative));
                    var body = StageAttemptSite.ReadRegularFile(path);
                    if (!body.IsOk) { WriteSessionRefusal(output, requestId, body.Failure.Detail); break; }
                    WriteSessionEnvelope(output, new JsonObject
                    {
                        ["schema"] = SessionSchema,
                        ["operation"] = "output",
                        ["request_id"] = requestId,
                        ["relative_path"] = relative,
                        ["sha256"] = StageAttemptSite.Sha256Hex(body.Value),
                    }, body.Value);
                    break;
                }
                default:
                    WriteSessionRefusal(output, requestId, $"Site session operation {operation} is not supported");
                    break;
            }
        }
    }

    static void WriteSessionRefusal(Stream output, string requestId, string detail) =>
        WriteSessionRefusal(output, requestId, SiteFailure.Permanent(detail));

    static void WriteSessionRefusal(Stream output, string requestId, SiteFailure failure) =>
        WriteSessionEnvelope(output, new JsonObject
        {
            ["schema"] = SessionSchema,
            ["operation"] = "refused",
            ["request_id"] = requestId,
            ["failure_category"] = failure.CategoryWire,
            ["retry_policy"] = failure.RetryPolicy == SiteRetryPolicy.Never ? "never" : "unclassified",
            ["detail"] = failure.Detail,
        });

    public sealed record SessionEnvelope(JsonObject Header, byte[] Body);

    public static SiteResult<SessionEnvelope?> ReadSessionEnvelope(Stream input)
    {
        Span<byte> prefix = stackalloc byte[12];
        var first = input.Read(prefix[..1]);
        if (first == 0) return SiteResult<SessionEnvelope?>.Ok(null);
        if (!ReadExactly(input, prefix[1..])) return SiteResult<SessionEnvelope?>.Permanent("Site session frame ended inside its length prefix");
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(prefix[..4]);
        var bodyLength = BinaryPrimitives.ReadInt64BigEndian(prefix[4..]);
        if (headerLength <= 0 || headerLength > MaximumSessionHeaderBytes)
            return SiteResult<SessionEnvelope?>.Permanent($"Site session header length {headerLength} is outside 1..{MaximumSessionHeaderBytes}");
        if (bodyLength < 0 || bodyLength > MaximumSessionBodyBytes || bodyLength > int.MaxValue)
            return SiteResult<SessionEnvelope?>.Permanent($"Site session body length {bodyLength} is outside the in-memory protocol bound");
        var headerBytes = new byte[headerLength];
        var body = new byte[(int)bodyLength];
        if (!ReadExactly(input, headerBytes) || !ReadExactly(input, body))
            return SiteResult<SessionEnvelope?>.Permanent("Site session frame ended before its declared bytes");
        try
        {
            var header = JsonNode.Parse(headerBytes)?.AsObject()
                ?? throw new JsonException("header is not an object");
            if (header["schema"]?.GetValue<string>() != SessionSchema)
                return SiteResult<SessionEnvelope?>.Permanent($"Site session schema must be {SessionSchema}");
            return SiteResult<SessionEnvelope?>.Ok(new SessionEnvelope(header, body));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return SiteResult<SessionEnvelope?>.Permanent("Site session header is not readable: " + error.Message);
        }
    }

    public static void WriteSessionEnvelope(Stream output, JsonObject header, ReadOnlySpan<byte> body = default)
    {
        var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());
        Span<byte> prefix = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(prefix[..4], headerBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(prefix[4..], body.Length);
        output.Write(prefix);
        output.Write(headerBytes);
        output.Write(body);
        output.Flush();
    }

    static bool ReadExactly(Stream input, Span<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var read = input.Read(bytes);
            if (read == 0) return false;
            bytes = bytes[read..];
        }
        return true;
    }

    static SiteResult<string> RequiredDigest(JsonObject header, string field)
    {
        var digest = header[field]?.GetValue<string>() ?? string.Empty;
        return digest.Length == 64 && digest.All(char.IsAsciiHexDigitLower)
            ? SiteResult<string>.Ok(digest)
            : SiteResult<string>.Permanent($"Site session {field} must be 64 lowercase hexadecimal characters");
    }

    static SiteResult<bool> PublishSessionBody(string storeRoot, string digest, byte[] body)
    {
        if (StageAttemptSite.Sha256Hex(body) != digest)
            return SiteResult<bool>.Permanent($"Site session content body does not hash to {digest}");
        var final = Path.Combine(storeRoot, digest);
        if (File.Exists(final))
            return HashFile(final) == digest
                ? SiteResult<bool>.Ok(true)
                : SiteResult<bool>.Permanent($"Site store path {digest} already holds different bytes");
        var partial = final + ".partial-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(partial, body);
        try { File.Move(partial, final, overwrite: false); }
        catch (IOException) when (File.Exists(final)) { File.Delete(partial); }
        File.SetAttributes(final, FileAttributes.ReadOnly);
        return HashFile(final) == digest
            ? SiteResult<bool>.Ok(true)
            : SiteResult<bool>.Permanent($"Site store publication {digest} changed after write");
    }

    static string HashFile(string path) => StageAttemptSite.Sha256Hex(File.ReadAllBytes(path));

    static SiteResult<string> ContainedRelativePath(string relative)
    {
        var parts = relative.Split('/', StringSplitOptions.None);
        return relative.Length > 0 && !Path.IsPathRooted(relative) && parts.All(part => IsOnePathComponent(part))
            ? SiteResult<string>.Ok(relative)
            : SiteResult<string>.Permanent($"Site session path {relative} must be a contained relative path");
    }

    static SiteResult<JsonObject> BeginSessionCell(JsonObject header, string workRoot)
    {
        var permit = ReadSessionPermit(header);
        if (!permit.IsOk) return permit.RefusalAs<JsonObject>();
        var cellRoot = Path.Combine(workRoot, "cells", permit.Value.AttemptId);
        Directory.CreateDirectory(cellRoot);
        var seal = Path.Combine(cellRoot, ".fern-cell-permit.json");
        var immutable = permit.Value.Json.ToJsonString();
        if (File.Exists(seal))
        {
            if (File.ReadAllText(seal) != immutable)
                return SiteResult<JsonObject>.Permanent($"cell attempt {permit.Value.AttemptId} already names another permit");
        }
        else
        {
            File.WriteAllText(seal + ".partial", immutable);
            File.Move(seal + ".partial", seal, overwrite: false);
        }
        return SiteResult<JsonObject>.Ok(new JsonObject
        {
            ["schema"] = SessionSchema,
            ["operation"] = "cell_begun",
            ["cell_attempt_id"] = permit.Value.AttemptId,
            ["remote_execution_id"] = permit.Value.RemoteExecutionId,
            ["parent_plan_epoch"] = permit.Value.ParentEpoch,
        });
    }

    sealed record SessionPermit(string AttemptId, string RemoteExecutionId, long ParentEpoch, JsonObject Json);

    static SiteResult<SessionPermit> ReadSessionPermit(JsonObject header)
    {
        var attemptId = header["cell_attempt_id"]?.GetValue<string>() ?? string.Empty;
        if (!IsOnePathComponent(attemptId)) return SiteResult<SessionPermit>.Permanent("cell operation needs a contained cell_attempt_id");
        var parentEpoch = header["parent_plan_epoch"]?.GetValue<long>() ?? 0;
        if (parentEpoch <= 0) return SiteResult<SessionPermit>.Permanent("cell operation needs a positive parent_plan_epoch");
        var remoteExecutionId = header["remote_execution_id"]?.GetValue<string>() ?? string.Empty;
        if (!IsOnePathComponent(remoteExecutionId) || !remoteExecutionId.StartsWith("remote-execution-", StringComparison.Ordinal))
            return SiteResult<SessionPermit>.Permanent("cell operation needs a contained remote_execution_id");
        var permit = new JsonObject
        {
            ["cell_attempt_id"] = attemptId,
            ["remote_execution_id"] = remoteExecutionId,
            ["parent_plan_epoch"] = parentEpoch,
        };
        foreach (var field in new[] { "stage_plan_instance_id", "site_id", "presented_capability_digest", "cell_request_digest", "cell_submission_digest" })
        {
            var value = header[field]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return SiteResult<SessionPermit>.Permanent($"cell operation needs {field}");
            permit[field] = value;
        }
        return SiteResult<SessionPermit>.Ok(new SessionPermit(attemptId, remoteExecutionId, parentEpoch, permit));
    }

    static SiteResult<JsonObject> RunSessionPhase(
        JsonObject header,
        string workRoot,
        string workerRoot,
        string storeRoot,
        string python,
        TimeSpan timeout,
        Action<JsonObject> phaseEvent)
    {
        var permit = ReadSessionPermit(header);
        if (!permit.IsOk) return permit.RefusalAs<JsonObject>();
        var attemptId = permit.Value.AttemptId;
        var parentPlanEpoch = permit.Value.ParentEpoch;
        var cellRoot = Path.Combine(workRoot, "cells", attemptId);
        var permitSeal = Path.Combine(cellRoot, ".fern-cell-permit.json");
        if (!File.Exists(permitSeal) || File.ReadAllText(permitSeal) != permit.Value.Json.ToJsonString())
            return SiteResult<JsonObject>.Permanent($"cell attempt {attemptId} was not begun with this permit");
        var ordinal = header["ordinal"]?.GetValue<long>() ?? -1;
        var stageId = header["stage_id"]?.GetValue<string>() ?? string.Empty;
        var handler = header["handler"]?.GetValue<string>() ?? string.Empty;
        var historicalStageAttemptId = header["historical_stage_attempt_id"]?.GetValue<string>() ?? string.Empty;
        if (ordinal < 0 || !IsOnePathComponent(stageId) || handler.Length == 0 || historicalStageAttemptId.Length == 0)
            return SiteResult<JsonObject>.Permanent("run_phase needs ordinal, contained stage_id, handler and historical_stage_attempt_id");
        var lastPhasePath = Path.Combine(cellRoot, ".fern-last-phase");
        var lastPhase = File.Exists(lastPhasePath)
            && long.TryParse(File.ReadAllText(lastPhasePath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLast)
                ? parsedLast : -1;
        if (ordinal > lastPhase + 1)
            return SiteResult<JsonObject>.Permanent($"cell attempt {attemptId} expects phase {lastPhase + 1}, not {ordinal}");
        var packageRelative = ContainedRelativePath(header["package_relative_path"]?.GetValue<string>() ?? string.Empty);
        if (!packageRelative.IsOk) return packageRelative.RefusalAs<JsonObject>();
        var sourceRoot = header["source_root"]?.GetValue<string>() ?? string.Empty;
        if (!Path.IsPathFullyQualified(sourceRoot)) return SiteResult<JsonObject>.Permanent("run_phase source_root must be absolute");
        var implementationRoot = Path.GetFullPath(Path.Combine(workRoot, "implementation")) + Path.DirectorySeparatorChar;
        var sourceFull = Path.GetFullPath(sourceRoot);
        if (!sourceFull.StartsWith(implementationRoot, StringComparison.Ordinal)
            || Path.GetFileName(sourceFull) is not { Length: 64 } sourceDigest
            || !sourceDigest.All(char.IsAsciiHexDigitLower))
        {
            return SiteResult<JsonObject>.Permanent("run_phase source_root must be one digest-named child of the Site implementation store");
        }
        var sourceMarker = Path.Combine(sourceFull, ".fern-source-archive.sha256");
        if (!File.Exists(sourceMarker) || File.ReadAllText(sourceMarker).Trim() != sourceDigest)
            return SiteResult<JsonObject>.Permanent($"run_phase source_root {sourceRoot} has no agreeing archive marker");
        // One Site session belongs to the machine.  Its worker root is only a
        // base: each frozen implementation digest gets its own resident Python
        // worker beneath it.  Two Plans can therefore share one SSH/session
        // without importing one another's source into a warm interpreter.
        var phaseWorkerRoot = Path.Combine(workerRoot, sourceDigest);
        if (header["files"] is not JsonArray files) return SiteResult<JsonObject>.Permanent("run_phase needs files");
        foreach (var item in files)
        {
            var file = item?.AsObject() ?? throw new JsonException("cell file is not an object");
            var relative = ContainedRelativePath(file["relative_path"]?.GetValue<string>() ?? string.Empty);
            if (!relative.IsOk) return relative.RefusalAs<JsonObject>();
            var digest = RequiredDigest(file, "sha256");
            if (!digest.IsOk) return digest.RefusalAs<JsonObject>();
            var stored = Path.Combine(storeRoot, digest.Value);
            if (!File.Exists(stored) || HashFile(stored) != digest.Value)
                return SiteResult<JsonObject>.Permanent($"run_phase file {relative.Value} is missing from the Site store as {digest.Value}");
            var destination = Path.Combine(cellRoot, StageAttemptSite.FromSlash(relative.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                var linked = ResidentPythonWorker.HardLinkOrCopy(stored, destination);
                if (!linked.IsOk) return linked.RefusalAs<JsonObject>();
            }
            if (HashFile(destination) != digest.Value)
                return SiteResult<JsonObject>.Permanent($"run_phase materialized file {relative.Value} changed");
        }
        var immutableSubmission = header.DeepClone().AsObject();
        immutableSubmission.Remove("request_id"); // transport correlation is not cell identity
        var submissionDigest = StageAttemptSite.Sha256Hex(Encoding.UTF8.GetBytes(immutableSubmission.ToJsonString()));
        var submissionSeal = Path.Combine(cellRoot, $".fern-phase-{ordinal}.sha256");
        if (File.Exists(submissionSeal))
        {
            if (File.ReadAllText(submissionSeal).Trim() != submissionDigest)
                return SiteResult<JsonObject>.Permanent($"cell attempt {attemptId} phase {ordinal} was already submitted with different immutable bytes");
        }
        else
        {
            File.WriteAllText(submissionSeal + ".partial", submissionDigest + "\n");
            File.Move(submissionSeal + ".partial", submissionSeal, overwrite: false);
        }
        var package = Path.Combine(cellRoot, StageAttemptSite.FromSlash(packageRelative.Value));
        var runtime = Path.Combine(cellRoot, "runtime", ordinal.ToString(CultureInfo.InvariantCulture));
        var startupEvents = new ConcurrentQueue<ResidentPythonWorker.WorkerStartupObservation>();
        using var trainingProgress = new LatestTrainingProgressWriter();
        var running = Task.Run(() => RunCellPhase(
            package, runtime, phaseWorkerRoot, storeRoot, python, timeout,
            progress: trainingProgress,
            workerStartup: startupEvents.Enqueue,
            authorityCellId: attemptId));
        var clock = Stopwatch.StartNew();
        var nextLiveness = TimeSpan.FromSeconds(5);

        void DrainStartupEvents()
        {
            var checkpoint = trainingProgress.Take();
            if (checkpoint is not null)
                phaseEvent(new JsonObject
                {
                    ["schema"] = SessionSchema,
                    ["operation"] = "training_progress",
                    ["cell_attempt_id"] = attemptId,
                    ["parent_plan_epoch"] = parentPlanEpoch,
                    ["ordinal"] = ordinal,
                    ["line"] = checkpoint,
                });
            while (startupEvents.TryDequeue(out var observed))
            {
                phaseEvent(new JsonObject
                {
                    ["schema"] = SessionSchema,
                    ["operation"] = "worker_startup",
                    ["cell_attempt_id"] = attemptId,
                    ["parent_plan_epoch"] = parentPlanEpoch,
                    ["ordinal"] = ordinal,
                    ["stage_id"] = stageId,
                    ["state"] = observed.State,
                    ["elapsed_ms"] = observed.ElapsedMs,
                    ["worker"] = new JsonObject
                    {
                        ["pid"] = observed.Identity.Pid,
                        ["process_start_ticks"] = observed.Identity.ProcessStartTicks,
                        ["started_at"] = observed.Identity.StartedAt,
                        ["interpreter"] = observed.Identity.Interpreter,
                        ["working_directory"] = observed.Identity.WorkingDirectory,
                        ["launch_id"] = observed.Identity.LaunchId,
                    },
                });
            }
        }

        while (!running.Wait(TimeSpan.FromMilliseconds(100)))
        {
            DrainStartupEvents();
            if (clock.Elapsed >= nextLiveness)
            {
                phaseEvent(new JsonObject
                {
                    ["schema"] = SessionSchema,
                    ["operation"] = "liveness",
                    ["cell_attempt_id"] = attemptId,
                    ["parent_plan_epoch"] = parentPlanEpoch,
                    ["elapsed_ms"] = (long)clock.Elapsed.TotalMilliseconds,
                });
                nextLiveness += TimeSpan.FromSeconds(5);
            }
            if (clock.Elapsed > timeout) return SiteResult<JsonObject>.Permanent($"cell {attemptId} phase {ordinal} wrote no receipt within {timeout}");
        }
        DrainStartupEvents();
        var result = running.Result;
        if (!result.IsOk) return result.RefusalAs<JsonObject>();
        var worker = ResidentPythonWorker.ReadIdentity(phaseWorkerRoot);
        if (!worker.IsOk) return worker.RefusalAs<JsonObject>();
        var receiptPath = Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.SitePhaseReceiptFile));
        var receiptJson = File.ReadAllText(receiptPath);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.StageReportFile))));  // read again below for the failure record's digest
        var reportPath = Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.StageReportFile));
        var outcome = report.RootElement.GetProperty("outcome");
        var status = outcome.GetProperty("kind").GetString() == "success" ? "completed" : "failed";
        if (status == "failed")
        {
            // A reason is a sentence; a traceback belongs in the report the record
            // points at. Every byte here is hashed into receipt_sha256 and stored.
            const int MessageBound = 2048;
            var fullMessage = outcome.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null;
            var boundedMessage = fullMessage is { Length: > MessageBound } ? fullMessage[..MessageBound] : fullMessage;
            // CHG 2026-09-11: the failure record carries the worker's own statement of
            // why (outcome.kind, code, message), which the stage runner already wrote
            // into its report. Until now only "failed" survived into the Home, and the
            // one sentence that explained a refusal ("use: unknown dataset ...") lived
            // in a worker folder nobody opened for two days. Schema v2 because the
            // record's bytes are hashed into receipt_sha256.
            receiptJson = new JsonObject
            {
                ["schema"] = "fern.site_phase_failure.v2",
                ["cell_id"] = attemptId,
                ["ordinal"] = ordinal,
                ["handler"] = handler,
                ["status"] = status,
                ["outcome"] = new JsonObject
                {
                    ["kind"] = outcome.GetProperty("kind").GetString(),
                    ["code"] = outcome.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null,
                    ["message"] = boundedMessage,
                    ["message_truncated"] = fullMessage is { Length: > MessageBound },
                },
                ["report"] = new JsonObject
                {
                    ["relative_path"] = Path.GetRelativePath(Path.Combine(workRoot, "cells"), reportPath).Replace('\\', '/'),
                    ["sha256"] = StageAttemptSite.Sha256Hex(File.ReadAllBytes(reportPath)),
                },
                ["worker"] = new JsonObject
                {
                    ["pid"] = worker.Value.Pid,
                    ["started_at"] = worker.Value.StartedAt,
                    ["interpreter"] = worker.Value.Interpreter,
                },
            }.ToJsonString();
        }
        var receiptSha = StageAttemptSite.Sha256Hex(Encoding.UTF8.GetBytes(receiptJson));
        var phaseMessage = new JsonObject
        {
            ["ordinal"] = ordinal,
            ["stage_id"] = stageId,
            ["handler"] = handler,
            ["status"] = status,
            ["receipt_sha256"] = receiptSha,
            ["receipt_json"] = receiptJson,
            ["historical_stage_attempt_id"] = historicalStageAttemptId,
            ["report"] = Path.GetRelativePath(Path.Combine(workRoot, "cells"), Path.Combine(runtime, SitePaths.StageReportFile)).Replace('\\', '/'),
            ["site_receipt"] = Path.GetRelativePath(Path.Combine(workRoot, "cells"), receiptPath).Replace('\\', '/'),
        };
        var outputs = new JsonArray();
        var dispositions = header["outputs"]?.AsArray() ?? [];
        var readings = ReadSiteReceipt(runtime);
        if (!readings.IsOk) return readings.RefusalAs<JsonObject>();
        foreach (var dispositionNode in dispositions)
        {
            if (status == "failed") break;
            var disposition = dispositionNode!.AsObject();
            if (disposition["phase"]!.GetValue<long>() != ordinal) continue;
            var port = disposition["port"]!.GetValue<string>();
            var storage = disposition["storage_disposition"]!.GetValue<string>();
            if (!IsOnePathComponent(port) || storage is not ("home" or "site"))
                return SiteResult<JsonObject>.Permanent($"output {ordinal}/{port} needs storage_disposition home or site");
            if (!readings.Value.TryGetValue(port, out var reading))
                return SiteResult<JsonObject>.Permanent($"output disposition names absent phase {ordinal} port {port}");
            outputs.Add(new JsonObject
            {
                ["phase"] = ordinal,
                ["port"] = port,
                ["storage_disposition"] = storage,
                ["relative_path"] = Path.GetRelativePath(Path.Combine(workRoot, "cells"), Path.Combine(runtime, StageAttemptSite.FromSlash(reading.RelativePath))).Replace('\\', '/'),
                ["byte_count"] = reading.ByteCount,
                ["sha256"] = reading.ByteDigest["sha256:".Length..],
            });
        }
        var eventMessage = phaseMessage.DeepClone().AsObject();
        eventMessage["schema"] = SessionSchema;
        eventMessage["operation"] = "phase_event";
        eventMessage["cell_attempt_id"] = attemptId;
        eventMessage["parent_plan_epoch"] = parentPlanEpoch;
        eventMessage["outputs"] = outputs.DeepClone();
        phaseEvent(eventMessage);
        if (ordinal > lastPhase)
        {
            File.WriteAllText(lastPhasePath + ".partial", ordinal.ToString(CultureInfo.InvariantCulture) + "\n");
            File.Move(lastPhasePath + ".partial", lastPhasePath, overwrite: true);
        }
        return SiteResult<JsonObject>.Ok(new JsonObject
        {
            ["schema"] = SessionSchema,
            ["operation"] = "phase_complete",
            ["cell_attempt_id"] = attemptId,
            ["parent_plan_epoch"] = parentPlanEpoch,
            ["worker_pid"] = worker.Value.Pid,
            ["phase"] = phaseMessage,
            ["outputs"] = outputs,
        });
    }
    /// <summary>The science-wired facts of a phase; nothing here is a machine path.</summary>
    public sealed record PhaseInstructions(
        string CellId,
        long Ordinal,
        string Handler,
        IReadOnlyDictionary<string, (long Phase, string Output)> InputsFromPhases,
        // Space-F: external inputs the Site holds in its store, by digest.
        IReadOnlyDictionary<string, (string Sha256Hex, string FileName)> InputsFromStore);

    public static SiteResult<PhaseInstructions> ReadPhaseInstructions(string runtime)
    {
        var bytes = StageAttemptSite.ReadRegularFile(Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.PhaseFile)));
        if (!bytes.IsOk)
        {
            return SiteResult<PhaseInstructions>.Permanent("phase file: " + bytes.Failure.Detail);
        }
        try
        {
            using var document = JsonDocument.Parse(bytes.Value);
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != SitePaths.PhaseSchema)
            {
                return SiteResult<PhaseInstructions>.Permanent($"phase file schema must be {SitePaths.PhaseSchema}");
            }
            var cellId = root.GetProperty("cell_id").GetString() ?? string.Empty;
            var ordinal = root.GetProperty("ordinal").GetInt64();
            var handler = root.GetProperty("handler").GetString() ?? string.Empty;
            if (cellId.Length == 0 || cellId.Contains('/') || handler.Length == 0 || ordinal < 0)
            {
                return SiteResult<PhaseInstructions>.Permanent("phase file needs a cell_id without '/', a handler and a non-negative ordinal");
            }
            var links = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
            if (root.TryGetProperty("inputs_from_phases", out var linksElement))
            {
                foreach (var property in linksElement.EnumerateObject())
                {
                    if (!IsOnePathComponent(property.Name))
                    {
                        return SiteResult<PhaseInstructions>.Permanent(
                            $"phase file: input port {property.Name} must be one path component");
                    }
                    var phase = property.Value.GetProperty("phase").GetInt64();
                    var output = property.Value.GetProperty("output").GetString() ?? string.Empty;
                    if (phase < 0 || phase >= ordinal || output.Length == 0)
                    {
                        return SiteResult<PhaseInstructions>.Permanent(
                            $"phase file: input port {property.Name} must come from an earlier phase's named output");
                    }
                    links[property.Name] = (phase, output);
                }
            }
            var fromStore = new SortedDictionary<string, (string, string)>(StringComparer.Ordinal);
            if (root.TryGetProperty("inputs_from_store", out var storeElement))
            {
                foreach (var property in storeElement.EnumerateObject())
                {
                    if (!IsOnePathComponent(property.Name))
                    {
                        return SiteResult<PhaseInstructions>.Permanent(
                            $"phase file: input port {property.Name} must be one path component");
                    }
                    var sha = property.Value.GetProperty("sha256").GetString() ?? string.Empty;
                    var name = property.Value.GetProperty("file_name").GetString() ?? string.Empty;
                    if (sha.Length != 64 || sha.Any(c => !char.IsAsciiHexDigitLower(c)) || name.Length == 0 || name.Contains('/'))
                    {
                        return SiteResult<PhaseInstructions>.Permanent(
                            $"phase file: input port {property.Name} from the store needs a lowercase sha256 and a bare file name");
                    }
                    fromStore[property.Name] = (sha, name);
                }
            }
            return SiteResult<PhaseInstructions>.Ok(new PhaseInstructions(cellId, ordinal, handler, links, fromStore));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return SiteResult<PhaseInstructions>.Permanent("phase file is not readable: " + error.Message);
        }
    }

    static bool IsOnePathComponent(string value) =>
        value.Length > 0
        && value is not "." and not ".."
        && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    /// <summary>
    /// Run one phase. <paramref name="runtime"/> is the phase workspace; its
    /// parent directory is the cell workspace the worker's cell request names,
    /// so every phase of one cell sits beside the others and the worker can
    /// link an earlier phase's output in by its receipt.
    /// </summary>
    public static SiteResult<StageExecutionResult> RunCellPhase(
        string package,
        string runtime,
        string workerRoot,
        string storeRoot,
        string python,
        TimeSpan timeout,
        TextWriter? progress,
        Action<ResidentPythonWorker.WorkerStartupObservation>? workerStartup,
        string? authorityCellId)
    {
        var runtimeExisted = StageAttemptSite.PathExists(runtime);
        var verified = StageAttemptSite.MaterializeStagePackage(package, runtime);
        if (!verified.IsOk)
        {
            return verified.RefusalAs<StageExecutionResult>();
        }
        if (timeout <= TimeSpan.Zero)
        {
            return SiteResult<StageExecutionResult>.Permanent("phase timeout must be positive");
        }
        var instructions = verified.Value.Instructions;
        var phase = ReadPhaseInstructions(verified.Value.Root);
        if (!phase.IsOk)
        {
            return phase.RefusalAs<StageExecutionResult>();
        }
        var effectivePhase = authorityCellId is null ? phase.Value : phase.Value with { CellId = authorityCellId };
        if (string.IsNullOrWhiteSpace(python))
        {
            return SiteResult<StageExecutionResult>.Permanent("a cell phase requires an explicit interpreter");
        }
        var sourceRoot = instructions.ImplementationSourceRoot;
        var installedSource = StageAttemptSite.VerifyInstalledSource(sourceRoot, instructions.ImplementationSourceFiles);
        if (!installedSource.IsOk)
        {
            return installedSource.RefusalAs<StageExecutionResult>();
        }
        var workerReceiptPath = ResidentPythonWorker.PhaseReceiptPath(
            workerRoot, effectivePhase.CellId, phase.Value.Ordinal, phase.Value.Handler);

        // The worker already committed this phase before the Site-level reply
        // was lost in transit. Its durable receipt supplies the phase timing;
        // the sealed runner report is the answer and science is not run again.
        if (runtimeExisted
            && File.Exists(Path.Combine(verified.Value.Root, StageAttemptSite.FromSlash(SitePaths.StageReportFile))))
        {
            var again = StageAttemptSite.ReadStageExecutionResult(verified.Value.Root, instructions.AttemptId);
            return again.IsOk
                ? WriteSiteReceipt(verified.Value.Root, effectivePhase, instructions.AttemptId, null, workerReceiptPath, again.Value)
                : again;
        }

        // Space-F: the Site's store holds the external inputs by digest; each
        // is linked into inputs/<port>/ after its bytes are checked, before
        // anything is submitted. A missing or changed file refuses by port.
        foreach (var (port, (sha, fileName)) in phase.Value.InputsFromStore)
        {
            var stored = Path.Combine(storeRoot, sha);
            var bytes = StageAttemptSite.ReadRegularFile(stored);
            if (!bytes.IsOk)
            {
                return SiteResult<StageExecutionResult>.Permanent($"input port {port}: the store holds no {sha}: {bytes.Failure.Detail}");
            }
            if (StageAttemptSite.Sha256Hex(bytes.Value) != sha)
            {
                return SiteResult<StageExecutionResult>.Permanent($"input port {port}: the store's {sha} does not hash to its name");
            }
            var destination = Path.Combine(verified.Value.Root, "inputs", port, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            var linked = ResidentPythonWorker.HardLinkOrCopy(stored, destination);
            if (!linked.IsOk)
            {
                return linked.RefusalAs<StageExecutionResult>();
            }
        }

        // Worker admission is its own process boundary. It must not inherit a
        // one-hour training deadline: a dead interpreter or import hang is a
        // Site-start failure, not a long-running scientific phase.
        var worker = ResidentPythonWorker.EnsureRunning(
            workerRoot,
            python,
            sourceRoot,
            ResidentPythonWorker.ImportDeadline,
            workerStartup);
        if (!worker.IsOk)
        {
            return worker.RefusalAs<StageExecutionResult>();
        }
        var runtimeFull = Path.GetFullPath(verified.Value.Root);
        var cellWorkspace = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(runtimeFull))
            ?? throw new InvalidOperationException("phase runtime has no parent directory");
        var requestRelative = Path.GetFileName(Path.TrimEndingDirectorySeparator(runtimeFull)) + "/" + SitePaths.StageRequestFile;
        var links = new JsonObject();
        foreach (var (port, (fromPhase, producedPort)) in phase.Value.InputsFromPhases)
        {
            links[port] = new JsonObject { ["phase"] = fromPhase, ["output"] = producedPort };
        }
        var cellRequest = new JsonObject
        {
            ["schema"] = "fern.cell_request.v1",
            ["cell_id"] = effectivePhase.CellId,
            ["workspace"] = cellWorkspace,
            ["phases"] = new JsonArray(new JsonObject
            {
                ["ordinal"] = phase.Value.Ordinal,
                ["handler"] = phase.Value.Handler,
                ["request"] = requestRelative,
                ["inputs_from_phases"] = links,
            }),
        };
        // A same-attempt replay must never erase this worker's durable fact.
        // The worker itself recognizes a successful receipt and does not run
        // the phase a second time.
        var receiptPath = workerReceiptPath;
        var log = new ResidentPythonWorker.GrowingFileReader(
            ResidentPythonWorker.CellLogPath(workerRoot, effectivePhase.CellId), fromEnd: true);
        var submitted = ResidentPythonWorker.Submit(workerRoot, cellRequest.ToJsonString() + "\n");
        if (!submitted.IsOk)
        {
            return submitted.RefusalAs<StageExecutionResult>();
        }

        var output = new StageCommandOutput { Progress = progress };
        var started = Stopwatch.StartNew();
        var pid = worker.Value.Pid;
        // Native notification is the fast path; the bounded wait is the
        // reconciliation fallback. FileSystemWatcher can coalesce/drop events
        // (and network filesystems may deliver none), so existence remains the
        // fact and neither channel is trusted alone.
        var receiptDirectory = Path.GetDirectoryName(receiptPath)
            ?? throw new InvalidOperationException("resident worker receipt has no parent directory");
        Directory.CreateDirectory(receiptDirectory);
        using var receiptChanged = new AutoResetEvent(false);
        using var receiptWatcher = new FileSystemWatcher(receiptDirectory, Path.GetFileName(receiptPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler signalReceipt = (_, _) => receiptChanged.Set();
        RenamedEventHandler signalRenamedReceipt = (_, _) => receiptChanged.Set();
        receiptWatcher.Created += signalReceipt;
        receiptWatcher.Changed += signalReceipt;
        receiptWatcher.Renamed += signalRenamedReceipt;
        while (!File.Exists(receiptPath))
        {
            _ = receiptChanged.WaitOne(TimeSpan.FromMilliseconds(250));
            var fresh = log.ReadNew();
            if (fresh.Length > 0)
            {
                output.Write(fresh);
            }
            if (started.Elapsed > timeout)
            {
                var killed = ResidentPythonWorker.Kill(worker.Value);
                output.Flush();
                output.Tail.WriteBeside(verified.Value.Root);
                if (!killed.IsOk)
                {
                    return SiteResult<StageExecutionResult>.Refused(killed.Failure with
                    {
                        Detail = $"cell phase {phase.Value.Ordinal} ({phase.Value.Handler}) exceeded {timeout}; "
                            + "refused to kill a worker whose lifetime identity changed: "
                            + killed.Failure.Detail
                            + output.Tail.Detail,
                    });
                }
                return SiteResult<StageExecutionResult>.Refused(SiteFailure.Unclassified(
                    $"cell phase {phase.Value.Ordinal} ({phase.Value.Handler}) exceeded {timeout}; the worker was killed" + output.Tail.Detail));
            }
            if (!ResidentPythonWorker.IsAlive(pid))
            {
                output.Flush();
                output.Tail.WriteBeside(verified.Value.Root);
                return SiteResult<StageExecutionResult>.Refused(SiteFailure.Unclassified(
                    $"the resident worker (pid {pid}) died before the receipt of phase {phase.Value.Ordinal}" + output.Tail.Detail));
            }
        }
        var remaining = log.ReadNew();
        if (remaining.Length > 0)
        {
            output.Write(remaining);
        }
        output.Flush();
        output.Tail.WriteBeside(verified.Value.Root);

        var result = StageAttemptSite.ReadStageExecutionResult(verified.Value.Root, instructions.AttemptId);
        if (!result.IsOk)
        {
            return result;
        }
        return WriteSiteReceipt(verified.Value.Root, effectivePhase, instructions.AttemptId, worker.Value, receiptPath, result.Value);
    }

    /// <summary>
    /// The same receipt for a phase the Home's own machine ran (the local
    /// route): the Home is then its own Site, and the monitor reads one file
    /// shape for both routes.
    /// </summary>
    public static SiteResult<StageExecutionResult> RecordSiteReceipt(
        string runtime,
        string cellId,
        long ordinal,
        string handler,
        string attemptId,
        string? workerReceiptPath,
        ResidentPythonWorker.WorkerIdentity? worker)
    {
        var result = StageAttemptSite.ReadStageExecutionResult(runtime, attemptId);
        if (!result.IsOk)
        {
            return result;
        }
        var phase = new PhaseInstructions(
            cellId, ordinal, handler, new Dictionary<string, (long, string)>(), new Dictionary<string, (string, string)>());
        return WriteSiteReceipt(runtime, phase, attemptId, worker, workerReceiptPath, result.Value);
    }

    /// <summary>
    /// The Site's own reading of the sealed outputs the runner report names:
    /// byte count and byte digest per port, reopened here after the worker
    /// finished. Written durably beside the report; returned instead of the
    /// bytes.
    /// </summary>
    static SiteResult<StageExecutionResult> WriteSiteReceipt(
        string runtime,
        PhaseInstructions phase,
        string attemptId,
        ResidentPythonWorker.WorkerIdentity? worker,
        string? workerReceiptPath,
        StageExecutionResult result)
    {
        // What the phase cost, from the worker's own receipt: seconds between
        // its start and its report, the compute a person asks about first.
        JsonObject? timing = null;
        if (workerReceiptPath is not null && File.Exists(workerReceiptPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(workerReceiptPath));
                var root = document.RootElement;
                timing = new JsonObject
                {
                    ["seconds"] = root.GetProperty("seconds").GetDouble(),
                    ["started_at"] = root.GetProperty("started_at").GetString(),
                    ["finished_at"] = root.GetProperty("finished_at").GetString(),
                };
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                timing = null; // a receipt this reader cannot parse names nothing rather than a wrong number
            }
        }
        var reportBytes = StageAttemptSite.ReadRegularFile(Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.StageReportFile)));
        if (!reportBytes.IsOk)
        {
            return reportBytes.RefusalAs<StageExecutionResult>();
        }
        var outputs = new JsonObject();
        try
        {
            using var document = JsonDocument.Parse(reportBytes.Value);
            var outcome = document.RootElement.GetProperty("outcome");
            if (outcome.GetProperty("kind").GetString() == "success"
                && outcome.TryGetProperty("output_paths", out var paths))
            {
                foreach (var property in paths.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var relative = property.Value.GetString() ?? string.Empty;
                    if (!relative.StartsWith("outputs/", StringComparison.Ordinal)
                        || relative.Split('/').Any(part => part is "" or "." or ".."))
                    {
                        return SiteResult<StageExecutionResult>.Permanent($"runner output {property.Name} path is not under outputs/");
                    }
                    var bytes = StageAttemptSite.ReadRegularFile(Path.Combine(runtime, StageAttemptSite.FromSlash(relative)));
                    if (!bytes.IsOk)
                    {
                        return SiteResult<StageExecutionResult>.Permanent($"runner output {property.Name}: {bytes.Failure.Detail}");
                    }
                    outputs[property.Name] = new JsonObject
                    {
                        ["relative_path"] = relative,
                        ["byte_count"] = bytes.Value.LongLength,
                        ["byte_digest"] = "sha256:" + StageAttemptSite.Sha256Hex(bytes.Value),
                    };
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return SiteResult<StageExecutionResult>.Permanent("runner report is not readable for the Site receipt: " + error.Message);
        }
        var receipt = new JsonObject
        {
            ["schema"] = SitePaths.SitePhaseReceiptSchema,
            ["stage_attempt_id"] = attemptId,
            ["cell_id"] = phase.CellId,
            ["ordinal"] = phase.Ordinal,
            ["handler"] = phase.Handler,
            ["runner_report_sha256"] = result.RunnerReportContentDigest,
            // Small control-plane evidence, not an output body. Home needs the
            // exact report bytes to reconstruct ReportedArtifact identities
            // before it can mint the next phase while tensors remain Site.
            ["runner_report_json"] = Encoding.UTF8.GetString(reportBytes.Value),
            ["worker"] = worker is null
                ? null
                : new JsonObject { ["pid"] = worker.Pid, ["started_at"] = worker.StartedAt, ["interpreter"] = worker.Interpreter },
            ["phase"] = timing,
            ["outputs"] = outputs,
        };
        var path = Path.Combine(runtime, StageAttemptSite.FromSlash(SitePaths.SitePhaseReceiptFile));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        File.WriteAllBytes(partial, Encoding.UTF8.GetBytes(receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"));
        File.Move(partial, path, overwrite: true);
        return SiteResult<StageExecutionResult>.Ok(result);
    }

    /// <summary>The Site receipt's per-port readings, for the Home.</summary>
    public sealed record SiteOutputReading(string RelativePath, long ByteCount, string ByteDigest);

    public static SiteResult<IReadOnlyDictionary<string, SiteOutputReading>> ReadSiteReceipt(string homeWorkspace)
    {
        var bytes = StageAttemptSite.ReadRegularFile(Path.Combine(homeWorkspace, StageAttemptSite.FromSlash(SitePaths.SitePhaseReceiptFile)));
        if (!bytes.IsOk)
        {
            return bytes.RefusalAs<IReadOnlyDictionary<string, SiteOutputReading>>();
        }
        try
        {
            using var document = JsonDocument.Parse(bytes.Value);
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != SitePaths.SitePhaseReceiptSchema)
            {
                return SiteResult<IReadOnlyDictionary<string, SiteOutputReading>>.Permanent(
                    $"Site receipt schema must be {SitePaths.SitePhaseReceiptSchema}");
            }
            var reportJson = root.GetProperty("runner_report_json").GetString() ?? string.Empty;
            var reportedDigest = root.GetProperty("runner_report_sha256").GetString() ?? string.Empty;
            var observedDigest = "sha256:" + StageAttemptSite.Sha256Hex(Encoding.UTF8.GetBytes(reportJson));
            if (reportedDigest != observedDigest)
            {
                return SiteResult<IReadOnlyDictionary<string, SiteOutputReading>>.Permanent(
                    $"Site receipt runner report hashes to {observedDigest}, not {reportedDigest}");
            }
            var readings = new SortedDictionary<string, SiteOutputReading>(StringComparer.Ordinal);
            foreach (var property in root.GetProperty("outputs").EnumerateObject())
            {
                readings[property.Name] = new SiteOutputReading(
                    property.Value.GetProperty("relative_path").GetString() ?? string.Empty,
                    property.Value.GetProperty("byte_count").GetInt64(),
                    property.Value.GetProperty("byte_digest").GetString() ?? string.Empty);
            }
            return SiteResult<IReadOnlyDictionary<string, SiteOutputReading>>.Ok(readings);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return SiteResult<IReadOnlyDictionary<string, SiteOutputReading>>.Permanent("Site receipt is not readable: " + error.Message);
        }
    }
}
