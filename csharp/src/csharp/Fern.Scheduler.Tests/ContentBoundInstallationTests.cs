using System.Diagnostics;
using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// An installed binary set is bound to CONTENT, never to a directory.
///
/// Until 2026-08-29 the installation receipt recorded `workspace_root`, serve
/// refused when that directory was gone, and three separate things were read
/// out of it while the set ran: the Registry migration chain, the execution
/// profiles, and the content-addressed Stage source store. So an installation
/// silently depended on the worktree it was built from — delete it and the
/// scheduler stops; leave it a commit behind and it refuses the database it
/// was installed to serve. That last one happened: a receipted worktree held
/// 56 migrations while its database held 57, and the Stage worker refused.
///
/// The current schema now travels inside Fern.Registry, and the receipt names the
/// stores it actually reads. What follows is the promise stated as a test:
/// delete the install tree, keep the stores, and the set still verifies.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Collection(TimingSensitiveCollection.Name)]
public sealed class ContentBoundInstallationTests
{
    /// <summary>The binary carries the exact current schema source bytes; it never reads a runtime SQL directory.</summary>
    [Fact]
    public void TheEmbeddedSchemaHasTheAuthoredBytesAndIdentity()
    {
        var path = Path.Combine(TestRegistry.RepoRoot(), "src", "csharp", "Fern.Registry", "Schema.sql");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(bytes), EmbeddedRegistrySchema.Sql);
        // CHG 2026-09-26: the stored label is the schema version, not a SHA-256
        // of these bytes; a Home is current by its version and its objects.
        Assert.Equal($"registry-schema:version-{RegistrySchemaTransforms.Current}", EmbeddedRegistrySchema.Identity);
    }

    /// <summary>
    /// THE PROMISE: install from one directory, delete that directory, and the
    /// installation still verifies — given the stores, which live where the
    /// receipt says and not where the install happened.
    ///
    /// The stores are deliberately placed OUTSIDE the install tree here, which
    /// is the arrangement the old receipt could not describe at all.
    /// </summary>
    [Fact]
    public void AnInstalledSetVerifiesAfterItsInstallTreeIsDeleted()
    {
        var stores = Directory.CreateTempSubdirectory("fern-content-bound-stores-").FullName;
        var installTree = Directory.CreateTempSubdirectory("fern-content-bound-tree-").FullName;
        try
        {
            var (deployRoot, profilesRoot, sshHelper) = ProbeFixtures.WriteControlStores(stores);
            var registry = Path.Combine(stores, "lifecycle.db");
            File.WriteAllText(registry, "fixture registry");

            var outcome = ControlPlaneInstaller.Install(
                InstallFixtures.ConfigFor(installTree, registry, deployRoot, profilesRoot, sshHelper));
            var installed = UnionCase.Of<Accepted<Installation>>(outcome).Value;

            // The published binaries and the receipt live under the deployment
            // root, which is under `stores`; the tree they were installed FROM
            // is now removed entirely.
            Directory.Delete(installTree, recursive: true);
            Assert.False(Directory.Exists(installTree));

            var verified = InstallationVerification.VerifyMaterialized(
                installed.ReceiptPath, registry);

            var accepted = Assert.IsType<InstallationVerificationOutcome.Accepted>(verified).Installation;
            Assert.Equal(Path.GetFullPath(deployRoot), accepted.DeployRoot);
            Assert.Equal(Path.GetFullPath(profilesRoot), accepted.ProfilesRoot);
            Assert.Equal(Path.GetFullPath(sshHelper), accepted.SshHelper);

            // And the receipt says nothing about a workspace.
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(installed.ReceiptPath));
            Assert.False(receipt.RootElement.TryGetProperty("workspace_root", out _));
            Assert.Equal(
                ControlPlaneInstallationContract.ReceiptSchema,
                receipt.RootElement.GetProperty("schema").GetString());
        }
        finally
        {
            Directory.Delete(stores, recursive: true);
            if (Directory.Exists(installTree))
            {
                Directory.Delete(installTree, recursive: true);
            }
        }
    }

    /// <summary>
    /// The installed operator members must be complete executable sets, not
    /// merely their invoking framework-dependent apphosts under digest names.
    ///
    /// This starts from the precise broken shape: a normal <c>dotnet build</c>
    /// creates a <c>fern-run</c> apphost beside <c>fern-run.dll</c>. The public
    /// installation command must publish its own self-contained <c>fern-run</c>
    /// and distinct <c>fern-experiment</c> members. Once the command returns,
    /// this test removes both the caller's build directory and the explicit
    /// source-root path it installed from, then starts the two receipt-named files
    /// from an unrelated directory. A copied apphost fails at that point; a
    /// published executable set reaches its command boundary.
    /// </summary>
    [Fact]
    public void InstalledOperatorMembersExecuteAfterTheirCallerBuildAndInstallationSourceAreDeleted()
    {
        var root = Directory.CreateTempSubdirectory("fern-installed-operator-").FullName;
        var source = Path.Combine(root, "source");
        var callerBuild = Path.Combine(root, "framework-dependent-caller-build");
        try
        {
            LinkInstallSource(TestRegistry.RepoRoot(), source);
            var defaultBuildState = SnapshotDefaultBuildState(source);
            var callerProject = Path.Combine(source, "src", "csharp", "Fern.Run.Cli", "Fern.Run.Cli.csproj");
            var callerArtifacts = Path.Combine(root, "caller-artifacts");
            RunChecked(
                "dotnet",
                [
                    "build", "--disable-build-servers", "--nologo", "-c", "Release",
                    "--artifacts-path", callerArtifacts,
                    "-o", callerBuild,
                    callerProject,
                ],
                source,
                "build framework-dependent fern-run caller");
            Assert.True(Directory.Exists(callerArtifacts), $"caller artifacts root missing {callerArtifacts}");
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(callerArtifacts, "*", SearchOption.AllDirectories));
            AssertDefaultBuildStateUnchanged(source, defaultBuildState);

            var caller = Path.Combine(callerBuild, "fern-run");
            Assert.True(File.Exists(caller), $"framework-dependent caller missing {caller}");
            Assert.True(
                File.Exists(Path.Combine(callerBuild, "fern-run.dll")),
                "the falsifier must begin from an apphost whose adjacent DLL would disappear");

            var stores = Path.Combine(root, "stores");
            var (deployRoot, profilesRoot, sshHelper) = ProbeFixtures.WriteControlStores(stores);
            var registry = Path.Combine(stores, "lifecycle.db");
            File.WriteAllText(registry, "fixture registry");
            var deployment = Path.Combine(stores, "installed-set");
            var unitConfig = Path.Combine(stores, "xdg");
            var processTemp = Directory.CreateDirectory(Path.Combine(root, "process-temp")).FullName;

            var installation = Run(
                caller,
                [
                    InstallCli.Verb,
                    "--registry", registry,
                    "--work-root", Path.Combine(stores, "work"),
                    "--deployment-root", deployment,
                    "--deploy-root", deployRoot,
                    "--profiles-root", profilesRoot,
                    "--ssh-helper", sshHelper,
                ],
                // CHG 2026-09-13: this test installs from the COMMITTED source,
                // which it must: its tree is a set of symbolic links, and the
                // worktree path refuses to read a build source through a link.
                // So it passes only when every build source matches HEAD, and it
                // fails while another lane holds uncommitted C#.
                root,
                new Dictionary<string, string?>
                {
                    ["FERN_SOURCE_ROOT"] = source,
                    ["XDG_CONFIG_HOME"] = unitConfig,
                    ["TMPDIR"] = processTemp,
                });
            Assert.True(
                installation.ExitCode == 0,
                $"installer exited {installation.ExitCode}:\n{installation.Stderr}\n{installation.Stdout}");
            Assert.Empty(
                Directory.EnumerateDirectories(processTemp, "fern-control-build-*", SearchOption.TopDirectoryOnly));
            AssertDefaultBuildStateUnchanged(source, defaultBuildState);

            var receiptPath = PrintedPath(installation.Stdout, "installation receipt ");
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
            var fields = receipt.RootElement;
            var run = fields.GetProperty("run_binary_path").GetString()!;
            var experiment = fields.GetProperty("experiment_binary_path").GetString()!;
            Assert.True(File.Exists(run), $"receipt-named fern-run member missing {run}");
            Assert.True(File.Exists(experiment), $"receipt-named fern-experiment member missing {experiment}");

            DeleteDisposableDirectory(root, callerBuild, "framework-dependent caller build");
            DeleteDisposableDirectory(root, callerArtifacts, "framework-dependent caller artifacts");
            DeleteLinkedInstallSource(source);
            Assert.False(Directory.Exists(callerBuild));
            Assert.False(Directory.Exists(callerArtifacts));
            Assert.False(Directory.Exists(source));

            var isolated = Directory.CreateDirectory(Path.Combine(root, "without-source-or-build")).FullName;
            var runVersion = Run(
                run,
                ["version"],
                isolated,
                new Dictionary<string, string?>
                {
                    ["FERN_SOURCE_ROOT"] = source,
                    ["FERN_BUILT_FROM_SOURCE_ARCHIVE_SHA256"] = "runtime-shell-is-not-build-provenance",
                });
            Assert.Equal(0, runVersion.ExitCode);
            using var version = JsonDocument.Parse(runVersion.Stdout);
            Assert.Equal("fern.binary_version.v1", version.RootElement.GetProperty("schema").GetString());
            // CHG: the installed member keeps the selected committed source
            // identity after both the source extraction and caller disappear.
            Assert.False(version.RootElement.GetProperty("built_from_dirty_tree").GetBoolean());
            var commit = version.RootElement.GetProperty("built_from_commit").GetString()!;
            var archive = version.RootElement.GetProperty("source_archive_sha256").GetString()!;
            Assert.Equal(40, commit.Length);
            Assert.Equal(64, archive.Length);
            Assert.Contains("committed source " + commit, installation.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime-shell", archive, StringComparison.Ordinal);

            var experimentUsage = Run(
                experiment,
                [],
                isolated,
                new Dictionary<string, string?> { ["FERN_SOURCE_ROOT"] = source });
            Assert.Equal(2, experimentUsage.ExitCode);
            Assert.Contains("usage: fern-experiment", experimentUsage.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(source))
            {
                DeleteLinkedInstallSource(source);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A missing store is refused by NAME. The old refusals said "receipted
    /// Fern workspace root ... is not a real directory", which named a
    /// directory nothing should have depended on and left an operator guessing
    /// which of its contents was actually wanted.
    /// </summary>
    [Fact]
    public void VerificationRefusesByNamingTheStoreThatIsMissing()
    {
        var stores = Directory.CreateTempSubdirectory("fern-content-bound-missing-").FullName;
        var installTree = Directory.CreateTempSubdirectory("fern-content-bound-missing-tree-").FullName;
        try
        {
            var (deployRoot, profilesRoot, sshHelper) = ProbeFixtures.WriteControlStores(stores);
            var registry = Path.Combine(stores, "lifecycle.db");
            File.WriteAllText(registry, "fixture registry");
            var installed = UnionCase.Of<Accepted<Installation>>(ControlPlaneInstaller.Install(
                InstallFixtures.ConfigFor(installTree, registry, deployRoot, profilesRoot, sshHelper))).Value;

            Directory.Delete(profilesRoot, recursive: true);

            var verified = InstallationVerification.VerifyMaterialized(
                installed.ReceiptPath, registry);

            var refused = Assert.IsType<InstallationVerificationOutcome.Refused>(verified);
            Assert.Contains("execution profiles directory", refused.Detail, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(profilesRoot), refused.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(stores, recursive: true);
            Directory.Delete(installTree, recursive: true);
        }
    }

    static void LinkInstallSource(string repository, string destination)
    {
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, "python", "fern_train"));
        File.CreateSymbolicLink(
            Path.Combine(destination, "python", "fern_train", "worker.py"),
            Path.Combine(repository, "python", "fern_train", "worker.py"));
        Directory.CreateSymbolicLink(Path.Combine(destination, ".git"), Path.Combine(repository, ".git"));
        // CHG 2026-09-26: the C# tree moved to src/csharp/; src/ is a real
        // directory here so only the C# tree is linked, as csharp/ was before.
        Directory.CreateDirectory(Path.Combine(destination, "src"));
        Directory.CreateSymbolicLink(Path.Combine(destination, "src", "csharp"), Path.Combine(repository, "src", "csharp"));
        // CHG 2026-09-13: the workflow pins are Roslyn AdditionalFiles of every
        // project that declares a step, referenced as csharp/../docs/how-to,
        // so a source tree without them cannot compile.
        Directory.CreateSymbolicLink(Path.Combine(destination, "docs"), Path.Combine(repository, "docs"));
        // tools/fern-control and tools/cardbox-go were retired with 8dca8708; the
        // installer reads neither, and a dangling directory link cannot be deleted
        // by the cleanup below.
    }

    static void DeleteLinkedInstallSource(string source)
    {
        // No recursive delete is allowed here. Each target is a link made by
        // LinkInstallSource, and Directory.Delete removes the link itself;
        // a target unexpectedly becoming a real directory makes this refuse
        // instead of touching the reviewed checkout.
        DeleteSymbolicLink(Path.Combine(source, "python", "fern_train", "worker.py"), isDirectory: false);
        DeleteSymbolicLink(Path.Combine(source, ".git"), isDirectory: true);
        DeleteSymbolicLink(Path.Combine(source, "src", "csharp"), isDirectory: true);
        Directory.Delete(Path.Combine(source, "src"));
        DeleteSymbolicLink(Path.Combine(source, "docs"), isDirectory: true);
        Directory.Delete(Path.Combine(source, "python", "fern_train"));
        Directory.Delete(Path.Combine(source, "python"));
        Directory.Delete(source);
    }

    static void DeleteDisposableDirectory(string root, string path, string label)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        Assert.True(
            fullPath.StartsWith(prefix, StringComparison.Ordinal),
            $"{label} cleanup target {fullPath} must stay under disposable test root {fullRoot}");
        Assert.True(Directory.Exists(fullPath), $"{label} cleanup target missing {fullPath}");
        Directory.Delete(fullPath, recursive: true);
    }

    static IReadOnlyDictionary<string, BuildPathSnapshot?> SnapshotDefaultBuildState(string source)
    {
        var csharpRoot = Path.Combine(source, "src", "csharp");
        var paths = Directory.EnumerateFiles(csharpRoot, "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project =>
            {
                var directory = Path.GetDirectoryName(project)!;
                return new[]
                {
                    Path.Combine(directory, "bin"),
                    Path.Combine(directory, "obj"),
                    Path.Combine(directory, "obj", "project.assets.json"),
                };
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(source, path), SnapshotBuildPath, StringComparer.Ordinal);
        return paths;
    }

    static BuildPathSnapshot? SnapshotBuildPath(string path)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new BuildPathSnapshot(file.Exists, IsDirectory: false, file.Length, file.LastWriteTimeUtc);
        }
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new BuildPathSnapshot(Exists: true, IsDirectory: true, Size: null, directory.LastWriteTimeUtc);
        }
        return null;
    }

    static void AssertDefaultBuildStateUnchanged(
        string source,
        IReadOnlyDictionary<string, BuildPathSnapshot?> before)
    {
        foreach (var (relative, expected) in before)
        {
            var current = SnapshotBuildPath(Path.Combine(source, relative));
            Assert.Equal(expected, current);
        }
    }

    static void DeleteSymbolicLink(string path, bool isDirectory)
    {
        FileSystemInfo link = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        Assert.True(
            link.LinkTarget is not null,
            $"{path} must still be a symbolic link; refusing to delete a checkout path through the falsifier cleanup");
        if (isDirectory)
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    static string PrintedPath(string output, string prefix)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(line), $"operator output has no {prefix} path:\n{output}");
        return line![prefix.Length..];
    }

    static void RunChecked(string program, IReadOnlyList<string> arguments, string workingDirectory, string purpose)
    {
        var result = Run(program, arguments, workingDirectory, new Dictionary<string, string?>());
        Assert.True(result.ExitCode == 0, $"{purpose} exited {result.ExitCode}:\n{result.Stderr}\n{result.Stdout}");
    }

    static ProcessResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            child.StartInfo.ArgumentList.Add(argument);
        }
        // A reused MSBuild node inherits this test's redirected stdout/stderr
        // handles after `dotnet build` exits. ReadToEndAsync then waits forever
        // for an EOF the resident node keeps open. This test owns disposable
        // builds and needs every child boundary to close before it deletes the
        // source tree, so node reuse is explicitly disabled for the whole
        // installer subprocess family.
        child.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (key, value) in environment)
        {
            child.StartInfo.Environment[key] = value;
        }

        child.Start();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(600_000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new TimeoutException($"{program} exceeded its ten-minute process deadline");
        }
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(child.ExitCode, stdout.Result, stderr.Result);
    }

    sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    sealed record BuildPathSnapshot(bool Exists, bool IsDirectory, long? Size, DateTime LastWriteTimeUtc);
}
