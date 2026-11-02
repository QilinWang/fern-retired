using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fern.Scheduler;

/// <summary>Bind an explicitly selected, stopped producer's complete epoch to a new native attempt.</summary>
public static class StudyCellRecovery
{
    public sealed record Invocation(string[] Arguments, string ResultPath, string ProvenanceJson);

    public static Invocation Prepare(StudyOrchestrator.ClaimedCell cell, string root, string previousPrefix,
        byte[] currentRequest, string implementationDigest)
    {
        var prefix = LocalCellWorker.ArtifactPrefix(cell);
        if (!Regex.IsMatch(previousPrefix, @"\Astudy-[0-9]+\.cell-[0-9]+\.attempt-[0-9]+\.epoch-[0-9]+\.\z")
            || !previousPrefix.StartsWith($"study-{cell.StudyId}.cell-{cell.CellId}.", StringComparison.Ordinal)
            || previousPrefix == prefix)
            throw new InvalidOperationException("recovery must name a different attempt of this same Study and cell");
        var previous = Path.Combine(root, previousPrefix);
        var destination = Path.Combine(root, prefix);
        if (File.Exists(previous + "results.json"))
            throw new InvalidOperationException("the selected producer already completed; collect its result instead of training it again");

        var oldRequest = JsonNode.Parse(File.ReadAllText(previous + "inputs.study_cell_request.json"))!;
        var newRequest = JsonNode.Parse(currentRequest)!;
        if (!JsonNode.DeepEquals(oldRequest, newRequest))
            throw new InvalidOperationException("recovery cell request differs: configuration, seed, data or compiled card changed");
        foreach (var input in newRequest["dataset_inputs"]!.AsObject())
        {
            var declared = input.Value!;
            var path = declared["path"]!.GetValue<string>();
            if (!Path.IsPathRooted(path)) path = Path.Combine(cell.SourceRoot!, path);
            if (!File.Exists(path) || Hash(path) != declared["sha256"]!.GetValue<string>())
                throw new InvalidOperationException("recovery dataset input changed: " + input.Key);
        }
        var envelope = JsonNode.Parse(File.ReadAllText(previous + "request.stage_runner_request.v1.json"))!;
        if (envelope["implementation_digest"]!.GetValue<string>() != implementationDigest
            || envelope["artifact_prefix"]!.GetValue<string>() != previousPrefix)
            throw new InvalidOperationException("recovery source implementation or artifact identity differs");
        var heartbeat = JsonNode.Parse(File.ReadAllText(previous + "heartbeat.json"))!;
        if (heartbeat["supervisor_host"]!.GetValue<string>() != Environment.MachineName)
            throw new InvalidOperationException("recovery requires the original producer host; cross-host adoption is not implemented");
        var pid = heartbeat["supervisor_pid"]!.GetValue<int>();
        if (pid <= 0) throw new InvalidOperationException("recovery heartbeat has no valid producer PID");
        try
        {
            using var producer = Process.GetProcessById(pid);
            if (!producer.HasExited) throw new InvalidOperationException("the original producer is still alive; refuse concurrent recovery");
        }
        catch (ArgumentException) { /* The recorded producer no longer exists. */ }

        var frozenPath = previous + "compiled-study.json";
        var frozen = JsonNode.Parse(File.ReadAllText(frozenPath))!;
        if (!JsonNode.DeepEquals(frozen["resolution"]!["resolved"]!["manifest"], newRequest["card"]))
            throw new InvalidOperationException("recovery frozen Study card differs from the current request");
        var frozenCell = frozen["cells"]!.AsArray().SingleOrDefault(value =>
            value!["cell"]!.GetValue<string>() == cell.CellName)
            ?? throw new InvalidOperationException("recovery frozen Study has no matching cell");
        // CHG 2026-09-27 (config-hash-leaves, slice 1): the configuration is compared whole; the
        // hash beside it decided nothing the comparison did not.
        if (!JsonNode.DeepEquals(frozenCell["config"], newRequest["config"])
            || frozenCell["coordinates"]!["seed"]!.GetValue<long>() != newRequest["seed"]!.GetValue<long>())
            throw new InvalidOperationException("recovery frozen cell configuration or seed differs from the registered request");
        var recoverySuffix = cell.CellName + ".training-recovery.pt";
        var modelSuffix = cell.CellName + ".model.safetensors";
        if (!File.Exists(previous + recoverySuffix))
            throw new InvalidOperationException("no complete-epoch recovery exists; refuse an epoch-zero restart");
        var files = new List<string> { "compiled-study.json", recoverySuffix };
        if (File.Exists(previous + modelSuffix)) files.Add(modelSuffix);
        var cardPath = destination + "recovery-card.json";
        if (File.Exists(cardPath) || files.Any(suffix => File.Exists(destination + suffix)))
            throw new InvalidOperationException("the destination already contains recovery artifacts");
        var evidence = new JsonArray();
        foreach (var suffix in files)
        {
            var from = previous + suffix;
            var before = Hash(from);
            File.Copy(from, destination + suffix, overwrite: false);
            if (Hash(from) != before || Hash(destination + suffix) != before)
                throw new InvalidOperationException("recovery bytes changed during copy: " + from);
            evidence.Add(new JsonObject { ["file"] = suffix, ["sha256"] = before });
        }
        File.WriteAllText(cardPath, newRequest["card"]!.ToJsonString());
        var provenance = new JsonObject
        {
            ["previous_artifact_prefix"] = previousPrefix, ["artifact_prefix"] = prefix,
            ["implementation_digest"] = implementationDigest, ["files"] = evidence,
            ["method"] = "native_run_cell_complete_epoch_recovery",
        }.ToJsonString();
        File.WriteAllText(destination + "recovery.json", provenance);
        return new Invocation(
            ["-m", "fern_analysis", "study", "run-cell", cardPath, "--cell", cell.CellName,
             "--out", root, "--device", "cuda", "--resume", "--artifact-prefix", prefix,
             "--compiled-study", destination + "compiled-study.json"],
            destination + "results.json", provenance);
    }

    public static string ReadResult(Invocation invocation, byte[] currentRequest)
    {
        if (!File.Exists(invocation.ResultPath) || new FileInfo(invocation.ResultPath).Length > LocalCellWorker.MaximumResultBytes)
            throw new InvalidOperationException("recovery produced no bounded result document");
        var bytes = File.ReadAllText(invocation.ResultPath);
        var result = JsonNode.Parse(bytes)!;
        var request = JsonNode.Parse(currentRequest)!;
        if (result["schema"]!.GetValue<string>() != StudyCollect.UnitResultsSchema
            || result["cell"]!.GetValue<string>() != request["cell"]!.GetValue<string>()
            || result["seed"]!.GetValue<long>() != request["seed"]!.GetValue<long>()
            || result["device"]!.GetValue<string>() != request["device"]!.GetValue<string>()
            || !JsonNode.DeepEquals(result["config"], request["config"]))
            throw new InvalidOperationException("recovery result configuration, cell or seed differs from its request");
        return bytes;
    }

    static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
