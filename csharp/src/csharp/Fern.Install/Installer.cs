using Fern.Registry;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fern.Install;

/// <summary>
/// Caller JSON for one installation (Go: InstallConfig in install.go). Every
/// field is required so a missing source binary is a refusal at the boundary,
/// never a default.
/// </summary>
public sealed class InstallConfig
{
    /// <summary>A release's complete identity, checked before receipt publication and activation.</summary>
    [JsonPropertyName("expected_binary_set_id")]
    public string? ExpectedBinarySetId { get; init; }
    [JsonPropertyName("source_site_binary")]
    public required string SourceSiteBinary { get; init; }

    [JsonPropertyName("source_experiment_binary")]
    public required string SourceExperimentBinary { get; init; }

    [JsonPropertyName("source_run_binary")]
    public required string SourceRunBinary { get; init; }

    /// <summary>
    /// The Go card compiler. It owns the scientific vocabulary and mints a
    /// configuration's identity, so a worker machine without it cannot run a
    /// registered cell at all: the cell's first act is to ask for that
    /// identity. Installed beside fern-run for exactly that reason.
    /// </summary>
    [JsonPropertyName("source_compile_binary")]
    public required string SourceCompileBinary { get; init; }

    [JsonPropertyName("source_revision")]
    public required string SourceRevision { get; init; }

    [JsonPropertyName("build_inventory")]
    public required string BuildInventory { get; init; }

    [JsonPropertyName("registry")]
    public required string Registry { get; init; }

    [JsonPropertyName("work_root")]
    public required string WorkRoot { get; init; }

    /// <summary>
    /// The content-addressed store the supervisor materializes Stage sources
    /// from (<c>source-blobs/</c>, <c>stage-source-roots/</c>). Chosen at
    /// install time; defaults to the installing tree's <c>deploy/</c>.
    /// </summary>
    [JsonPropertyName("deploy_root")]
    public required string DeployRoot { get; init; }

    /// <summary>
    /// The directory holding <c>&lt;execution-profile-id&gt;.json</c>. Chosen at
    /// install time; defaults to the installing tree's <c>profiles/</c>.
    /// </summary>
    [JsonPropertyName("profiles_root")]
    /// <summary>
    /// CHG 2026-09-12: null on a machine that places no work. A pull worker
    /// (run-study-cells --home) never reads an execution profile: the Home
    /// decides what it runs, and the profiles live where the placement does.
    /// </summary>
    public required string? ProfilesRoot { get; init; }

    /// <summary>
    /// The maintained SSH helper an SSH Worker Site is reached through.
    /// Chosen at install time; defaults to the installing tree's
    /// <c>scripts/connect_runpod_ssh.sh</c>. Recorded as a file rather than
    /// folded into a root because it is the one script the set executes.
    /// </summary>
    [JsonPropertyName("ssh_helper")]
    /// <summary>
    /// CHG 2026-09-12: null on a machine that reaches no other machine. A
    /// pull worker is reached, not reaching; the helper is the controller's.
    /// </summary>
    public required string? SshHelper { get; init; }

    [JsonPropertyName("deployment_root")]
    public required string DeploymentRoot { get; init; }

    /// <summary>
    /// CHG 2026-09-11 night (slice 10): each member's input digest
    /// (<see cref="ControlMembers.InputDigest"/>) when the installer built or
    /// reused it from committed inputs, recorded in the receipt under
    /// <c>member_inputs</c>; a member given as an explicit path has none.
    /// Optional because a set of verified bytes is still a set without it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? MemberInputs { get; init; }
}

public sealed record Installation(
    [property: JsonPropertyName("receipt_path")] string ReceiptPath,
    [property: JsonPropertyName("site_binary_path")] string SiteBinaryPath,
    [property: JsonPropertyName("experiment_binary_path")] string ExperimentBinaryPath,
    [property: JsonPropertyName("run_binary_path")] string RunBinaryPath,
    [property: JsonPropertyName("compile_binary_path")] string CompileBinaryPath,
    [property: JsonPropertyName("binary_set_id")] string BinarySetId);

/// <summary>
/// CHG 2026-09-10: a union, not an abstract record. Under the hierarchy a
/// caller could write <c>((InstallOutcome.Installed)outcome).Installation</c>
/// after checking only for the refusal — the compiler allowed the downcast and
/// a third case would have thrown at run time. On a union that cast does not
/// compile (CS0030), so every reader matches. The cases are the shared
/// <see cref="Accepted{T}"/> and <see cref="Refused"/>.
/// </summary>
public union InstallOutcome(Accepted<Installation>, Refused);

public static class ControlPlaneInstallationContract
{
    /// <summary>
    /// v2 (2026-09-11): `fern-compile` joins the set. A worker machine's
    /// frozen source snapshot holds no Go build, so until this member was
    /// installed a registered cell on the pod refused at its identity call:
    /// Python asks the compiler for the configuration's identity before it
    /// trains anything, and there was nothing to ask.
    ///
    /// v1 (2026-09-11): the installed set is a machine's control plane, not a
    /// scheduler service. The resident daemon, its systemd unit and the
    /// `fern-scheduler-cs` binary retired with the Plan layer, so the set no
    /// longer carries a scheduler member and the receipt no longer names a
    /// unit, a service or a cycle. What it installs is what a machine executes.
    ///
    /// v10 (2026-08-31): Scheduler installation identity contains only C# runtime
    /// members. Go `fern-card` and C# `fern-registry` remain the independently
    /// receipted Cardbox reconcile service's inputs; they are not members of
    /// the Scheduler set, and serve never verifies or executes them.
    ///
    /// v9 (2026-08-29): `workspace_root` is gone, and `deploy_root`,
    /// `profiles_root` and `ssh_helper` name what the installed set actually
    /// reads. An installed set is bound to CONTENT — a source revision and its
    /// member digests — so it may not depend on a worktree that can move, be
    /// deleted, or fall a commit behind. The migration chain, formerly a
    /// fourth read under that root, is now embedded in Fern.Registry and
    /// travels inside the binaries.
    ///
    /// The receipt's own identity rule is unchanged and is worth restating,
    /// because these fields sit inside it: the receipt is FILED under the
    /// complete binary-set digest (`&lt;set-digest&gt;.json`, verified against
    /// `binary_set_id`), and each member carries its own SHA-256, which
    /// verification recomputes from the file before any election. The store
    /// paths below are NOT part of that identity: two hosts may serve one
    /// binary set from different directories, and moving a store does not mint
    /// a new set. What the paths must do is exist and hold what they claim,
    /// which is checked at install time and again when serve verifies.
    /// </summary>
    public const string ReceiptSchema = "fern.control_plane_installation.v2";
    public const string StageRunnerCapabilitySchema = "fern.stage_runner_capability.v1";
    public const string StageRunnerProtocol = "fern.stage_runner.v1";
    public const long ProbeTimeoutMs = 5000;
    public const long ProbeOutputLimit = 64 * 1024;
}

/// <summary>
/// Publishes a machine's control-plane binaries, content-addressed, with one
/// receipt naming the complete set. It starts nothing: an installed binary is
/// a file a person or a verb runs, never a service.
///
/// All candidate and path validation completes before any output root is
/// created, so a refusal writes nothing.
/// </summary>
public static class ControlPlaneInstaller
{
    public static InstallOutcome Install(InstallConfig config)
    {
        // CHG 2026-09-12: the Registry this set is installed against is a file
        // on a machine that holds the Home, and the Home's address on a machine
        // that pulls from one (run-study-cells --home). Both are recorded
        // verbatim; only a path is checked to exist.
        var (registry, registryError) = HomeIdentity(config.Registry);
        if (registryError is not null)
        {
            return new Refused(registryError);
        }

        // The stores the installed set will read at run time, named here and
        // recorded in the receipt. They are checked now, once, so that a
        // missing store is an install-time refusal rather than a worker that
        // starts and then cannot materialize what it was given.
        var (deployRoot, deployError) = ExistingDirectory(config.DeployRoot, "Fern deploy store root");
        if (deployError is not null)
        {
            return new Refused(deployError);
        }

        string? profilesRoot = null;
        if (config.ProfilesRoot is { Length: > 0 })
        {
            var (resolvedProfiles, profilesError) = ExistingDirectory(config.ProfilesRoot, "Fern execution profiles directory");
            if (profilesError is not null)
            {
                return new Refused(profilesError);
            }
            profilesRoot = resolvedProfiles;
        }

        string? sshHelper = null;
        if (config.SshHelper is { Length: > 0 })
        {
            var (resolvedHelper, sshHelperError) = ExistingFile(config.SshHelper, "maintained SSH helper");
            if (sshHelperError is not null)
            {
                return new Refused(sshHelperError);
            }
            sshHelper = resolvedHelper;
        }

        var sources = new (string Source, string Role)[]
        {
            (config.SourceSiteBinary, "fern-site executable"),
            (config.SourceExperimentBinary, "fern-experiment executable"),
            (config.SourceRunBinary, "fern-run executable"),
            (config.SourceCompileBinary, "fern-compile executable"),
        };
        var resolved = new string[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var (path, error) = ExistingFile(sources[i].Source, sources[i].Role);
            if (error is not null)
            {
                return new Refused(error);
            }

            resolved[i] = path!;
        }

        var (siteSource, experimentSource, runSource, compileSource) =
            (resolved[0], resolved[1], resolved[2], resolved[3]);

        if (config.BuildInventory.Length == 0)
        {
            return new Refused("control-plane installation needs an explicit build inventory path");
        }

        if (ProbeControlIdentity(siteSource, "Worker Site", InstallationIdentityContract.WorkerSiteRole) is { } siteRefusal)
        {
            return new Refused(siteRefusal);
        }

        // Every candidate is now verified; only from here does the installer
        // create directories and publish bytes.
        var workRoot = MakeAbsoluteDirectory(config.WorkRoot);
        var deploymentRoot = MakeAbsoluteDirectory(config.DeploymentRoot);

        var publications = new List<(string Source, string Directory, string Prefix, string Label)>
        {
            (siteSource, "worker-site-binaries", "fern-site.", "Worker Site"),
            (experimentSource, "experiment-binaries", "fern-experiment.", "experiment authoring"),
            (runSource, "run-operator-binaries", "fern-run.", "run operator"),
            (compileSource, "compile-binaries", "fern-compile.", "card compiler"),
        };

        var published = new (string Digest, string Path)[publications.Count];
        for (var i = 0; i < publications.Count; i++)
        {
            var (source, directory, prefix, label) = publications[i];
            var (digest, path, error) = DurableFiles.PublishBinary(source, Path.Combine(deploymentRoot, directory), prefix);
            if (error is not null)
            {
                return new Refused($"publish immutable {label} binary: {error}");
            }

            published[i] = (digest!, path!);
        }

        var (siteDigest, sitePath) = published[0];
        var (experimentDigest, experimentPath) = published[1];
        var (runDigest, runPath) = published[2];
        var (compileDigest, compilePath) = published[3];
        // The installed set is exactly the executables this machine runs.
        // Cardbox owns the Go card/home pair under its separate
        // knowledge-reconcile receipt.
        var members = new List<BinarySetMember>
        {
            new("fern-site", siteDigest, sitePath),
            new("fern-experiment", experimentDigest, experimentPath),
            new("fern-run", runDigest, runPath),
            new("fern-compile", compileDigest, compilePath),
        };
        return BuildInventory.SetIdFor(members) switch
        {
            Refused setIdRefused =>
                new Refused($"identify complete installed binary set: {setIdRefused.Detail}"),
            Accepted<string> setIdentity => AfterSetId(setIdentity.Value),
        };

        InstallOutcome AfterSetId(string binarySetId)
        {
            if (config.ExpectedBinarySetId is { } expected && expected != binarySetId)
                return new Refused($"the verified set is {binarySetId} and the release manifest published {expected}");
            // Filed under the complete set digest, in a directory named by the
            // Registry Home this set was installed against: two Homes on one
            // machine are two installations, and neither overwrites the other.
            var receiptPath = Path.Combine(
                deploymentRoot,
                "control-plane-installations",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(registry!))),
                binarySetId["sha256:".Length..] + ".json");

            var receipt = new JsonObject
            {
                ["schema"] = ControlPlaneInstallationContract.ReceiptSchema,
                ["installed_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["registry"] = registry,
                ["work_root"] = workRoot,
                ["deploy_root"] = deployRoot,
                ["profiles_root"] = profilesRoot,
                ["ssh_helper"] = sshHelper,
                // A JsonObject drops nothing: a null here is the recorded fact
                // that this machine has no such store, not a missing field.
                ["site_binary_sha256"] = siteDigest,
                ["site_binary_path"] = sitePath,
                ["experiment_binary_sha256"] = experimentDigest,
                ["experiment_binary_path"] = experimentPath,
                ["run_binary_sha256"] = runDigest,
                ["run_binary_path"] = runPath,
                ["compile_binary_sha256"] = compileDigest,
                ["compile_binary_path"] = compilePath,
                ["binary_set_id"] = binarySetId,
                ["activation"] = "binaries published; this command starts nothing",
            };
            if (config.MemberInputs is { Count: > 0 } memberInputs)
            {
                var inputs = new JsonObject();
                foreach (var (member, digest) in memberInputs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    inputs[member] = digest;
                }
                receipt["member_inputs"] = inputs;
            }
            DurableFiles.Write(receiptPath, PrettyJson(receipt), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            BinarySetActivation activation;
            using (var inventory = BuildInventory.Open(config.BuildInventory))
            {
                activation = inventory.Activate(config.SourceRevision, members);
            }

            return activation switch
            {
                Refused activationRefused =>
                    new Refused($"activate complete installed binary set: {activationRefused.Detail}"),
                Accepted<BinarySet> activationAccepted => AfterActivation(activationAccepted.Value),
            };

            InstallOutcome AfterActivation(BinarySet installedSet)
            {
                if (installedSet.SetId != binarySetId)
                {
                    return new Refused(
                        $"activated binary set {installedSet.SetId} differs from installation receipt {binarySetId}");
                }

                return new Accepted<Installation>(new Installation(
                    ReceiptPath: receiptPath,
                    SiteBinaryPath: sitePath,
                    ExperimentBinaryPath: experimentPath,
                    RunBinaryPath: runPath,
                    CompileBinaryPath: compilePath,
                    BinarySetId: binarySetId));
            }
        }
    }

    /// <summary>
    /// Verifies one control-plane candidate before the installer writes a
    /// deployment directory, receipt, or unit (Go: ProbeControlIdentity).
    /// Returns the refusal detail, or null when the candidate is accepted.
    /// </summary>
    static string? ProbeControlIdentity(string path, string label, string expectedRole)
    {
        var timeoutMs = ControlPlaneInstallationContract.ProbeTimeoutMs;
        var maxBytes = ControlPlaneInstallationContract.ProbeOutputLimit;
        return IdentityProbe.Run(path, expectedRole, timeoutMs, maxBytes, "installation-identity") switch
        {
            IdentityProbe.Outcome.Accepted => null,
            IdentityProbe.Outcome.Missing => $"inspect {label} executable {path}: no such file",
            IdentityProbe.Outcome.NotRegular => $"{label} executable {path} is not a regular file; installation wrote nothing",
            IdentityProbe.Outcome.NotExecutable => $"{label} executable {path} has no executable bit; installation wrote nothing",
            IdentityProbe.Outcome.TimedOut => $"{label} identity probe {path} exceeded {timeoutMs} ms; installation wrote nothing",
            IdentityProbe.Outcome.UnboundedOutput => $"{label} identity probe {path} exceeded the {maxBytes}-byte output limit; installation wrote nothing",
            IdentityProbe.Outcome.Failed failed => $"{label} identity probe {path} failed: exit status {failed.ExitCode}: {failed.Stderr.Trim()}; installation wrote nothing",
            IdentityProbe.Outcome.ParseFailed parse => $"{label} identity probe {path} did not return typed JSON: {RenderParse(parse.Parse)}; installation wrote nothing",
            var other => throw new InvalidOperationException(other.ToString()),
        };
    }

    static string RenderParse(InstallationIdentityParse parse) => parse switch
    {
        InstallationIdentityParse.Refused refused => refused.Detail,
        InstallationIdentityParse.UnknownField unknown => $"installation identity JSON is invalid: unknown field \"{unknown.FieldName}\"",
        var other => other.ToString() ?? "",
    };


    static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Go's strict decoder: exactly one JSON object, no unknown field, no
    /// trailing value. The error text mirrors Go so the install tests can
    /// assert the same words.
    /// </summary>
    static (JsonDocument? Document, string? Error) OneStrictObject(byte[] data, IReadOnlyList<string> knownFields)
    {
        int end;
        try
        {
            var reader = new Utf8JsonReader(data, new JsonReaderOptions { AllowTrailingCommas = false });
            if (!reader.Read())
            {
                return (null, "returned invalid JSON: EOF");
            }

            reader.Skip();
            end = (int)reader.BytesConsumed;
            if (reader.Read())
            {
                return (null, "returned trailing output");
            }
        }
        catch (JsonException error)
        {
            return (null, $"returned invalid JSON: {error.Message}");
        }

        var document = JsonDocument.Parse(data.AsMemory(0, end));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return (null, "returned invalid JSON: not an object");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!knownFields.Contains(property.Name))
            {
                document.Dispose();
                return (null, $"returned invalid JSON: unknown field \"{property.Name}\"");
            }
        }

        return (document, null);
    }

    static byte[] PrettyJson(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            node.WriteTo(writer);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>
    /// The Registry this installed set answers to: an existing file when the
    /// machine holds the Home, or the Home service address when it pulls from
    /// one over HTTP. A URL is recorded as written, with no trailing slash, so
    /// two spellings of one Home are one identity.
    /// </summary>
    static (string? Identity, string? Error) HomeIdentity(string registry)
    {
        if (Uri.TryCreate(registry, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            return (url.AbsoluteUri.TrimEnd('/'), null);
        }
        return ExistingFile(registry, "Registry Home database");
    }

    static (string? Path, string? Error) ExistingFile(string path, string role)
    {
        if (Directory.Exists(path) || !File.Exists(path))
        {
            return (null, $"{role} {path} is not a regular file");
        }

        var info = new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return (Path.GetFullPath(target?.FullName ?? info.FullName), null);
    }

    static (string? Path, string? Error) ExistingDirectory(string path, string role)
    {
        if (!Directory.Exists(path))
        {
            return (null, $"{role} {path} is not a directory");
        }

        var info = new DirectoryInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return (Path.GetFullPath(target?.FullName ?? info.FullName), null);
    }

    static string MakeAbsoluteDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }
}

/// <summary>
/// Durable writes the way Go's install.go does them: staging file, fsync,
/// rename into place, then fsync of the directory so the name itself is
/// durable. Hard links and directory fsync have no managed API, so the two
/// libc calls are declared here and nowhere else.
/// </summary>
public static class DurableFiles
{
    public static void Write(string path, byte[] data, UnixFileMode mode)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var stagingPath = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write))
            {
                staging.Write(data, 0, data.Length);
                staging.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagingPath, mode);
            }
            File.Move(stagingPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(stagingPath);
        }

        SyncDirectory(directory);
    }

    /// <summary>
    /// Publishes one binary under its content address (Go: publishBinary).
    /// The bytes are copied to a staging file, hashed on the way, made
    /// read-only and executable, synced, and hard-linked into place; an
    /// existing published file is accepted only when its bytes still match.
    /// </summary>
    public static (string? Digest, string? Path, string? Error) PublishBinary(string source, string directory, string prefix)
    {
        var expectedDigest = FileSha256(source);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, prefix + expectedDigest);
        if (File.Exists(finalPath))
        {
            if (FileSha256(finalPath) != expectedDigest)
            {
                return (null, null, $"existing published binary {finalPath} has wrong bytes");
            }

            return (expectedDigest, finalPath, null);
        }

        var stagingPath = Path.Combine(directory, ".publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write))
            using (var input = File.OpenRead(source))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var chunk = new byte[1 << 16];
                int n;
                while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    staging.Write(chunk, 0, n);
                    hash.AppendData(chunk, 0, n);
                }

                var copiedDigest = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (copiedDigest != expectedDigest)
                {
                    return (null, null, $"binary source changed while publishing: expected {expectedDigest}, copied {copiedDigest}");
                }

                staging.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    stagingPath,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            if (!HardLink(stagingPath, finalPath))
            {
                if (!File.Exists(finalPath) || FileSha256(finalPath) != expectedDigest)
                {
                    return (null, null, $"racing published binary {finalPath} has wrong bytes");
                }
            }

            SyncDirectory(directory);
            return (expectedDigest, finalPath, null);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    /// <summary>The content address of one file's bytes: lowercase SHA-256 hex.</summary>
    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Returns false when the target already exists (EEXIST).</summary>
    public static bool HardLink(string existing, string target)
    {
        if (Libc.link(existing, target) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == Libc.EEXIST)
        {
            return false;
        }

        throw new IOException($"link {existing} -> {target} failed with errno {error}");
    }

    /// <summary>
    /// Take an exclusive advisory lock on an open file, or report that
    /// another process holds it. Returns false only for EWOULDBLOCK.
    ///
    /// This must be flock(2) and nothing else. Rust's deployment lock is
    /// std's <c>File::try_lock</c>, which is flock on Unix, and an SSH Plan
    /// start takes the SAME lock so it cannot race a deployment. .NET's
    /// managed <c>FileStream.Lock</c> is an fcntl byte-range lock, a
    /// different and independent mechanism on Linux: two processes using the
    /// two kinds on one file both believe they hold it. A lock that does not
    /// exclude the other language's lock is not a lock, so the call is
    /// declared here beside link and fsync, which have no managed API either.
    /// </summary>
    public static bool TryLockExclusive(SafeFileHandle handle)
    {
        if (Libc.flock((int)handle.DangerousGetHandle(), Libc.LOCK_EX | Libc.LOCK_NB) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is Libc.EWOULDBLOCK)
        {
            return false;
        }

        throw new IOException($"flock failed with errno {error}");
    }

    /// <summary>
    /// Open one lock file as a bare descriptor that closes across exec.
    ///
    /// It has to be a bare descriptor. .NET's FileStream emulates Windows
    /// FileShare on Unix by taking an flock of its own at open time — a SHARED
    /// one for FileShare.ReadWrite — so merely opening the lock file through
    /// FileStream already blocks any exclusive flock, including Rust's. A
    /// caller could then never tell "the deployment lock is held" from "some
    /// process has the file open", and Rust's Plan start would be refused by a
    /// C# process that was not deploying at all.
    ///
    /// CLOEXEC is set here rather than relied on, because open(2) does not set
    /// it: a supervisor this process execs must not inherit the lock and hold
    /// it open after the deployment ends. Rust sets the same flag on the same
    /// file for the same reason (binary_deploy::lock_deploy).
    /// </summary>
    public static SafeFileHandle OpenLockFile(string path)
    {
        var descriptor = Libc.open(path, Libc.O_RDWR | Libc.O_CREAT, Libc.LockFileMode);
        if (descriptor < 0)
        {
            throw new IOException($"open lock file {path} failed with errno {Marshal.GetLastPInvokeError()}");
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            var flags = Libc.fcntl(descriptor, Libc.F_GETFD);
            if (flags < 0 || Libc.fcntl(descriptor, Libc.F_SETFD, flags | Libc.FD_CLOEXEC) < 0)
            {
                throw new IOException(
                    $"cannot make lock file {path} close across exec: errno {Marshal.GetLastPInvokeError()}");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>True when this descriptor closes across exec, as Rust sets it.</summary>
    public static bool ClosesOnExec(SafeFileHandle handle)
    {
        var flags = Libc.fcntl((int)handle.DangerousGetHandle(), Libc.F_GETFD);
        if (flags < 0)
        {
            throw new IOException($"fcntl F_GETFD failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return (flags & Libc.FD_CLOEXEC) != 0;
    }

    public static void SyncDirectory(string directory)
    {
        var descriptor = Libc.open(directory, Libc.O_RDONLY | Libc.O_DIRECTORY);
        if (descriptor < 0)
        {
            throw new IOException($"open directory {directory} for fsync failed with errno {Marshal.GetLastPInvokeError()}");
        }

        try
        {
            if (Libc.fsync(descriptor) != 0)
            {
                throw new IOException($"fsync directory {directory} failed with errno {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            _ = Libc.close(descriptor);
        }
    }

    static class Libc
    {
        public const int EEXIST = 17;
        public const int EWOULDBLOCK = 11;
        public const int LOCK_EX = 2;
        public const int LOCK_NB = 4;
        public const int F_GETFD = 1;
        public const int F_SETFD = 2;
        public const int FD_CLOEXEC = 1;
        public const int O_RDWR = 2;
        public const int O_CREAT = 0x40;

        /// <summary>0644: the lock file carries no content anyone reads.</summary>
        public const int LockFileMode = 0x1A4;
        public const int O_RDONLY = 0;
        public const int O_DIRECTORY = 0x10000;

        [DllImport("libc", SetLastError = true)]
        public static extern int flock(int descriptor, int operation);

        [DllImport("libc", SetLastError = true)]
        public static extern int fcntl(int descriptor, int command);

        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
        public static extern int fcntl(int descriptor, int command, int argument);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        public static extern int open(string path, int flags, int mode);

        [DllImport("libc", SetLastError = true)]
        public static extern int link(string existing, string target);

        [DllImport("libc", SetLastError = true)]
        public static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int fsync(int descriptor);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int descriptor);
    }
}
