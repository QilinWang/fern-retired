using System.Security.Cryptography;
using System.Text;

namespace Fern.Scheduler;

/// <summary>
/// What one file in a completed Attempt directory is for.
///
/// The three acceptance roles prove the run has a metric, its declared
/// invocation, and the machine environment that actually produced it. Every
/// other role is descriptive: it says what a file is so a later reader can
/// decide whether it still needs it, and it is part of the signed manifest
/// either way.
/// </summary>
public enum ArtifactFileRole
{
    MetricsReceipt,
    Invocation,
    ExecutionEnvironment,
    Checkpoint,
    PointForecast,
    RawEvaluationTensors,
    SequenceTensors,
    ForecastCalibration,
    QuantileDiagnostics,
    Diagnostics,
    Other,
}

/// <summary>One content-addressed stable file captured at the moment a run is accepted.</summary>
public sealed record ArtifactFileReceipt(string RelativePath, ArtifactFileRole Role, long ByteCount, string Sha256);

/// <summary>The complete stable evidence set one Attempt leaves in its artifact directory.</summary>
public sealed record AttemptArtifactManifestV1(
    IReadOnlyList<ArtifactFileReceipt> Files,
    long TotalBytes,
    string ReceiptDigest);

/// <summary>
/// Read and sign every stable artifact file for one completed run.
///
/// A heartbeat is deliberately excluded: it is a liveness signal that may move
/// after a worker's useful output is complete, so treating it as evidence would
/// manufacture false stale alarms. Every other regular file is signed with its
/// path relative to the Attempt root. Nested typed-output directories are
/// allowed; symlinks and other non-regular entries are refused at every depth.
///
/// Ported 2026-08-28 from fern_run/src/results.rs (`capture_artifact_manifest`,
/// `capture_artifact_files`, `ArtifactFileRole`) at HEAD. The digest rule is
/// the reason this is a port and not a rewrite: 8,994 sealed snapshots in the
/// campaign Registry carry receipt digests this function must still reproduce,
/// so every part of the fold — the field order, the 0x1F separator after each
/// field including the last, and the ordinal sort by relative path — is load
/// bearing.
/// </summary>
public static class AttemptArtifactManifest
{
    public const string MetricsIpcFile = "metrics.ipc";
    public const string InvocationFile = "invocation.json";
    public const string ExecutionEnvironmentFile = "execution_environment.json";
    public const string ModelSafetensorsFile = "model.safetensors";
    public const string PointForecastFile = "point_forecast.safetensors";
    public const string RawTensorsFile = "raw_tensors.safetensors";
    public const string SequenceTensorsFile = "sequence_tensors.safetensors";

    /// <summary>A liveness signal, not evidence: it may move after the useful output is complete.</summary>
    static readonly string[] VolatileArtifactFiles = ["heartbeat.json"];

    /// <summary>Sign one completed Attempt directory.</summary>
    public static AttemptArtifactManifestV1 Capture(string directory)
    {
        var files = new List<ArtifactFileReceipt>();
        CaptureArtifactFiles(directory, directory, files);
        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        foreach (var role in new[]
                 {
                     ArtifactFileRole.MetricsReceipt,
                     ArtifactFileRole.Invocation,
                     ArtifactFileRole.ExecutionEnvironment,
                 })
        {
            if (!files.Any(file => file.Role == role && file.ByteCount > 0))
            {
                throw new AttemptArtifactRefusal(
                    $"completed artifact directory {directory} lacks a non-empty {StoredName(role)} receipt");
            }
        }

        var totalBytes = files.Sum(file => file.ByteCount);
        var receipt = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            foreach (var part in new[]
                     {
                         file.RelativePath,
                         StoredName(file.Role),
                         file.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         file.Sha256,
                     })
            {
                receipt.AppendData(Encoding.UTF8.GetBytes(part));
                receipt.AppendData([0x1f]);
            }
        }
        return new AttemptArtifactManifestV1(
            files, totalBytes, Convert.ToHexStringLower(receipt.GetHashAndReset()));
    }

    static void CaptureArtifactFiles(string root, string directory, List<ArtifactFileReceipt> files)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var fileName = Path.GetFileName(path);
            if (VolatileArtifactFiles.Contains(fileName, StringComparer.Ordinal))
            {
                continue;
            }
            var isDirectory = Directory.Exists(path);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                throw new AttemptArtifactRefusal(
                    $"artifact directory {directory} contains non-regular entry \"{fileName}\"");
            }
            if (isDirectory)
            {
                CaptureArtifactFiles(root, path, files);
                continue;
            }
            if (info is not FileInfo file || !file.Exists)
            {
                throw new AttemptArtifactRefusal(
                    $"artifact directory {directory} contains non-regular entry \"{fileName}\"");
            }
            files.Add(new ArtifactFileReceipt(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                ForFileName(fileName),
                file.Length,
                Sha256File(path)));
        }
    }

    public static ArtifactFileRole ForFileName(string name) => name switch
    {
        MetricsIpcFile or "compact_metrics.arrow" => ArtifactFileRole.MetricsReceipt,
        InvocationFile => ArtifactFileRole.Invocation,
        ExecutionEnvironmentFile => ArtifactFileRole.ExecutionEnvironment,
        ModelSafetensorsFile => ArtifactFileRole.Checkpoint,
        PointForecastFile => ArtifactFileRole.PointForecast,
        RawTensorsFile => ArtifactFileRole.RawEvaluationTensors,
        SequenceTensorsFile => ArtifactFileRole.SequenceTensors,
        "forecast_calibration.json" => ArtifactFileRole.ForecastCalibration,
        "quantile_diag.json" => ArtifactFileRole.QuantileDiagnostics,
        "diagnostics.json" or "calibration.json" => ArtifactFileRole.Diagnostics,
        _ => ArtifactFileRole.Other,
    };

    /// <summary>The role's stored spelling. It is inside the digest, so it cannot be renamed.</summary>
    public static string StoredName(ArtifactFileRole role) => role switch
    {
        ArtifactFileRole.MetricsReceipt => "metrics_receipt",
        ArtifactFileRole.Invocation => "invocation",
        ArtifactFileRole.ExecutionEnvironment => "execution_environment",
        ArtifactFileRole.Checkpoint => "checkpoint",
        ArtifactFileRole.PointForecast => "point_forecast",
        ArtifactFileRole.RawEvaluationTensors => "raw_evaluation_tensors",
        ArtifactFileRole.SequenceTensors => "sequence_tensors",
        ArtifactFileRole.ForecastCalibration => "forecast_calibration",
        ArtifactFileRole.QuantileDiagnostics => "quantile_diagnostics",
        ArtifactFileRole.Diagnostics => "diagnostics",
        _ => "other",
    };

    /// <summary>
    /// The acceptance requirement only. It is not a retention declaration and
    /// not automatic-GC permission.
    /// </summary>
    public static bool RequiredForAcceptance(ArtifactFileRole role) =>
        role is ArtifactFileRole.MetricsReceipt
            or ArtifactFileRole.Invocation
            or ArtifactFileRole.ExecutionEnvironment;

    static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>Raised when a completed Attempt directory cannot be signed as it stands.</summary>
public sealed class AttemptArtifactRefusal(string message) : Exception(message);
