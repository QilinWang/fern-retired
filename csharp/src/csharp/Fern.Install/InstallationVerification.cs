using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Install;

public sealed record VerifiedInstallation(
    /// Registry Home governed by this exact immutable binary set.
    string RegistryPath,
    string WorkRoot,
    /// The content-addressed Stage source store the supervisor materializes from.
    string DeployRoot,
    /// The directory holding &lt;execution-profile-id&gt;.json.
    string? ProfilesRoot,
    /// The maintained helper an SSH Worker Site is reached through.
    string? SshHelper,
    string SiteBinarySha256,
    string SiteBinaryPath,
    string ExperimentBinarySha256,
    string ExperimentBinaryPath,
    string RunBinarySha256,
    string RunBinaryPath,
    string CompileBinarySha256,
    string CompileBinaryPath,
    string BinarySetId);

public abstract record InstallationVerificationOutcome
{
    public sealed record Accepted(VerifiedInstallation Installation) : InstallationVerificationOutcome;

    public sealed record Refused(string Detail) : InstallationVerificationOutcome;
}

/// <summary>
/// The executables one installation receipt names, read without binding the
/// receipt to a Registry Home.
///
/// A worker machine has the receipt and the binaries but not necessarily the
/// Home they were installed against -- the Home is on the machine that placed
/// the work. So the members are readable on their own, and the Home-binding
/// checks stay in <see cref="InstallationVerification.VerifyMaterialized"/>
/// where they belong.
/// </summary>

public sealed record InstalledControlInspection(
    string Schema,
    string State,
    string? Reason,
    string? ReceiptPath,
    string? BinarySetId);

/// <summary>
/// Resolves the newest installed control set governing one Registry Home.
/// This is an operator-side discovery read, not the scheduler election gate:
/// absence is projected as typed Unknown and can never mean compatibility.
/// </summary>
public static class InstalledControlInspectionCli
{
    public const string Verb = "inspect-installed-control";
    const string ProjectionSchema = "fern.installed_control_inspection.v1";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        string? registry = null, deploymentRoot = null;
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag is not ("--registry" or "--deployment-root"))
            {
                stderr.WriteLine($"unknown flag {flag}");
                return 2;
            }
            if (i + 1 >= args.Count)
            {
                stderr.WriteLine($"missing value for {flag}");
                return 2;
            }
            if (flag == "--registry") registry = args[++i];
            else deploymentRoot = args[++i];
        }
        if (registry is null || deploymentRoot is null)
        {
            stderr.WriteLine($"{Verb} requires --registry --deployment-root");
            return 2;
        }

        stdout.WriteLine(JsonReadPolicy.Write(Inspect(registry, deploymentRoot)));
        return 0;
    }

    public static InstalledControlInspection Inspect(string registryPath, string deploymentRoot)
    {
        try
        {
            var registry = HomeIdentity(registryPath, "Registry Home database");
            // CHG 2026-09-18: read where the installer writes. `scheduler-services`
            // is where the retired resident scheduler filed its receipts (retired
            // 2026-09-11 with the Plan layer); this inspector kept reading it, so a
            // freshly installed and selected set answered "unknown" while 82 receipts
            // of a retired shape sat in the old folder.
            var receiptsRoot = Path.Combine(Path.GetFullPath(deploymentRoot), "control-plane-installations");
            if (!Directory.Exists(receiptsRoot))
            {
                return Unknown($"no installed control-plane receipts under {receiptsRoot}: directory does not exist");
            }

            ControlPlaneInstallationReceipt? newest = null;
            string? newestPath = null;
            var receiptsSeen = 0;
            foreach (var path in Directory.EnumerateFiles(receiptsRoot, "*.json", SearchOption.AllDirectories))
            {
                receiptsSeen++;
                try
                {
                    var stored = JsonReadPolicy.ReadStored<ControlPlaneInstallationReceipt>(File.ReadAllText(path, Encoding.UTF8));
                    var receipt = stored.Value;
                    if (receipt.Schema != ControlPlaneInstallationContract.ReceiptSchema
                        || HomeIdentity(receipt.Registry, "receipted Registry Home database") != registry)
                    {
                        continue;
                    }
                    if (newest is null || receipt.InstalledAtUnixMs >= newest.InstalledAtUnixMs)
                    {
                        newest = receipt;
                        newestPath = Path.GetFullPath(path);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    // An older or malformed receipt cannot answer this read and
                    // is not counted as agreement with the current dispatcher.
                }
            }

            if (newest is null)
            {
                return Unknown(
                    $"none of the {receiptsSeen} receipt(s) under {receiptsRoot} governs {registry} at schema {ControlPlaneInstallationContract.ReceiptSchema}");
            }

            return new InstalledControlInspection(
                ProjectionSchema,
                "known",
                null,
                newestPath,
                newest.BinarySetId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Unknown(error.Message);
        }
    }

    static InstalledControlInspection Unknown(string reason) =>
        new(ProjectionSchema, "unknown", reason, null, null);

    /// <summary>
    /// CHG 2026-09-12: the Registry an installed set answers to is a file on a
    /// machine that holds the Home and a service address on one that pulls
    /// from it; a URL is its own identity, a path resolves to the real file.
    /// </summary>
    static string HomeIdentity(string registry, string role) =>
        Uri.TryCreate(registry, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.AbsoluteUri.TrimEnd('/')
            : ExistingFile(registry, role);

    static string ExistingFile(string path, string role)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"{role} {path} is not a regular file");
        }
        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
    }
}

/// <summary>
/// Proves the immutable five-member C# Scheduler receipt before it opens
/// Registry Home or executes any receipted program.
/// </summary>
public static class InstallationVerification
{
    /// <summary>
    /// The schema this verifier understands, taken from the installer's own
    /// constant rather than repeated here. Two copies of one schema string is
    /// how a receipt writer and its reader come to disagree: this file held
    /// "v8" while the installer had moved to v9, and every verification of a
    /// current receipt was refused before any field was read.
    /// </summary>
    const string ReceiptSchema = ControlPlaneInstallationContract.ReceiptSchema;

    public static InstallationVerificationOutcome VerifyMaterialized(
        string receiptPath,
        string registryPath)
    {
        try
        {
            var receiptFile = ExistingFile(receiptPath, "scheduler installation receipt");
            var encoded = File.ReadAllText(receiptFile, Encoding.UTF8);
            // CHG 2026-09-20 (owner): this file is a STORED record our own
            // installer wrote, not caller-supplied JSON, so it is read with the
            // tolerant policy this class documents for stored records. Read as
            // caller input it was refused whole for carrying one field this
            // reader does not declare -- `stage_runner_binary_sha256`, written by
            // a newer publisher into 26 of the 33 receipts on this machine -- and
            // that refusal protected nothing: an extra field cannot make the
            // binary paths and digests below any less true, and those are still
            // verified one by one. The owner's instruction: "get rid of the
            // refusal if not needed ... if it matters use field matching".
            var stored = JsonReadPolicy.ReadStored<ControlPlaneInstallationReceipt>(encoded);
            var receipt = stored.Value;
            if (receipt.Schema != ReceiptSchema)
            {
                throw new InvalidOperationException(
                    $"scheduler installation receipt schema is \"{receipt.Schema}\", expected \"{ReceiptSchema}\"");
            }
            var requestedRegistry = HomeIdentity(registryPath, "Registry Home database");
            var receiptedRegistry = HomeIdentity(receipt.Registry, "receipted Registry Home database");
            if (requestedRegistry != receiptedRegistry)
            {
                throw new InvalidOperationException(
                    $"control-plane installation receipt governs {receiptedRegistry}, not requested Registry Home {requestedRegistry}");
            }

            var site = VerifyReceiptedFile(
                receipt.SiteBinaryPath,
                receipt.SiteBinarySha256,
                "fern-site executable");
            var experiment = VerifyReceiptedFile(
                receipt.ExperimentBinaryPath,
                receipt.ExperimentBinarySha256,
                "fern-experiment executable");
            var run = VerifyReceiptedFile(
                receipt.RunBinaryPath,
                receipt.RunBinarySha256,
                "fern-run executable");
            if (receipt.CompileBinaryPath is not { Length: > 0 } compilePath
                || receipt.CompileBinarySha256 is not { Length: > 0 } compileDigest)
            {
                throw new InvalidOperationException(
                    "this installation carries no fern-compile member: it was installed before the card compiler joined the set, "
                    + "and a cell on this machine would refuse at its identity call. Install the control plane again.");
            }

            var compile = VerifyReceiptedFile(
                compilePath,
                compileDigest,
                "fern-compile executable");

            var setMembers = new List<(string, string)>
            {
                ("fern-site", receipt.SiteBinarySha256),
                ("fern-experiment", receipt.ExperimentBinarySha256),
                ("fern-run", receipt.RunBinarySha256),
                ("fern-compile", receipt.CompileBinarySha256),
            };
            var expectedSetId = BinarySetId([.. setMembers]);
            if (receipt.BinarySetId != expectedSetId)
            {
                throw new InvalidOperationException(
                    $"control-plane receipt binary set {receipt.BinarySetId} differs from member identity {expectedSetId}");
            }

            var expectedReceiptName = expectedSetId["sha256:".Length..] + ".json";
            if (Path.GetFileName(receiptFile) != expectedReceiptName)
            {
                throw new InvalidOperationException(
                    "control-plane receipt filename does not name its complete execution binary-set digest");
            }

            return new InstallationVerificationOutcome.Accepted(
                new VerifiedInstallation(
                    requestedRegistry,
                    ExistingDirectory(receipt.WorkRoot, "receipted Fern work root"),
                    ExistingDirectory(receipt.DeployRoot, "receipted Fern deploy store root"),
                    // CHG 2026-09-12: a pull worker records neither, and a
                    // store that was never installed cannot be verified as
                    // missing; a recorded one is still checked.
                    receipt.ProfilesRoot is { Length: > 0 } profiles
                        ? ExistingDirectory(profiles, "receipted Fern execution profiles directory") : null,
                    receipt.SshHelper is { Length: > 0 } helper
                        ? ExistingFile(helper, "receipted maintained SSH helper") : null,
                    receipt.SiteBinarySha256,
                    site,
                    receipt.ExperimentBinarySha256,
                    experiment,
                    receipt.RunBinarySha256,
                    run,
                    compileDigest,
                    compile,
                    receipt.BinarySetId));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new InstallationVerificationOutcome.Refused(error.Message);
        }
    }

    static string VerifyReceiptedFile(string path, string digest, string name)
    {
        var file = ExistingFile(path, $"receipted {name}");
        try
        {
            VerifyFileDigest(file, digest, $"receipted {name}");
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"receipted {name} failed byte verification; no election was attempted: {error.Message}");
        }

        return file;
    }

    static string ExistingDirectory(string path, string name)
    {
        var absolute = Path.GetFullPath(path);
        var info = new DirectoryInfo(absolute);
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new InvalidOperationException($"{name} {absolute} is not a real directory");
        }

        return absolute;
    }

    /// <summary>
    /// CHG 2026-09-12: the Registry an installed set answers to is a file on a
    /// machine that holds the Home and a service address on one that pulls
    /// from it; a URL is its own identity, a path resolves to the real file.
    /// </summary>
    static string HomeIdentity(string registry, string role) =>
        Uri.TryCreate(registry, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.AbsoluteUri.TrimEnd('/')
            : ExistingFile(registry, role);

    static string ExistingFile(string path, string role)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"{role} {path} is not a regular file");
        }

        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
    }

    static void VerifyFileDigest(string path, string expected, string label)
    {
        using var stream = File.OpenRead(path);
        var actual = Sha256Hex(stream);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{label} digest {actual} differs from receipt {expected}");
        }
    }

    static string BinarySetId(params (string Name, string Digest)[] members)
    {
        using var material = new MemoryStream();
        material.Write("fern.installed-binary-set.v1\0"u8);
        foreach (var member in members.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (member.Digest.Length != 64
                || member.Digest.Any(character => !Uri.IsHexDigit(character))
                || member.Digest != member.Digest.ToLowerInvariant())
            {
                throw new InvalidOperationException(
                    $"{member.Name} SHA-256 must be 64 lowercase hexadecimal characters");
            }

            material.Write(Encoding.UTF8.GetBytes(member.Name));
            material.WriteByte(0);
            material.Write(Encoding.UTF8.GetBytes(member.Digest));
            material.WriteByte(0);
        }

        return "sha256:" + Sha256Hex(material.ToArray());
    }

    static string Sha256Hex(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

sealed class ControlPlaneInstallationReceipt
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("installed_at_unix_ms")]
    public required long InstalledAtUnixMs { get; init; }

    [JsonPropertyName("registry")]
    public required string Registry { get; init; }

    [JsonPropertyName("work_root")]
    public required string WorkRoot { get; init; }

    [JsonPropertyName("deploy_root")]
    public required string DeployRoot { get; init; }

    [JsonPropertyName("profiles_root")]
    public required string? ProfilesRoot { get; init; }

    [JsonPropertyName("ssh_helper")]
    public required string? SshHelper { get; init; }

    [JsonPropertyName("site_binary_sha256")]
    public required string SiteBinarySha256 { get; init; }

    [JsonPropertyName("site_binary_path")]
    public required string SiteBinaryPath { get; init; }

    [JsonPropertyName("experiment_binary_sha256")]
    public required string ExperimentBinarySha256 { get; init; }

    [JsonPropertyName("experiment_binary_path")]
    public required string ExperimentBinaryPath { get; init; }

    [JsonPropertyName("run_binary_sha256")]
    public required string RunBinarySha256 { get; init; }

    [JsonPropertyName("run_binary_path")]
    public required string RunBinaryPath { get; init; }

    /// <summary>
    /// Optional so that an installation written before 2026-09-11 can still be
    /// READ, and refused by name rather than by a deserializer message. A
    /// receipt without this member is a machine that cannot mint a
    /// configuration identity, and saying which binary is missing is the whole
    /// difference between "reinstall" and "reinstall what".
    /// </summary>
    [JsonPropertyName("compile_binary_sha256")]
    public string? CompileBinarySha256 { get; init; }

    [JsonPropertyName("compile_binary_path")]
    public string? CompileBinaryPath { get; init; }

    [JsonPropertyName("binary_set_id")]
    public required string BinarySetId { get; init; }

    [JsonPropertyName("activation")]
    public required string Activation { get; init; }

    /// <summary>
    /// CHG 2026-09-11 night (slice 10): each member's input digest, by
    /// member name, when the installer built or reused it from committed
    /// inputs. Absent from receipts written before the slice.
    /// </summary>
    [JsonPropertyName("member_inputs")]
    public Dictionary<string, string>? MemberInputs { get; init; }
}
