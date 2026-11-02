using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fern.Site;

/// <summary>
/// The Worker Site's DB-free transport and process boundary. It verifies
/// Home-issued package bytes before any runner process may start, copies them
/// into a fresh runtime through one atomic rename, runs the packaged runner,
/// and publishes an immutable transport return package Home can reopen and
/// check independently.
///
/// It never opens a database, never learns what the Stage means, and never
/// derives a scientific result digest. The Site binds the runner-report bytes;
/// only Registry Home may interpret outputs and mint the scientific receipt.
/// </summary>
public static class StageAttemptSite
{
    // ---- the identity the whole slice turns on ----

    /// <summary>
    /// The Site's promise that it answered the exact instructions Home issued:
    /// the instructions in declaration order with their files sorted by path,
    /// encoded with HTML escaping OFF, hashed.
    ///
    /// Pinned against the Rust contract at
    /// sha256:b2eec13800845c16459086f260e998a7d83d701108e5e4edee468f576b21871e
    /// for the instructions the ported Go test names, and against a second
    /// preimage full of quotes, control characters and non-ASCII captured from
    /// the Go executable itself.
    /// </summary>
    public static SiteResult<string> CanonicalInstructionsDigest(AttemptInstructions instructions)
    {
        var validation = ValidateInstructions(instructions);
        if (!validation.IsOk)
        {
            return validation.RefusalAs<string>();
        }
        var sorted = instructions with
        {
            Files = instructions.Files.OrderBy(file => file.RelativePath, Utf8Ordinal).ToList(),
        };
        return SiteResult<string>.Ok(
            "sha256:" + Sha256Hex(GoJson.InstructionsBytes(sorted, escapeHtml: false)));
    }

    // ---- verifying what Home issued ----

    /// <summary>
    /// Reopen one Home-issued package, check its exact file set and byte
    /// identities, and refuse whole-worker request contracts.
    ///
    /// Example: <c>VerifyStagePackage("/srv/fern/attempt-1/outbound")</c>
    /// </summary>
    public static SiteResult<StagePackage> VerifyStagePackage(string root) =>
        VerifyStagePackage(root, requireExactFileSet: true);

    static SiteResult<StagePackage> VerifyStagePackage(string root, bool requireExactFileSet)
    {
        var instructionsBytes = ReadRegularFile(Path.Combine(root, SitePaths.InstructionsFile));
        if (!instructionsBytes.IsOk)
        {
            return SiteResult<StagePackage>.Permanent(
                "cannot read packaged Stage instructions: " + instructionsBytes.Failure.Detail);
        }
        var decoded = DecodeOneObject(instructionsBytes.Value, ReadInstructions, "packaged Stage instructions");
        if (!decoded.IsOk)
        {
            return decoded.RefusalAs<StagePackage>();
        }
        var instructions = decoded.Value;
        var validation = ValidateInstructions(instructions);
        if (!validation.IsOk)
        {
            return validation.RefusalAs<StagePackage>();
        }

        var expectedPaths = new SortedSet<string>(Utf8Ordinal) { SitePaths.InstructionsFile };
        foreach (var declared in instructions.Files)
        {
            expectedPaths.Add(declared.RelativePath);
            var bytes = ReadRegularFile(Path.Combine(root, FromSlash(declared.RelativePath)));
            if (!bytes.IsOk)
            {
                return SiteResult<StagePackage>.Permanent(
                    $"cannot read packaged Stage file \"{declared.RelativePath}\": {bytes.Failure.Detail}");
            }
            var observedDigest = "sha256:" + Sha256Hex(bytes.Value);
            if (bytes.Value.LongLength != declared.ByteCount || observedDigest != declared.ByteDigest)
            {
                return SiteResult<StagePackage>.Permanent(
                    $"packaged Stage file \"{declared.RelativePath}\" changed: expected {declared.ByteCount} "
                    + $"bytes / {declared.ByteDigest}, found {bytes.Value.Length} bytes / {observedDigest}");
            }
        }

        var observedPaths = PackageFileSet(root);
        if (!observedPaths.IsOk)
        {
            return observedPaths.RefusalAs<StagePackage>();
        }
        if (requireExactFileSet)
        {
            if (!observedPaths.Value.SequenceEqual(expectedPaths, StringComparer.Ordinal))
            {
                return SiteResult<StagePackage>.Permanent("packaged Stage file set does not match its instructions");
            }
        }
        else
        {
            // A runtime that has already run holds its own outputs. Those three
            // roots are the runner's, and nothing else may appear.
            foreach (var observed in observedPaths.Value)
            {
                if (expectedPaths.Contains(observed))
                {
                    continue;
                }
                if (!observed.StartsWith("outputs/", StringComparison.Ordinal)
                    && !observed.StartsWith("report/", StringComparison.Ordinal)
                    && !observed.StartsWith("logs/", StringComparison.Ordinal))
                {
                    return SiteResult<StagePackage>.Permanent(
                        $"materialized Stage runtime contains undeclared path \"{observed}\"");
                }
            }
        }
        return SiteResult<StagePackage>.Ok(new StagePackage(root, instructions));
    }

    // ---- copying it somewhere it may be written to ----

    /// <summary>
    /// Copy one verified outbound package into a fresh runtime directory,
    /// reopen it, then publish it with one atomic rename. An identical existing
    /// runtime is replayed; different bytes are refused. A crash mid-copy leaves
    /// a temporary directory nobody reads, never a half-built runtime.
    /// </summary>
    public static SiteResult<StagePackage> MaterializeStagePackage(string outbound, string runtime)
    {
        var issued = VerifyStagePackage(outbound);
        if (!issued.IsOk)
        {
            return issued;
        }
        if (PathExists(runtime))
        {
            if (!Directory.Exists(runtime) || IsSymbolicLink(runtime))
            {
                return SiteResult<StagePackage>.Permanent(
                    $"Stage runtime {runtime} must be a non-symlink directory");
            }
            var existing = VerifyStagePackage(runtime, requireExactFileSet: false);
            if (!existing.IsOk)
            {
                return existing;
            }
            if (!SameInstructions(existing.Value.Instructions, issued.Value.Instructions))
            {
                return SiteResult<StagePackage>.Permanent(
                    "the existing Stage runtime contains different instructions");
            }
            return existing;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(runtime)) ?? ".";
        var prepared = PrepareTransportRoot(parent);
        if (!prepared.IsOk)
        {
            return prepared.RefusalAs<StagePackage>();
        }
        var temporary = CreateTemporaryDirectory(parent, Path.GetFileName(Path.TrimEndingDirectorySeparator(runtime)));
        var published = false;
        try
        {
            var paths = new List<string> { SitePaths.InstructionsFile };
            paths.AddRange(issued.Value.Instructions.Files.Select(file => file.RelativePath));
            foreach (var relative in paths)
            {
                var bytes = ReadRegularFile(Path.Combine(outbound, FromSlash(relative)));
                if (!bytes.IsOk)
                {
                    return bytes.RefusalAs<StagePackage>();
                }
                var destination = Path.Combine(temporary, FromSlash(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // The packaged runner is executable. Scientific checking is a
                // Home authority and therefore is not another Site executable.
                var mode = relative == SitePaths.PackagedRunnerFile
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var written = WriteDurableFile(destination, bytes.Value, mode);
                if (!written.IsOk)
                {
                    return written.RefusalAs<StagePackage>();
                }
            }
            var staged = VerifyStagePackage(temporary);
            if (!staged.IsOk)
            {
                return staged;
            }
            if (!SameInstructions(staged.Value.Instructions, issued.Value.Instructions))
            {
                return SiteResult<StagePackage>.Permanent("materialized Stage runtime changed its instructions");
            }
            Directory.Move(temporary, runtime);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
        return VerifyStagePackage(runtime);
    }

    // ---- running it ----

    /// <summary>
    /// Materialize verified bytes, read ONLY the operational runner projection
    /// from the immutable request, and run the fixed adapter. Scientific
    /// parameters, output admission, and result identities stay opaque to the
    /// Site; Home checks the returned report and output bytes independently.
    /// </summary>
    public static SiteResult<StageExecutionResult> RunStagePackage(
        string outbound,
        string runtime,
        string python,
        TimeSpan timeout,
        TextWriter? progress = null)
    {
        var runtimeExisted = PathExists(runtime);
        var verified = MaterializeStagePackage(outbound, runtime);
        if (!verified.IsOk)
        {
            return verified.RefusalAs<StageExecutionResult>();
        }
        if (timeout <= TimeSpan.Zero)
        {
            return SiteResult<StageExecutionResult>.Permanent("Stage runner timeout must be positive");
        }
        var projection = ReadRunnerProjection(verified.Value.Root);
        if (!projection.IsOk)
        {
            return projection.RefusalAs<StageExecutionResult>();
        }
        var requestPath = Path.Combine(verified.Value.Root, FromSlash(SitePaths.StageRequestFile));

        ProcessStartInfo runner;
        switch (projection.Value.Runner)
        {
            case "rust_binary":
                {
                    var binary = Path.Combine(verified.Value.Root, "implementation", "fern-stage-runner");
                    runner = new ProcessStartInfo(binary);
                    runner.ArgumentList.Add("run");
                    runner.ArgumentList.Add("--request");
                    runner.ArgumentList.Add(requestPath);
                    runner.ArgumentList.Add("--workspace");
                    runner.ArgumentList.Add(verified.Value.Root);
                    break;
                }
            case "python_module":
                {
                    if (!runtimeExisted && string.IsNullOrWhiteSpace(python))
                    {
                        return SiteResult<StageExecutionResult>.Permanent(
                            "Python Stage runner requires an explicit interpreter");
                    }
                    // CHG 2026-08-29: the source is INSTALLED once on the Site, not
                    // packaged per attempt; the instructions name its root and every
                    // file with its digest, and the Site checks those bytes before it
                    // imports them — the same guarantee the packaged copy gave.
                    var sourceRoot = verified.Value.Instructions.ImplementationSourceRoot;
                    var installedSource = VerifyInstalledSource(sourceRoot, verified.Value.Instructions.ImplementationSourceFiles);
                    if (!installedSource.IsOk)
                    {
                        return installedSource.RefusalAs<StageExecutionResult>();
                    }
                    runner = new ProcessStartInfo(python);
                    runner.ArgumentList.Add("-m");
                    runner.ArgumentList.Add("fern_stage_runtime");
                    runner.ArgumentList.Add("run");
                    runner.ArgumentList.Add("--request");
                    runner.ArgumentList.Add(requestPath);
                    runner.ArgumentList.Add("--workspace");
                    runner.ArgumentList.Add(verified.Value.Root);
                    runner.WorkingDirectory = sourceRoot;
                    runner.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                    // CHG 2026-09-26: src/ joins the import roots (serie/ moved to src/serie/).
                    runner.Environment["PYTHONPATH"] = sourceRoot + Path.PathSeparator + Path.Combine(sourceRoot, "python")
                        + Path.PathSeparator + Path.Combine(sourceRoot, "src");
                    break;
                }
            default:
                return SiteResult<StageExecutionResult>.Permanent(
                    $"unsupported Stage runner \"{projection.Value.Runner}\"");
        }

        if (runtimeExisted)
        {
            // The runner already ran once in this runtime. Re-running it would
            // be a second execution of the same Attempt, so the Site only
            // reopens the report left by the first run.
            if (!ReadRegularFile(Path.Combine(verified.Value.Root, FromSlash(SitePaths.StageReportFile))).IsOk)
            {
                return SiteResult<StageExecutionResult>.Permanent(
                    "existing Stage runtime has no complete runner report; retry requires a new Attempt runtime");
            }
            return ReadStageExecutionResult(verified.Value.Root, verified.Value.Instructions.AttemptId);
        }

        var output = new StageCommandOutput { Progress = progress };
        var runnerFailure = RunBoundedStageCommand(runner, output.Write, timeout);
        output.Flush();
        output.Tail.WriteBeside(verified.Value.Root);
        if (runnerFailure is not null && !runnerFailure.Value.WasNonZeroExit)
        {
            return SiteResult<StageExecutionResult>.Refused(SiteFailure.Unclassified(
                runnerFailure.Value.Detail + output.Tail.Detail));
        }
        var runnerDetail = runnerFailure is null ? null : runnerFailure.Value.Detail + output.Tail.Detail;

        var result = ReadStageExecutionResult(verified.Value.Root, verified.Value.Instructions.AttemptId);
        if (!result.IsOk && runnerDetail is not null)
        {
            return SiteResult<StageExecutionResult>.Refused(SiteFailure.Unclassified(
                $"Stage runner failed: {runnerDetail}; {result.Failure.Detail}"));
        }
        return result;
    }

    internal readonly record struct ProcessFailure(string Detail, bool WasNonZeroExit);

    /// <summary>
    /// The installed implementation source root holds exactly the files the
    /// instructions declare, byte for byte. Anything else in the directory is
    /// ignored (the identity is the declared list, never the folder).
    /// </summary>
    /// <param name="root">The installed source directory, including its non-linked ancestors.</param>
    /// <param name="declared">The relative paths, byte counts and SHA-256 digests of the required regular files.</param>
    /// <example><code>StageAttemptSite.VerifyInstalledSource(sourceRoot, instructions.ImplementationSourceFiles);</code></example>
    // CHG: the local installer shares this public check with Site execution;
    // validate the whole declaration before reading files, including every
    // directory through which its regular files would be reached.
    public static SiteResult<bool> VerifyInstalledSource(string root, IReadOnlyList<AttemptFile> declared, bool allowMissing = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return SiteResult<bool>.Permanent(
                    $"installed implementation source root {root} is not a directory on this Site");
            }
            var absoluteRoot = Path.GetFullPath(root);
            var rootPrefix = absoluteRoot.EndsWith(Path.DirectorySeparatorChar)
                ? absoluteRoot
                : absoluteRoot + Path.DirectorySeparatorChar;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var resolved = new List<(AttemptFile Declaration, string Path)>();
            foreach (var file in declared)
            {
                var relative = file.RelativePath;
                if (string.IsNullOrWhiteSpace(relative)
                    || Path.IsPathRooted(relative)
                    || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0')
                    || relative.Split('/').Any(component => component is "" or "." or ".."))
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source path \"{relative}\" must be a portable relative path");
                }
                if (file.ByteCount < 0 || !ValidSha256(file.ByteDigest))
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source \"{relative}\" requires a nonnegative byte count and a valid sha256 digest");
                }
                if (!paths.Add(relative))
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source declaration names \"{relative}\" twice");
                }
                var path = Path.GetFullPath(Path.Combine(absoluteRoot, FromSlash(relative)));
                if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source path \"{relative}\" escapes root {root}");
                }
                resolved.Add((file, path));
            }
            if ((!allowMissing && !Directory.Exists(absoluteRoot)) || IsSymbolicLink(absoluteRoot))
            {
                return SiteResult<bool>.Permanent(
                    $"installed implementation source root {root} is not a directory on this Site");
            }
            var checkedDirectories = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var directory in resolved.Select(file => Path.GetDirectoryName(file.Path)!).Prepend(absoluteRoot))
            {
                for (string? current = directory; current is not null && checkedDirectories.Add(current);
                     current = Path.GetDirectoryName(current))
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(current); }
                    catch (FileNotFoundException) when (allowMissing) { continue; }
                    catch (DirectoryNotFoundException) when (allowMissing) { continue; }
                    if ((attributes & FileAttributes.Directory) == 0
                        || (attributes & FileAttributes.ReparsePoint) != 0 || IsSymbolicLink(current))
                    {
                        return SiteResult<bool>.Permanent(
                            $"installed implementation source directory {current} must be a non-symlink directory");
                    }
                }
            }
            // CHG: installation observation may report absence, but still checks
            // every present member and its ancestors using these same rules.
            var complete = true;
            foreach (var (file, path) in resolved)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) when (allowMissing) { complete = false; continue; }
                catch (DirectoryNotFoundException) when (allowMissing) { complete = false; continue; }
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || IsSymbolicLink(path))
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source \"{file.RelativePath}\" under {root} must be a regular non-symlink file");
                }
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = input.Length;
                var observed = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(input));
                if (length != file.ByteCount || observed != file.ByteDigest)
                {
                    return SiteResult<bool>.Permanent(
                        $"installed implementation source \"{file.RelativePath}\" under {root} changed: expected {file.ByteCount} "
                        + $"bytes / {file.ByteDigest}, found {length} bytes / {observed}");
                }
            }
            return SiteResult<bool>.Ok(complete);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return SiteResult<bool>.Permanent(
                $"cannot verify installed implementation source under {root}: {error.Message}");
        }
    }

    /// <summary>
    /// Start one child, feed both its streams to <paramref name="consume"/> as
    /// they arrive, and kill it if the deadline passes. Every enumerated
    /// failure of the process boundary comes back through here: a refusal to
    /// start, a non-zero exit, a hang, and output that never stops.
    /// </summary>
    internal static ProcessFailure? RunBoundedStageCommand(
        ProcessStartInfo start,
        Action<ReadOnlySpan<byte>> consume,
        TimeSpan timeout)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.UseShellExecute = false;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new ProcessFailure("start Stage process: the operating system started no process", false);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessFailure("start Stage process: " + error.Message, false);
        }
        process.StandardInput.Close();

        var pump = new object();
        var readers = new[]
        {
            PumpStream(process.StandardOutput.BaseStream, consume, pump),
            PumpStream(process.StandardError.BaseStream, consume, pump),
        };
        if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the deadline and the kill; the wait below settles it.
            }
            process.WaitForExit();
            Task.WaitAll(readers);
            return new ProcessFailure("Stage process timed out", false);
        }
        Task.WaitAll(readers);
        return process.ExitCode == 0
            ? null
            : new ProcessFailure($"exit status {process.ExitCode}", true);
    }

    static Task PumpStream(Stream stream, Action<ReadOnlySpan<byte>> consume, object pump) => Task.Run(() =>
    {
        var buffer = new byte[8 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                return;
            }
            if (read <= 0)
            {
                return;
            }
            // One consumer, two pipes: the bounded tail and the progress line
            // assembler are not safe to enter twice at once.
            lock (pump)
            {
                consume(buffer.AsSpan(0, read));
            }
        }
    });

    readonly record struct RunnerProjection(string Runner, string Entrypoint);

    static SiteResult<RunnerProjection> ReadRunnerProjection(string runtime)
    {
        var requestBytes = ReadRegularFile(Path.Combine(runtime, FromSlash(SitePaths.StageRequestFile)));
        if (!requestBytes.IsOk)
        {
            return SiteResult<RunnerProjection>.Permanent(
                "cannot reopen Stage runner request: " + requestBytes.Failure.Detail);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBytes.Value);
        }
        catch (JsonException error)
        {
            return SiteResult<RunnerProjection>.Permanent("cannot read Stage runner projection: " + error.Message);
        }
        using (document)
        {
            var root = document.RootElement;
            var schema = OptionalString(root, "schema");
            var protocol = OptionalString(root, "protocol_version");
            if (schema != "fern.stage_runner_request.v1" || protocol != "fern.stage_runner.v1")
            {
                return SiteResult<RunnerProjection>.Permanent(
                    "Stage runner request has an unsupported schema or protocol");
            }
            var implementation = root.TryGetProperty("invocation", out var invocation)
                && invocation.ValueKind == JsonValueKind.Object
                && invocation.TryGetProperty("implementation", out var declared)
                && declared.ValueKind == JsonValueKind.Object
                    ? declared
                    : default;
            var runner = implementation.ValueKind == JsonValueKind.Object
                ? OptionalString(implementation, "runner") ?? string.Empty
                : string.Empty;
            var entrypoint = implementation.ValueKind == JsonValueKind.Object
                ? OptionalString(implementation, "entrypoint") ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(entrypoint))
            {
                return SiteResult<RunnerProjection>.Permanent("Stage runner request has no implementation entrypoint");
            }
            return SiteResult<RunnerProjection>.Ok(new RunnerProjection(runner, entrypoint));
        }
    }

    internal static SiteResult<StageExecutionResult> ReadStageExecutionResult(string runtime, string attemptId)
    {
        var reportBytes = ReadRegularFile(Path.Combine(runtime, FromSlash(SitePaths.StageReportFile)));
        if (!reportBytes.IsOk)
        {
            return SiteResult<StageExecutionResult>.Permanent(
                "cannot read Stage runner report: " + reportBytes.Failure.Detail);
        }
        string? schema;
        string? reportedAttemptId;
        try
        {
            using var document = JsonDocument.Parse(reportBytes.Value);
            schema = OptionalString(document.RootElement, "schema");
            reportedAttemptId = OptionalString(document.RootElement, "stage_attempt_id");
        }
        catch (JsonException error)
        {
            return SiteResult<StageExecutionResult>.Permanent(
                "cannot read Stage runner report projection: " + error.Message);
        }
        if (schema != "fern.stage_runner_report.v1" || reportedAttemptId != attemptId)
        {
            return SiteResult<StageExecutionResult>.Permanent(
                "Stage runner report does not answer these Attempt instructions");
        }
        return SiteResult<StageExecutionResult>.Ok(
            new StageExecutionResult("sha256:" + Sha256Hex(reportBytes.Value)));
    }

    // ---- publishing what came back ----

    /// <summary>
    /// Publish the runner report and every
    /// output byte under the existing remote Attempt return envelope. The
    /// destination appears complete or not at all, and republishing the same
    /// return is accepted while a different one is refused.
    /// </summary>
    public static SiteResult<StageReturnPackage> PackageStageReturn(
        string runtime,
        string destination,
        AttemptInstructions instructions,
        StageExecutionResult result,
        bool includeOutputs = true)
    {
        var validation = ValidateInstructions(instructions);
        if (!validation.IsOk)
        {
            return validation.RefusalAs<StageReturnPackage>();
        }
        if (!ValidSha256(result.RunnerReportContentDigest))
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "Stage execution result requires a runner-report digest");
        }
        var reportBytes = ReadRegularFile(Path.Combine(runtime, FromSlash(SitePaths.StageReportFile)));
        if (!reportBytes.IsOk)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "cannot reopen Stage runner report: " + reportBytes.Failure.Detail);
        }
        string? runnerSchema;
        string? runnerAttemptId;
        try
        {
            using var document = JsonDocument.Parse(reportBytes.Value);
            runnerSchema = OptionalString(document.RootElement, "schema");
            runnerAttemptId = OptionalString(document.RootElement, "stage_attempt_id");
        }
        catch (JsonException error)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "cannot read Stage runner report projection: " + error.Message);
        }
        if (runnerSchema != "fern.stage_runner_report.v1" || runnerAttemptId != instructions.AttemptId)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "Stage runner report does not answer these Attempt instructions");
        }
        if ("sha256:" + Sha256Hex(reportBytes.Value) != result.RunnerReportContentDigest)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "Stage runner report changed before return packaging");
        }

        var paths = new List<string> { SitePaths.StageReportFile };
        if (File.Exists(Path.Combine(runtime, FromSlash(SitePaths.RunnerOutputTailFile))))
        {
            paths.Add(SitePaths.RunnerOutputTailFile);
        }
        // CHG 2026-08-29 (Program 18, Site-C): a phase return carries the
        // Site's own reading of the sealed outputs and NOT the outputs; the
        // bytes stay on the Site until the cell ends.
        if (File.Exists(Path.Combine(runtime, FromSlash(SitePaths.SitePhaseReceiptFile))))
        {
            paths.Add(SitePaths.SitePhaseReceiptFile);
        }
        var outputsRoot = Path.Combine(runtime, "outputs");
        if (includeOutputs && PathExists(outputsRoot))
        {
            if (!Directory.Exists(outputsRoot) || IsSymbolicLink(outputsRoot))
            {
                return SiteResult<StageReturnPackage>.Permanent(
                    "Stage outputs root must be a non-symlink directory");
            }
            var walked = WalkRegularFiles(outputsRoot, runtime, "Stage output path");
            if (!walked.IsOk)
            {
                return walked.RefusalAs<StageReturnPackage>();
            }
            paths.AddRange(walked.Value);
        }
        paths.Sort(Utf8Ordinal.Compare);

        var files = new List<AttemptFile>(paths.Count);
        foreach (var relative in paths)
        {
            var bytes = ReadRegularFile(Path.Combine(runtime, FromSlash(relative)));
            if (!bytes.IsOk)
            {
                return bytes.RefusalAs<StageReturnPackage>();
            }
            files.Add(new AttemptFile(relative, bytes.Value.LongLength, "sha256:" + Sha256Hex(bytes.Value)));
        }
        var instructionsDigest = CanonicalInstructionsDigest(instructions);
        if (!instructionsDigest.IsOk)
        {
            return instructionsDigest.RefusalAs<StageReturnPackage>();
        }
        var report = new AttemptReturnReport(
            SitePaths.ReturnReportSchema,
            instructionsDigest.Value,
            instructions.AttemptId,
            result.RunnerReportContentDigest,
            files);

        var existing = VerifyStageReturnPackage(destination, instructions);
        if (existing.IsOk)
        {
            return SameReturnReport(existing.Value.Report, report)
                ? existing
                : SiteResult<StageReturnPackage>.Permanent("the existing Stage return contains a different report");
        }
        if (PathExists(Path.Combine(destination, SitePaths.ReturnReportFile)))
        {
            return existing;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(destination)) ?? ".";
        var prepared = PrepareTransportRoot(parent);
        if (!prepared.IsOk)
        {
            return prepared.RefusalAs<StageReturnPackage>();
        }
        var temporary = CreateTemporaryDirectory(
            parent, Path.GetFileName(Path.TrimEndingDirectorySeparator(destination)));
        var published = false;
        try
        {
            foreach (var file in files)
            {
                var bytes = ReadRegularFile(Path.Combine(runtime, FromSlash(file.RelativePath)));
                if (!bytes.IsOk)
                {
                    return bytes.RefusalAs<StageReturnPackage>();
                }
                var destinationPath = Path.Combine(temporary, FromSlash(file.RelativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                var written = WriteDurableFile(
                    destinationPath, bytes.Value, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (!written.IsOk)
                {
                    return written.RefusalAs<StageReturnPackage>();
                }
            }
            var reportFile = WriteDurableFile(
                Path.Combine(temporary, SitePaths.ReturnReportFile),
                GoJson.ReturnReportBytes(report, escapeHtml: true),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!reportFile.IsOk)
            {
                return reportFile.RefusalAs<StageReturnPackage>();
            }
            var staged = VerifyStageReturnPackage(temporary, instructions);
            if (!staged.IsOk)
            {
                return staged;
            }
            Directory.Move(temporary, destination);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
        return VerifyStageReturnPackage(destination, instructions);
    }

    /// <summary>
    /// Reopen one immutable Site return against the exact Home-issued
    /// instructions and check its full declared file set. A changed or extra
    /// file is named, never counted.
    /// </summary>
    public static SiteResult<StageReturnPackage> VerifyStageReturnPackage(
        string root,
        AttemptInstructions instructions)
    {
        var reportBytes = ReadRegularFile(Path.Combine(root, SitePaths.ReturnReportFile));
        if (!reportBytes.IsOk)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "cannot read Stage return report: " + reportBytes.Failure.Detail);
        }
        var decoded = DecodeOneObject(reportBytes.Value, ReadReturnReport, "Stage return report");
        if (!decoded.IsOk)
        {
            return decoded.RefusalAs<StageReturnPackage>();
        }
        var report = decoded.Value;
        var instructionsDigest = CanonicalInstructionsDigest(instructions);
        if (!instructionsDigest.IsOk)
        {
            return instructionsDigest.RefusalAs<StageReturnPackage>();
        }
        if (report.Schema != SitePaths.ReturnReportSchema
            || report.InstructionsDigest != instructionsDigest.Value
            || report.AttemptId != instructions.AttemptId)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "Stage return does not answer these exact Attempt instructions");
        }
        if (!ValidSha256(report.RunnerReportContentDigest) || report.Files.Count == 0)
        {
            return SiteResult<StageReturnPackage>.Permanent(
                "Stage return requires a runner-report digest and returned files");
        }

        var expectedPaths = new SortedSet<string>(Utf8Ordinal) { SitePaths.ReturnReportFile };
        var hasRunnerReport = false;
        foreach (var file in report.Files)
        {
            var fileValidation = ValidateAttemptFile(file);
            if (!fileValidation.IsOk)
            {
                return fileValidation.RefusalAs<StageReturnPackage>();
            }
            if (!expectedPaths.Add(file.RelativePath))
            {
                return SiteResult<StageReturnPackage>.Permanent(
                    $"Stage return repeats package path \"{file.RelativePath}\"");
            }
            hasRunnerReport = hasRunnerReport || file.RelativePath == SitePaths.StageReportFile;
            var bytes = ReadRegularFile(Path.Combine(root, FromSlash(file.RelativePath)));
            if (!bytes.IsOk)
            {
                return SiteResult<StageReturnPackage>.Permanent(
                    $"cannot read Stage return file \"{file.RelativePath}\": {bytes.Failure.Detail}");
            }
            if (bytes.Value.LongLength != file.ByteCount || "sha256:" + Sha256Hex(bytes.Value) != file.ByteDigest)
            {
                return SiteResult<StageReturnPackage>.Permanent(
                    $"Stage return file \"{file.RelativePath}\" changed");
            }
            if (file.RelativePath == SitePaths.StageReportFile)
            {
                string? schema = null;
                string? reportedAttemptId = null;
                try
                {
                    using var document = JsonDocument.Parse(bytes.Value);
                    schema = OptionalString(document.RootElement, "schema");
                    reportedAttemptId = OptionalString(document.RootElement, "stage_attempt_id");
                }
                catch (JsonException)
                {
                    // Falls into the same refusal as a report that binds the wrong Attempt.
                }
                if (schema != "fern.stage_runner_report.v1"
                    || reportedAttemptId != instructions.AttemptId
                    || "sha256:" + Sha256Hex(bytes.Value) != report.RunnerReportContentDigest)
                {
                    return SiteResult<StageReturnPackage>.Permanent(
                        "Stage runner report does not bind the returned report digest and Attempt");
                }
            }
        }
        if (!hasRunnerReport)
        {
            return SiteResult<StageReturnPackage>.Permanent("Stage return carries no Stage runner report");
        }
        var observedPaths = PackageFileSet(root);
        if (!observedPaths.IsOk)
        {
            return observedPaths.RefusalAs<StageReturnPackage>();
        }
        if (!observedPaths.Value.SequenceEqual(expectedPaths, StringComparer.Ordinal))
        {
            return SiteResult<StageReturnPackage>.Permanent("Stage return file set does not match its report");
        }
        return SiteResult<StageReturnPackage>.Ok(new StageReturnPackage(root, report));
    }

    // ---- admission ----

    static SiteResult<bool> ValidateInstructions(AttemptInstructions instructions)
    {
        if (instructions.Schema != SitePaths.InstructionsSchema)
        {
            return SiteResult<bool>.Permanent(
                $"remote instructions schema must be \"{SitePaths.InstructionsSchema}\"");
        }
        foreach (var (name, value) in new[]
        {
            ("matrix_id", instructions.MatrixId),
            ("coordinate_id", instructions.CoordinateId),
            ("attempt_id", instructions.AttemptId),
            ("run_hash", instructions.RunHash),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SiteResult<bool>.Permanent($"remote instructions {name} must be non-empty");
            }
        }
        if (!ValidSha256(instructions.ExecutionProfileRevision) || !ValidSha256(instructions.WorkerRequestContentDigest))
        {
            return SiteResult<bool>.Permanent(
                "remote instructions require full sha256 profile and worker-request digests");
        }
        if (instructions.Files.Count == 0)
        {
            return SiteResult<bool>.Permanent("remote instructions require at least one package file");
        }
        // v2 (2026-08-29): the Site runs an INSTALLED binary against an INSTALLED
        // source root; both are named here and verified where they are used.
        if (!ValidSha256(instructions.SiteBinaryDigest))
        {
            return SiteResult<bool>.Permanent("remote instructions require the installed Site binary's full sha256 (site_binary_sha256)");
        }
        if (string.IsNullOrWhiteSpace(instructions.ImplementationSourceRoot)
            || !instructions.ImplementationSourceRoot.StartsWith('/')
            || instructions.ImplementationSourceRoot.Contains("/../", StringComparison.Ordinal)
            || instructions.ImplementationSourceRoot.EndsWith("/..", StringComparison.Ordinal))
        {
            return SiteResult<bool>.Permanent("remote instructions require an absolute installed implementation_source_root");
        }
        if (instructions.ImplementationSourceFiles.Count == 0)
        {
            return SiteResult<bool>.Permanent("remote instructions require at least one implementation source file to verify");
        }
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceFile in instructions.ImplementationSourceFiles)
        {
            var sourceValidation = ValidateAttemptFile(sourceFile);
            if (!sourceValidation.IsOk)
            {
                return sourceValidation;
            }
            if (!sourcePaths.Add(sourceFile.RelativePath))
            {
                return SiteResult<bool>.Permanent(
                    $"remote instructions name implementation source \"{sourceFile.RelativePath}\" twice");
            }
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in instructions.Files)
        {
            var fileValidation = ValidateAttemptFile(file);
            if (!fileValidation.IsOk)
            {
                return fileValidation;
            }
            if (!paths.Add(file.RelativePath))
            {
                return SiteResult<bool>.Permanent($"remote instructions repeat package path \"{file.RelativePath}\"");
            }
        }
        // The Stage path and the older whole-worker path are different
        // contracts. One of them, never both, and never neither.
        var hasStage = paths.Contains(SitePaths.StageRequestFile);
        var hasWorker = paths.Contains(SitePaths.WorkerRequestFile);
        return (hasStage, hasWorker) switch
        {
            (true, false) => SiteResult<bool>.Ok(true),
            (false, true) => SiteResult<bool>.Permanent(
                "Stage runner Site path refuses whole-worker packages; use the existing worker route"),
            (true, true) => SiteResult<bool>.Permanent(
                "remote instructions must not mix worker and Stage runner requests"),
            _ => SiteResult<bool>.Permanent("remote instructions must carry a Stage runner request"),
        };
    }

    static SiteResult<bool> ValidateAttemptFile(AttemptFile file)
    {
        var clean = CleanSlashPath(file.RelativePath);
        if (string.IsNullOrWhiteSpace(file.RelativePath)
            || clean != file.RelativePath
            || clean == "."
            || clean.StartsWith("../", StringComparison.Ordinal)
            || file.RelativePath.StartsWith('/')
            || file.RelativePath.Contains('\\', StringComparison.Ordinal)
            || file.RelativePath.Contains(':', StringComparison.Ordinal)
            || file.RelativePath == SitePaths.InstructionsFile)
        {
            return SiteResult<bool>.Permanent(
                $"remote Attempt path \"{file.RelativePath}\" must be a portable package-relative payload path");
        }
        if (file.ByteCount <= 0)
        {
            return SiteResult<bool>.Permanent($"remote Attempt file \"{file.RelativePath}\" must not be empty");
        }
        if (!ValidSha256(file.ByteDigest))
        {
            return SiteResult<bool>.Permanent($"remote Attempt file \"{file.RelativePath}\" has an invalid sha256 digest");
        }
        return SiteResult<bool>.Ok(true);
    }

    // ---- filesystem ----

    static SiteResult<bool> PrepareTransportRoot(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(error.Message));
        }
        if (!Directory.Exists(directory) || IsSymbolicLink(directory))
        {
            return SiteResult<bool>.Permanent(
                $"remote Attempt transport root {directory} is not a regular non-symlink directory; "
                + "no package was written");
        }
        return SiteResult<bool>.Ok(true);
    }

    static string CreateTemporaryDirectory(string parent, string leaf)
    {
        var path = Path.Combine(parent, "." + leaf + ".tmp-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    static SiteResult<bool> WriteDurableFile(string path, byte[] contents, UnixFileMode mode)
    {
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, mode);
                }
                file.Write(contents, 0, contents.Length);
                file.Flush(flushToDisk: true);
            }
            return SiteResult<bool>.Ok(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(error.Message));
        }
    }

    internal static SiteResult<byte[]> ReadRegularFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return SiteResult<byte[]>.Refused(SiteFailure.Unclassified(
                    Directory.Exists(filePath)
                        ? $"{filePath} must be a regular non-symlink file"
                        : $"open {filePath}: no such file or directory"));
            }
            if (info.LinkTarget is not null)
            {
                return SiteResult<byte[]>.Refused(SiteFailure.Unclassified(
                    $"{filePath} must be a regular non-symlink file"));
            }
            return SiteResult<byte[]>.Ok(File.ReadAllBytes(filePath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<byte[]>.Refused(SiteFailure.Unclassified(error.Message));
        }
    }

    /// <summary>Every regular file below root, as slash paths relative to it, sorted by UTF-8 bytes.</summary>
    static SiteResult<IReadOnlyList<string>> PackageFileSet(string root)
    {
        var walked = WalkRegularFiles(root, root, "packaged Stage path");
        if (!walked.IsOk)
        {
            return walked;
        }
        var sorted = walked.Value.OrderBy(path => path, Utf8Ordinal).ToList();
        return SiteResult<IReadOnlyList<string>>.Ok(sorted);
    }

    static SiteResult<IReadOnlyList<string>> WalkRegularFiles(string root, string relativeTo, string subject)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return SiteResult<IReadOnlyList<string>>.Refused(SiteFailure.Unclassified(error.Message));
            }
            foreach (var entry in entries)
            {
                if (IsSymbolicLink(entry))
                {
                    return SiteResult<IReadOnlyList<string>>.Permanent($"{subject} {entry} is a symlink");
                }
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (!File.Exists(entry))
                {
                    return SiteResult<IReadOnlyList<string>>.Permanent($"{subject} {entry} is not a regular file");
                }
                found.Add(Path.GetRelativePath(relativeTo, entry).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        return SiteResult<IReadOnlyList<string>>.Ok(found);
    }

    internal static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path)
        || new FileInfo(path).LinkTarget is not null;

    internal static bool IsSymbolicLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return true;
        }
        return new DirectoryInfo(path).LinkTarget is not null;
    }

    // ---- small shared rules ----

    /// <summary>
    /// Ordinal by UTF-8 bytes, which is what Go's sort.Strings does. C#'s
    /// ordinal comparison is by UTF-16 code unit and disagrees above the basic
    /// plane, so file sets and digests use this one.
    /// </summary>
    internal static readonly IComparer<string> Utf8Ordinal = Comparer<string>.Create(static (left, right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right)));

    static bool SameInstructions(AttemptInstructions left, AttemptInstructions right) =>
        GoJson.InstructionsBytes(left, escapeHtml: true)
            .AsSpan()
            .SequenceEqual(GoJson.InstructionsBytes(right, escapeHtml: true));

    static bool SameReturnReport(AttemptReturnReport left, AttemptReturnReport right) =>
        GoJson.ReturnReportBytes(left, escapeHtml: true)
            .AsSpan()
            .SequenceEqual(GoJson.ReturnReportBytes(right, escapeHtml: true));

    internal static bool ValidSha256(string? value) =>
        value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string FromSlash(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Go's path.Clean, for the portability check on a declared package path.</summary>
    static string CleanSlashPath(string value)
    {
        if (value.Length == 0)
        {
            return ".";
        }
        var rooted = value.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in value.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count > 0 && segments[^1] != "..")
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                    else if (!rooted)
                    {
                        segments.Add("..");
                    }
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }
        var joined = string.Join('/', segments);
        if (rooted)
        {
            return "/" + joined;
        }
        return joined.Length == 0 ? "." : joined;
    }

    static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Decode exactly one JSON object with no unknown field, the way Go's
    /// DisallowUnknownFields decoder followed by a second Decode does. Trailing
    /// content after the object is a refusal, not ignored.
    /// </summary>
    static SiteResult<T> DecodeOneObject<T>(
        byte[] bytes,
        Func<JsonElement, SiteResult<T>> read,
        string subject)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        JsonDocument document;
        try
        {
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException error)
        {
            return SiteResult<T>.Permanent($"cannot decode {subject}: {error.Message}");
        }
        using (document)
        {
            if (reader.BytesConsumed != bytes.LongLength && HasTrailingContent(bytes, reader.BytesConsumed))
            {
                return SiteResult<T>.Permanent($"{subject} must contain one JSON object");
            }
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SiteResult<T>.Permanent($"cannot decode {subject}: it is not a JSON object");
            }
            return read(document.RootElement);
        }
    }

    static bool HasTrailingContent(byte[] bytes, long consumed)
    {
        for (var index = consumed; index < bytes.LongLength; index++)
        {
            if (bytes[index] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return true;
            }
        }
        return false;
    }

    static SiteResult<AttemptInstructions> ReadInstructions(JsonElement root)
    {
        var known = new[]
        {
            "schema", "matrix_id", "coordinate_id", "attempt_id", "run_hash",
            "execution_profile_revision", "worker_request_content_digest", "files",
            "site_binary_sha256", "implementation_source_root", "implementation_source_files",
        };
        var unknown = UnknownField(root, known);
        if (unknown is not null)
        {
            return SiteResult<AttemptInstructions>.Permanent(
                $"cannot decode packaged Stage instructions: unknown field \"{unknown}\"");
        }
        var sourceFiles = ReadFiles(root, "packaged Stage instructions (implementation_source_files)", "implementation_source_files");
        if (!sourceFiles.IsOk)
        {
            return sourceFiles.RefusalAs<AttemptInstructions>();
        }
        var files = ReadFiles(root, "packaged Stage instructions");
        if (!files.IsOk)
        {
            return files.RefusalAs<AttemptInstructions>();
        }
        return SiteResult<AttemptInstructions>.Ok(new AttemptInstructions(
            OptionalString(root, "schema") ?? string.Empty,
            OptionalString(root, "matrix_id") ?? string.Empty,
            OptionalString(root, "coordinate_id") ?? string.Empty,
            OptionalString(root, "attempt_id") ?? string.Empty,
            OptionalString(root, "run_hash") ?? string.Empty,
            OptionalString(root, "execution_profile_revision") ?? string.Empty,
            OptionalString(root, "worker_request_content_digest") ?? string.Empty,
            files.Value,
            OptionalString(root, "site_binary_sha256") ?? string.Empty,
            OptionalString(root, "implementation_source_root") ?? string.Empty,
            sourceFiles.Value));
    }

    static SiteResult<AttemptReturnReport> ReadReturnReport(JsonElement root)
    {
        var known = new[]
        {
            "schema", "instructions_digest", "attempt_id", "worker_result_content_digest", "files",
        };
        var unknown = UnknownField(root, known);
        if (unknown is not null)
        {
            return SiteResult<AttemptReturnReport>.Permanent(
                $"cannot decode Stage return report: unknown field \"{unknown}\"");
        }
        var files = ReadFiles(root, "Stage return report");
        if (!files.IsOk)
        {
            return files.RefusalAs<AttemptReturnReport>();
        }
        return SiteResult<AttemptReturnReport>.Ok(new AttemptReturnReport(
            OptionalString(root, "schema") ?? string.Empty,
            OptionalString(root, "instructions_digest") ?? string.Empty,
            OptionalString(root, "attempt_id") ?? string.Empty,
            OptionalString(root, "worker_result_content_digest") ?? string.Empty,
            files.Value));
    }

    static SiteResult<IReadOnlyList<AttemptFile>> ReadFiles(JsonElement root, string subject) =>
        ReadFiles(root, subject, "files");

    static SiteResult<IReadOnlyList<AttemptFile>> ReadFiles(JsonElement root, string subject, string member)
    {
        var files = new List<AttemptFile>();
        if (!root.TryGetProperty(member, out var declared) || declared.ValueKind == JsonValueKind.Null)
        {
            return SiteResult<IReadOnlyList<AttemptFile>>.Ok(files);
        }
        if (declared.ValueKind != JsonValueKind.Array)
        {
            return SiteResult<IReadOnlyList<AttemptFile>>.Permanent($"cannot decode {subject}: files is not an array");
        }
        foreach (var entry in declared.EnumerateArray())
        {
            var unknown = UnknownField(entry, ["relative_path", "byte_count", "byte_digest"]);
            if (unknown is not null)
            {
                return SiteResult<IReadOnlyList<AttemptFile>>.Permanent(
                    $"cannot decode {subject}: unknown field \"{unknown}\"");
            }
            var byteCount = entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("byte_count", out var count)
                && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt64(out var parsed)
                    ? parsed
                    : 0;
            files.Add(new AttemptFile(
                OptionalString(entry, "relative_path") ?? string.Empty,
                byteCount,
                OptionalString(entry, "byte_digest") ?? string.Empty));
        }
        return SiteResult<IReadOnlyList<AttemptFile>>.Ok(files);
    }

    static string? UnknownField(JsonElement element, IReadOnlyList<string> known)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                return property.Name;
            }
        }
        return null;
    }
}
