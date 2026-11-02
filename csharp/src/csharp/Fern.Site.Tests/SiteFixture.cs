using System.Security.Cryptography;
using System.Text;

namespace Fern.Site.Tests;

/// <summary>
/// The package, runtime and child-process fixtures every Site test builds on.
///
/// The Go tests re-executed their own test binary as the packaged runner,
/// steered by environment variables. C# has no equivalent trick, and it does
/// not need one: a small shell script IS a separate program, so these fixtures
/// exercise the same real process boundary — a fork, two pipes, an exit status
/// — without the harness. Where a Go test relied on one of the five child modes
/// its TestMain provided, the same behaviour is written here as a script.
/// </summary>
static class SiteFixture
{
    internal const string ScientificResultDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A fresh directory that removes itself at the end of the test.</summary>
    internal sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "fern-site-tests-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string Join(params string[] parts) =>
            System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    internal static string Sha256(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Write one instructions file declaring exactly one payload file, and
    /// optionally that file itself. Withholding it is how a package is made
    /// defective without touching the declaration.
    /// </summary>
    internal static void WriteInstructions(
        string root,
        string requestPath,
        byte[] request,
        bool writeRequest,
        string? sourceRoot = null,
        IReadOnlyList<AttemptFile>? sourceFiles = null)
    {
        var instructions = Instructions(requestPath, request, sourceRoot, sourceFiles);
        File.WriteAllBytes(
            Path.Combine(root, SitePaths.InstructionsFile),
            InstructionsBytes(instructions));
        if (writeRequest)
        {
            WriteRuntimeFile(root, requestPath, request);
        }
    }

    internal static AttemptInstructions Instructions(
        string requestPath, byte[] request, string? sourceRoot = null, IReadOnlyList<AttemptFile>? sourceFiles = null) =>
        new(
            SitePaths.InstructionsSchema,
            "matrix-1",
            "coordinate-1",
            "attempt-1",
            "run-1",
            "sha256:" + new string('1', 64),
            "sha256:" + new string('2', 64),
            [new AttemptFile(requestPath, request.LongLength, Sha256(request))],
            // v2 (2026-08-29): the Site binary and source are installed, not packaged.
            "sha256:" + new string('3', 64),
            sourceRoot ?? "/opt/fern/implementation/fixture",
            sourceFiles ?? [new AttemptFile("python/fern_train/worker.py", 1, "sha256:" + new string('4', 64))]);

    /// <summary>Write one complete instructions record as the file the Site reads (Program 18 phase fixtures).</summary>
    internal static void WriteInstructionsRecord(string root, AttemptInstructions instructions) =>
        File.WriteAllBytes(Path.Combine(root, SitePaths.InstructionsFile), InstructionsBytes(instructions));

    /// <summary>Add declared files to an instructions file already on disk, as the Go fixtures did.</summary>
    internal static void DeclareAdditionalFiles(string root, params AttemptFile[] files)
    {
        var path = Path.Combine(root, SitePaths.InstructionsFile);
        var current = ReadInstructions(path);
        File.WriteAllBytes(path, InstructionsBytes(current with { Files = [.. current.Files, .. files] }));
    }

    internal static void WriteRuntimeFile(string root, string relativePath, byte[] bytes)
    {
        var file = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    /// <summary>
    /// A packaged runner that behaves like the Go fixture's child: <c>run</c>
    /// writes the runner report, appends one line to logs/runner-started so a
    /// second spawn would be visible, and records PYTHONPATH when it is set;
    /// <c>check-return</c> writes the checked receipt.
    /// </summary>
    internal static byte[] RunnerScript(string attemptId = "attempt-1") => Script($$"""
        if [ "$mode" = "check-return" ]; then
          mkdir -p "$workspace/report"
          printf '%s' '{"schema":"fern.stage_receipt.v1","result_digest":"{{ScientificResultDigest}}"}' \
            > "$workspace/report/stage_receipt.v1.json"
        else
          mkdir -p "$workspace/report" "$workspace/logs"
          printf '%s' '{"schema":"fern.stage_runner_report.v1","stage_attempt_id":"{{attemptId}}"}' \
            > "$workspace/report/stage_runner_report.v1.json"
          printf 'run\n' >> "$workspace/logs/runner-started"
          if [ -n "$PYTHONPATH" ]; then printf '%s' "$PYTHONPATH" > "$workspace/logs/python-path"; fi
        fi
        """);

    /// <summary>The four enumerated process-boundary behaviours, as their own programs.</summary>
    internal static byte[] BoundaryScript(string mode) => mode switch
    {
        "success" => Script("""
            echo "boundary success"
            """),
        "failure" => Script("""
            echo "boundary declared failure" >&2
            exit 17
            """),
        "hang" => Script("""
            echo "boundary entered but never returned" >&2
            while true; do sleep 3600; done
            """),
        "flood" => Script($$"""
            awk 'BEGIN { for (i = 0; i < {{2 * SitePaths.MaximumCommandOutputDetailBytes}}; i++) printf "x" }' >&2
            printf 'final boundary failure' >&2
            exit 19
            """),
        "progress" => Script("""
            echo "trainer banner that is not protocol data"
            echo 'FERN_TRAIN_PROGRESS {"schema":"fern.training_progress.v1","kind":"training_epoch","current":10,"total":100}'
            """),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "no such boundary child"),
    };

    /// <summary>Every script takes the Site's argv shape, so the body can just read $mode and $workspace.</summary>
    static byte[] Script(string body) => Encoding.UTF8.GetBytes(
        "#!/bin/sh\nset -e\nworkspace=\"\"\nmode=\"$1\"\n"
        + "if [ $# -gt 0 ]; then shift; fi\n"
        + "while [ $# -gt 0 ]; do case \"$1\" in --workspace) workspace=\"$2\"; shift 2;; *) shift;; esac; done\n"
        + body + "\n");

    /// <summary>
    /// Set a file's Unix mode, refusing on Windows rather than pretending.
    /// A Worker Site is a Unix machine: the runner is spawned by exec, and a
    /// package whose runner is not executable cannot run at all.
    /// </summary>
    internal static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the Worker Site package boundary is a Unix boundary");
        }
        File.SetUnixFileMode(path, mode);
    }

    internal static void MakeExecutable(string path) =>
        SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>The file's mode as the golden records it, for example 0o700.</summary>
    internal static string OctalMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the Worker Site package boundary is a Unix boundary");
        }
        return "0o" + Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(3, '0');
    }

    /// <summary>Write one executable program into a package and declare it.</summary>
    internal static void PlaceExecutable(string root, string relativePath, byte[] program)
    {
        WriteRuntimeFile(root, relativePath, program);
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        MakeExecutable(path);
        DeclareAdditionalFiles(root, new AttemptFile(relativePath, program.LongLength, Sha256(program)));
    }

    /// <summary>
    /// A complete package whose declared runner is a real executable: the
    /// shortest thing that can actually run to a checked receipt.
    /// </summary>
    internal static string RunnableRustStagePackage(TemporaryDirectory home)
    {
        var root = home.Join("outbound");
        Directory.CreateDirectory(root);
        var request = Encoding.UTF8.GetBytes(
            """
            {"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1",
            "invocation":{"implementation":{"runner":"rust_binary","entrypoint":"fern-stage-runner"}}}
            """.ReplaceLineEndings(string.Empty));
        WriteInstructions(root, SitePaths.StageRequestFile, request, writeRequest: true);
        PlaceExecutable(root, SitePaths.PackagedRunnerFile, RunnerScript());
        return root;
    }

    // ---- reading an instructions file back, for fixtures that amend one ----

    static byte[] InstructionsBytes(AttemptInstructions instructions)
    {
        var text = new StringBuilder();
        text.Append('{');
        Append(text, "schema", instructions.Schema);
        text.Append(',');
        Append(text, "matrix_id", instructions.MatrixId);
        text.Append(',');
        Append(text, "coordinate_id", instructions.CoordinateId);
        text.Append(',');
        Append(text, "attempt_id", instructions.AttemptId);
        text.Append(',');
        Append(text, "run_hash", instructions.RunHash);
        text.Append(',');
        Append(text, "execution_profile_revision", instructions.ExecutionProfileRevision);
        text.Append(',');
        Append(text, "worker_request_content_digest", instructions.WorkerRequestContentDigest);
        text.Append(",\"files\":[");
        for (var index = 0; index < instructions.Files.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }
            text.Append('{');
            Append(text, "relative_path", instructions.Files[index].RelativePath);
            text.Append(",\"byte_count\":").Append(instructions.Files[index].ByteCount).Append(',');
            Append(text, "byte_digest", instructions.Files[index].ByteDigest);
            text.Append('}');
        }
        text.Append("],");
        Append(text, "site_binary_sha256", instructions.SiteBinaryDigest);
        text.Append(',');
        Append(text, "implementation_source_root", instructions.ImplementationSourceRoot);
        text.Append(",\"implementation_source_files\":[");
        for (var index = 0; index < instructions.ImplementationSourceFiles.Count; index++)
        {
            var file = instructions.ImplementationSourceFiles[index];
            if (index > 0) text.Append(',');
            text.Append('{');
            Append(text, "relative_path", file.RelativePath);
            text.Append(",\"byte_count\":").Append(file.ByteCount);
            text.Append(',');
            Append(text, "byte_digest", file.ByteDigest);
            text.Append('}');
        }
        text.Append("]}");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    static void Append(StringBuilder text, string name, string value) =>
        text.Append('"').Append(name).Append("\":\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');

    static AttemptInstructions ReadInstructions(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return new AttemptInstructions(
            root.GetProperty("schema").GetString()!,
            root.GetProperty("matrix_id").GetString()!,
            root.GetProperty("coordinate_id").GetString()!,
            root.GetProperty("attempt_id").GetString()!,
            root.GetProperty("run_hash").GetString()!,
            root.GetProperty("execution_profile_revision").GetString()!,
            root.GetProperty("worker_request_content_digest").GetString()!,
            root.GetProperty("files").EnumerateArray().Select(file => new AttemptFile(
                file.GetProperty("relative_path").GetString()!,
                file.GetProperty("byte_count").GetInt64(),
                file.GetProperty("byte_digest").GetString()!)).ToList(),
            root.TryGetProperty("site_binary_sha256", out var digest) ? digest.GetString()! : string.Empty,
            root.TryGetProperty("implementation_source_root", out var sourceRoot) ? sourceRoot.GetString()! : string.Empty,
            root.TryGetProperty("implementation_source_files", out var sourceFiles)
                ? sourceFiles.EnumerateArray().Select(file => new AttemptFile(
                    file.GetProperty("relative_path").GetString()!,
                    file.GetProperty("byte_count").GetInt64(),
                    file.GetProperty("byte_digest").GetString()!)).ToList()
                : []);
    }
}
