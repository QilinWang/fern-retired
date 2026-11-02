using System.Diagnostics;
using Fern.Experiment;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

public sealed class ModelEnvironmentInstallationTests
{
    [Fact]
    public void TheCatalogRefusesModelsThatRequireTwoInterpreterEnvironments()
    {
        var resolved = ModelEnvironmentInstallation.Resolve(
            ["chronos_bolt_base", "zeus"],
            "/site/python");

        Assert.False(resolved.IsOk);
        Assert.Contains("two interpreter environments", resolved.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInstallationPlanUsesUvPipAndHasNoSyncOperation()
    {
        foreach (var models in new[]
                 {
                     new[] { "chronos_bolt_small", "chronos_bolt_base", "chronos2" },
                     new[] { "zeus" },
                 })
        {
            var resolved = ModelEnvironmentInstallation.Resolve(models, "/site/bin/python");
            Assert.True(resolved.IsOk, resolved.IsOk ? "" : resolved.Failure.Detail);

            var argv = resolved.Value.UvArgv;
            Assert.Equal("uv", argv[0]);
            Assert.Equal("pip", argv[1]);
            Assert.Equal("install", argv[2]);
            Assert.Equal("--python", argv[3]);
            Assert.Equal("/site/bin/python", argv[4]);
            Assert.DoesNotContain("sync", argv);
            Assert.DoesNotContain("add", argv);
            Assert.DoesNotContain("remove", argv);
            Assert.DoesNotContain("--active", argv);

            if (resolved.Value.Environment == ModelInterpreterEnvironment.DefaultChronos)
            {
                Assert.Empty(resolved.Value.InterpreterBootstrapArgv);
            }
            else
            {
                Assert.Equal(
                    ["uv", "venv", "--allow-existing", "--python", "3.12", "--no-progress", "/site"],
                    resolved.Value.InterpreterBootstrapArgv);
                // The dependency-confusion guard: no second index and no relaxed
                // index strategy anywhere in the resolution. The CUDA torch
                // build arrives as one immutable pinned wheel URL instead.
                Assert.DoesNotContain("--index-strategy", argv);
                Assert.DoesNotContain("unsafe-best-match", argv);
                Assert.DoesNotContain("--index", argv);
                Assert.DoesNotContain("--default-index", argv);
                Assert.Contains(argv, arg => arg.StartsWith(
                    "https://download.pytorch.org/whl/cu130/torch-2.9.1", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void MutationRootsNameTheVenvAndTheSharedCaches()
    {
        Assert.Equal(
            [
                "environment:/site",
                "model-cache:huggingface",
                "package-cache:uv",
            ],
            ModelEnvironmentInstallation.MutationRoots("/site/bin/python"));
        Assert.Equal(
            "environment:/opt/python",
            ModelEnvironmentInstallation.MutationRoots("/opt/python")[0]);
    }

    [Fact]
    public void InstallAttemptProcessScanReportsMarkedLiveProcess()
    {
        var attemptId = "environment-install:test-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("/usr/bin/env")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ModelEnvironmentInstallation.InstallAttemptProcessMarker + "=" + attemptId);
        start.ArgumentList.Add("sleep");
        start.ArgumentList.Add("10");
        using var process = Process.Start(start);
        Assert.NotNull(process);
        try
        {
            ModelEnvironmentInstallation.EnvironmentInstallProcessScan? observed = null;
            for (var tries = 0; tries < 100; tries++)
            {
                var scan = ModelEnvironmentInstallation.ScanLocalInstallAttemptProcesses(attemptId);
                Assert.True(scan.IsOk, scan.RefusalDetail);
                observed = scan.Value;
                if (observed.LiveMatches.Any(match => match.Pid == process.Id))
                {
                    break;
                }
                Thread.Sleep(20);
            }

            Assert.NotNull(observed);
            var match = Assert.Single(observed.LiveMatches, match => match.Pid == process.Id);
            Assert.Contains("sleep", match.CommandLine, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public void InstallLeaseProcessAbsenceReceiptRefusesLiveMatchesAndNamesTheMarker()
    {
        var profile = Profile("runpod-a4500");
        var lease = new EnvironmentInstallLease(
            "lease:test",
            "sha256:machine",
            profile.Python,
            profile.ProfileId,
            ModelEnvironmentInstallation.MutationRoots(profile.Python),
            7,
            "environment-install:owner",
            "tester",
            1,
            2);
        var live = new ModelEnvironmentInstallation.EnvironmentInstallProcessScan(
            ModelEnvironmentInstallation.ProcessObservationSchema,
            lease.OwnerAttemptId,
            [new ModelEnvironmentInstallation.EnvironmentInstallProcessMatch(123, 456, "uv pip install")]);

        var refused = ModelEnvironmentInstallation.BuildInstallLeaseProcessAbsenceReceipt(
            lease, profile, "observer", live);

        Assert.False(refused.IsOk);
        Assert.Contains("still has live process", refused.Failure.Detail, StringComparison.Ordinal);

        var absent = ModelEnvironmentInstallation.BuildInstallLeaseProcessAbsenceReceipt(
            lease,
            profile,
            "observer",
            new ModelEnvironmentInstallation.EnvironmentInstallProcessScan(
                ModelEnvironmentInstallation.ProcessObservationSchema,
                lease.OwnerAttemptId,
                []));
        Assert.True(absent.IsOk, absent.RefusalDetail);
        Assert.Contains(ModelEnvironmentInstallation.InstallAttemptProcessMarker, absent.Value, StringComparison.Ordinal);
        Assert.Contains("\"observed_outcome\":\"owner_process_absent\"", absent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteReconcileEnvironmentInstallLeaseCommandIsOnTheFernRunSurface()
    {
        var executable = Path.Combine(
            TestRegistry.RepoRoot(),
            "src", "csharp",
            "Fern.Run.Cli",
            "bin",
            "Debug",
            "net11.0",
            "fern-run");
        Assert.True(File.Exists(executable), $"build src/csharp/Fern.Run.Cli first: {executable} is absent");

        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("site");
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(10_000), "fern-run site --help did not exit within 10 seconds");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("reconcile-environment-install-lease", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReceiptIdentityChangesWhenAnInstalledPackageChanges()
    {
        var plan = ModelEnvironmentInstallation.Resolve(
            ["chronos_bolt_base"],
            "/site/python").Value;
        var machine = "sha256:" + new string('m', 64);
        var first = ModelEnvironmentInstallation.ReceiptIdentity(
            machine,
            "sha256:machine",
            ModelEnvironmentInstallation.RequiredPythonAbi,
            plan,
            new Dictionary<string, string> { ["chronos-forecasting"] = "2.3.1", ["torch"] = "2.13.0" });
        var replay = ModelEnvironmentInstallation.ReceiptIdentity(
            machine,
            "sha256:machine",
            ModelEnvironmentInstallation.RequiredPythonAbi,
            plan,
            new Dictionary<string, string> { ["torch"] = "2.13.0", ["chronos-forecasting"] = "2.3.1" });
        var changed = ModelEnvironmentInstallation.ReceiptIdentity(
            machine,
            "sha256:machine",
            ModelEnvironmentInstallation.RequiredPythonAbi,
            plan,
            new Dictionary<string, string> { ["chronos-forecasting"] = "2.3.2", ["torch"] = "2.13.0" });

        Assert.Equal(first, replay);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void AnExtraUnrelatedPackageDoesNotChangeEnvironmentIdentity()
    {
        var plan = ModelEnvironmentInstallation.Resolve(
            ["chronos_bolt_base"],
            "/site/python").Value;
        var required = new Dictionary<string, string>
        {
            ["chronos-forecasting"] = "2.3.1",
            ["torch"] = "2.13.0",
        };
        var withIpython = new Dictionary<string, string>(required) { ["ipython"] = "8.0.0" };
        var machine = "sha256:" + new string('m', 64);
        var first = ModelEnvironmentInstallation.ReceiptIdentity(
            machine, "sha256:machine", ModelEnvironmentInstallation.RequiredPythonAbi, plan, required);
        var extra = ModelEnvironmentInstallation.ReceiptIdentity(
            machine, "sha256:machine", ModelEnvironmentInstallation.RequiredPythonAbi, plan, withIpython);

        Assert.Equal(first, extra);
        Assert.NotEqual(
            first,
            ModelEnvironmentInstallation.ReceiptIdentity(
                machine, "sha256:other-machine", ModelEnvironmentInstallation.RequiredPythonAbi, plan, required));
        Assert.NotEqual(
            first,
            ModelEnvironmentInstallation.ReceiptIdentity(
                "sha256:" + new string('n', 64), "sha256:machine", ModelEnvironmentInstallation.RequiredPythonAbi, plan, required));
    }

    [Fact]
    public void AMergedSshProbeStreamKeepsTheJsonObjectAfterPhaseLines()
    {
        var merged =
            "model-environment phase=python_start\n"
            + "model-environment phase=imports\n"
            + """{"packages":{"torch":"2.12.0"},"python":"/site/bin/python"}""" + "\n";
        Assert.Equal(
            """{"packages":{"torch":"2.12.0"},"python":"/site/bin/python"}""",
            ModelEnvironmentInstallation.LastJsonObjectLine(merged));
        Assert.Null(ModelEnvironmentInstallation.LastJsonObjectLine("model-environment phase=imports\n"));
    }

    [Fact]
    public void CompileRefusesAMissingEnvironmentReceiptInsteadOfProbingRuntime()
    {
        var refused = EnvironmentReceiptAdmission.Admit(Profile("local-4070"), []);
        Assert.False(refused.IsOk);
        Assert.Contains("--environment-receipt", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("not a reason to start fern_stage_runtime probe", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileAdmitsAMatchingEnvironmentReceiptAndDoesNotNeedARuntimeProbe()
    {
        var profile = Profile("runpod-a4500");
        var path = WriteReceipt(profile, "already_ready", "sha256:" + new string('a', 64));
        var admitted = EnvironmentReceiptAdmission.Admit(profile, [path]);
        Assert.True(admitted.IsOk, admitted.IsOk ? "" : admitted.Failure.Detail);
        Assert.Equal(profile.ProfileId, admitted.Value.SiteId);
        Assert.Equal("already_ready", admitted.Value.Outcome);
        Assert.Equal("DefaultChronos", admitted.Value.EnvironmentKind);
        Assert.StartsWith("sha256:", admitted.Value.EnvironmentIdentity);
    }

    [Fact]
    public void CompileRefusesAMissingRegistryEnvironmentAdmissionInsteadOfProbingRuntime()
    {
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var refused = EnvironmentReceiptAdmission.AdmitFromRegistry(
            opened.Connection, Profile("runpod-a4500"), ["chronos2"], []);
        Assert.False(refused.IsOk);
        Assert.Contains("no environment admission", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("not a reason to start fern_stage_runtime probe", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInstallationRefusesBeforeEffectWhenMachineAdmissionIsMissing()
    {
        var profile = Profile("runpod-a4500");
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));

        var consumed = EnvironmentInstallationAdmission.ConsumeCurrentMachineAdmission(
            opened.Writer,
            profile);

        Assert.False(consumed.IsOk);
        Assert.Contains("no current MachineAdmission", consumed.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("not permission to install packages", consumed.Failure.Detail, StringComparison.Ordinal);
        Assert.Null(SiteEnvironmentAdmissionAuthority.Current(opened.Connection, profile.ProfileId));
    }

    [Fact]
    public void EnvironmentInstallationStillConsumesMachineAdmissionAfterEndpointOnlyProfileChange()
    {
        var recorded = Profile("runpod-a4500");
        var afterReconnect = recorded with
        {
            SshDestination = "root@example:31042",
        };
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var machine = AdmitMachine(opened.Writer, recorded);

        var consumed = EnvironmentInstallationAdmission.ConsumeCurrentMachineAdmission(
            opened.Writer,
            afterReconnect);

        Assert.True(consumed.IsOk, consumed.IsOk ? "" : consumed.Failure.Detail);
        Assert.Equal(machine.MachineAdmissionIdentity, consumed.Value.MachineAdmissionIdentity);
    }

    [Fact]
    public void CompileConsumesTheRegistryEnvironmentAdmissionAndDoesNotNeedARuntimeProbe()
    {
        var profile = Profile("runpod-a4500");
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var identity = "sha256:" + new string('a', 64);
        var machine = AdmitMachine(opened.Writer, profile);
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(
            opened.Writer, Admission(profile, identity, ["chronos2"], machine)).IsOk);

        var admitted = EnvironmentReceiptAdmission.AdmitFromRegistry(
            opened.Connection, profile, ["chronos2"], []);
        Assert.True(admitted.IsOk, admitted.IsOk ? "" : admitted.Failure.Detail);
        Assert.Equal(identity, admitted.Value.EnvironmentIdentity);
        Assert.Equal("registry:runpod-a4500", admitted.Value.ReceiptLocator);
    }



    [Fact]
    public void CompileRefusesARegistryAdmissionThatOmitsTheCompiledModel()
    {
        var profile = Profile("runpod-a4500");
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var machine = AdmitMachine(opened.Writer, profile);
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(
            opened.Writer, Admission(profile, "sha256:" + new string('a', 64), ["chronos_bolt_small"], machine)).IsOk);

        var refused = EnvironmentReceiptAdmission.AdmitFromRegistry(
            opened.Connection, profile, ["chronos2"], []);
        Assert.False(refused.IsOk);
        Assert.Contains("chronos2", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("re-run site install-model-environment", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileRefusesARegistryAdmissionWhoseGpuUuidDoesNotMatchTheSite()
    {
        var profile = Profile("runpod-a4500");
        var admission = new SiteEnvironmentAdmission(
            profile.ProfileId,
            "sha256:" + new string('a', 64),
            profile.Revision,
            profile.Python,
            "DefaultChronos",
            ["chronos2"],
            "already_ready",
            "sha256:" + new string('d', 64),
            "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "{}",
            1,
            "tester",
            MachineGeneration: "sha256:" + new string('m', 64),
            MachineAdmissionIdentity: "sha256:" + new string('p', 64),
            MachineAdmissionRecordedAtMs: 1);
        var refused = EnvironmentReceiptAdmission.AdmitAdmission(
            profile,
            admission,
            ["chronos2"],
            "GPU-ffffffff-ffff-ffff-ffff-ffffffffffff");
        Assert.False(refused.IsOk);
        Assert.Contains("registered Site device", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileStillAdmitsWhenOnlyTheSshEndpointChanged()
    {
        var recorded = Profile("runpod-a4500");
        var afterReconnect = recorded with
        {
            Revision = "sha256:" + new string('b', 64),
            SshDestination = "root@example:31042",
        };
        var path = WriteReceipt(recorded, "already_ready", "sha256:" + new string('c', 64));
        var admitted = EnvironmentReceiptAdmission.Admit(afterReconnect, [path]);
        Assert.True(admitted.IsOk, admitted.IsOk ? "" : admitted.Failure.Detail);
        Assert.Equal("sha256:" + new string('c', 64), admitted.Value.EnvironmentIdentity);
    }

    [Fact]
    public void CompileStillAdmitsARegistryRowAfterTheSshEndpointChanged()
    {
        var recorded = Profile("runpod-a4500");
        var afterReconnect = recorded with
        {
            Revision = "sha256:" + new string('b', 64),
            SshDestination = "root@example:31042",
        };
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var identity = "sha256:" + new string('a', 64);
        var machine = AdmitMachine(opened.Writer, recorded);
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(
            opened.Writer, Admission(recorded, identity, ["chronos2"], machine)).IsOk);

        var admitted = EnvironmentReceiptAdmission.AdmitFromRegistry(
            opened.Connection, afterReconnect, ["chronos2"], []);
        Assert.True(admitted.IsOk, admitted.IsOk ? "" : admitted.Failure.Detail);
        Assert.Equal(identity, admitted.Value.EnvironmentIdentity);
    }

    [Fact]
    public void CompileRefusesARegistryAdmissionWhoseMachineParentIsNotCurrent()
    {
        var profile = Profile("runpod-a4500");
        var path = Home();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));
        var oldMachine = AdmitMachine(
            opened.Writer,
            profile,
            deviceId: "GPU-old");
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(
            opened.Writer,
            Admission(profile, "sha256:" + new string('a', 64), ["chronos2"], oldMachine)).IsOk);
        _ = AdmitMachine(
            opened.Writer,
            profile,
            deviceId: "GPU-new");

        var refused = EnvironmentReceiptAdmission.AdmitFromRegistry(
            opened.Connection, profile, ["chronos2"], []);

        Assert.False(refused.IsOk);
        Assert.Contains("current MachineAdmission", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("re-run site install-model-environment", refused.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileRefusesWhenTheRegisteredMachineGenerationChanged()
    {
        var profile = Profile("runpod-a4500");
        var admission = new SiteEnvironmentAdmission(
            profile.ProfileId,
            "sha256:" + new string('a', 64),
            profile.Revision,
            profile.Python,
            "DefaultChronos",
            ["chronos2"],
            "already_ready",
            "sha256:" + new string('d', 64),
            "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "{}",
            1,
            "tester",
            "sha256:" + new string('m', 64),
            "sha256:" + new string('e', 64),
            MachineAdmissionIdentity: "sha256:" + new string('p', 64),
            MachineAdmissionRecordedAtMs: 1);
        var refused = EnvironmentReceiptAdmission.AdmitAdmission(
            profile,
            admission,
            ["chronos2"],
            "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "sha256:" + new string('n', 64));
        Assert.False(refused.IsOk);
        Assert.Contains("machine generation", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("does not inherit", refused.Failure.Detail, StringComparison.Ordinal);
    }

    static SiteEnvironmentAdmissionWrite Admission(
        ExecutionProfileV5 profile,
        string identity,
        IReadOnlyList<string> models,
        MachineAdmission machine,
        string environmentKind = "DefaultChronos") =>
        new(
            profile.ProfileId,
            identity,
            profile.Revision,
            profile.Python,
            environmentKind,
            models,
            "already_ready",
            "sha256:" + new string('d', 64),
            machine.DeviceId,
            EnvironmentReceipt(profile, identity, machine, environmentKind, "already_ready"),
            "tester",
            machine.MachineGeneration,
            machine.EndpointSessionId,
            null,
            null,
            null,
            machine.MachineAdmissionIdentity,
            machine.RecordedAtMs);

    static MachineAdmission AdmitMachine(
        RegistryWriter writer,
        ExecutionProfileV5 profile,
        string deviceId = "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
    {
        var write = MachineAdmissionVerification.BuildWrite(
            profile,
            Fingerprint(profile, deviceId),
            Storage(),
            "test");
        Assert.True(write.IsOk, write.RefusalDetail);
        var recorded = MachineAdmissionAuthority.Record(
            writer,
            write.Value);
        Assert.True(recorded.TryGetValue(out var outcome), Detail(recorded));
        return outcome.Admission;
    }

    static IReadOnlyList<MachineStorageBoundary> Storage() =>
    [
        new("/workspace/work", "/workspace", "overlay", "hard_stop_passed"),
    ];

    static ObservedExecutionFingerprint Fingerprint(ExecutionProfileV5 profile, string deviceId)
    {
        var cuda = profile.Device == "cuda";
        return new ObservedExecutionFingerprint(
            ObservedExecutionProbe.Schema,
            1_000,
            "test-version",
            new ObservedPlatform("test-host", "Linux", "6.1", "x86_64"),
            new ObservedPythonRuntime(
                profile.Python,
                profile.Python,
                "3.12.10",
                "CPython",
                profile.WorkerModule,
                true),
            new ObservedTorchRuntime(
                "2.9.1+cu130",
                cuda ? "13.0" : null,
                cuda ? 9_800UL : null,
                false,
                "highest",
                true,
                true,
                false,
                false),
            cuda,
            cuda ? 0U : null,
            null,
            "PCI_BUS_ID",
            cuda ? deviceId : null,
            cuda
                ? [
                    new ObservedCudaDevice(
                        0,
                        "NVIDIA RTX A4500",
                        [8u, 6u],
                        20_401_094_656UL,
                        56u,
                        deviceId,
                        "555.99"),
                ]
                : []);
    }

    static string EnvironmentReceipt(
        ExecutionProfileV5 profile,
        string identity,
        MachineAdmission machine,
        string environmentKind,
        string outcome) =>
        $$"""
        {"schema":"fern.model_environment_installation.v1","outcome":"{{outcome}}","environment_identity":"{{identity}}","environment_kind":"{{environmentKind}}","site_id":"{{profile.ProfileId}}","execution_profile_revision":"{{profile.Revision}}","interpreter":"{{profile.Python}}","machine_generation":"{{machine.MachineGeneration}}","device_id":"{{machine.DeviceId}}","machine_admission_identity":"{{machine.MachineAdmissionIdentity}}"}
        """;

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "operation returned no result";

    static ExecutionProfileV5 ReadProfile(string siteId)
    {
        var profile = ExecutionProfileV5.Read(
            WorktreeControlPaths.Profiles(TestRegistry.RepoRoot()), siteId, expectedRevision: null);
        Assert.True(profile.IsOk, profile.IsOk ? "" : profile.Failure.Detail);
        return profile.Value;
    }

    static ExecutionProfileV5 Profile(string siteId) => new(
        siteId,
        "sha256:" + new string('1', 64),
        siteId.StartsWith("runpod", StringComparison.Ordinal) ? "ssh" : "local",
        siteId.StartsWith("runpod", StringComparison.Ordinal) ? "root@example:22" : null,
        null,
        "cuda",
        "/source",
        "/site/bin/python",
        "fern_train.worker",
        1,
        3,
        5,
        3600,
        86400,
        "/campaign",
        "/artifacts",
        "/work",
        4,
        [8, 9],
        null,
        null);

    static string WriteReceipt(ExecutionProfileV5 profile, string outcome, string identity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fern-env-receipt-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "schema": "fern.model_environment_installation.v1",
              "outcome": "{{outcome}}",
              "environment_identity": "{{identity}}",
              "environment_kind": "DefaultChronos",
              "site_id": "{{profile.ProfileId}}",
              "execution_profile_revision": "{{profile.Revision}}",
              "interpreter": "{{profile.Python}}",
              "machine_admission_identity": "sha256:{{new string('m', 64)}}"
            }
            """);
        return path;
    }

    const string AdmittedReceiptJson = """
        {
          "environment_identity": "sha256:aaa",
          "python_version": "3.12.7",
          "packages": { "torch": "2.9.1+cu130", "transformers": "4.40.1" },
          "asset_manifest": {
            "amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c": [
              { "file": "config.json", "size": 1200, "blob": "cfg111" },
              { "file": "model.safetensors", "size": 100000, "blob": "abc222" }
            ]
          }
        }
        """;

    static ModelEnvironmentInstallation.EnvironmentDrift Drift(
        string receipt,
        string identity,
        IReadOnlyDictionary<string, string> packages,
        string python,
        string? manifestJson)
    {
        if (manifestJson is null)
        {
            return ModelEnvironmentInstallation.DriftAgainstAdmission(receipt, identity, packages, python, null);
        }
        using var manifest = System.Text.Json.JsonDocument.Parse(manifestJson);
        return ModelEnvironmentInstallation.DriftAgainstAdmission(
            receipt, identity, packages, python, manifest.RootElement);
    }

    static readonly Dictionary<string, string> AdmittedPackages = new(StringComparer.Ordinal)
    {
        ["torch"] = "2.9.1+cu130",
        ["transformers"] = "4.40.1",
    };

    const string AdmittedManifestJson = """
        {
          "amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c": [
            { "file": "config.json", "size": 1200, "blob": "cfg111" },
            { "file": "model.safetensors", "size": 100000, "blob": "abc222" }
          ]
        }
        """;

    [Fact]
    public void AnIdenticalObservationHasNoDrift()
    {
        var drift = Drift(AdmittedReceiptJson, "sha256:aaa", AdmittedPackages, "3.12.7", AdmittedManifestJson);
        Assert.Empty(drift.Sentences);
    }

    [Fact]
    public void ADriftedPackageVersionIsNamedInsteadOfReAdmitted()
    {
        var observed = new Dictionary<string, string>(AdmittedPackages, StringComparer.Ordinal)
        {
            ["torch"] = "2.9.2+cu130",
        };
        var drift = Drift(AdmittedReceiptJson, "sha256:bbb", observed, "3.12.7", AdmittedManifestJson);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("torch", sentence, StringComparison.Ordinal);
        Assert.Contains("2.9.1+cu130", sentence, StringComparison.Ordinal);
        Assert.Contains("2.9.2+cu130", sentence, StringComparison.Ordinal);
        Assert.True(drift.HasNonAssetDrift);
        Assert.Empty(drift.DriftedAssetKeys);
    }

    [Fact]
    public void APythonPatchChangeIsDriftEvenThoughTheAbiStillMatches()
    {
        var drift = Drift(AdmittedReceiptJson, "sha256:aaa", AdmittedPackages, "3.12.8", AdmittedManifestJson);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("3.12.7", sentence, StringComparison.Ordinal);
        Assert.Contains("3.12.8", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedCheckpointShardIsNamedByFile()
    {
        const string truncated = """
            {
              "amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c": [
                { "file": "config.json", "size": 1200, "blob": "cfg111" },
                { "file": "model.safetensors", "size": 90000, "blob": "abc222" }
              ]
            }
            """;
        var drift = Drift(AdmittedReceiptJson, "sha256:aaa", AdmittedPackages, "3.12.7", truncated);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("model.safetensors", sentence, StringComparison.Ordinal);
        Assert.Contains("90000", sentence, StringComparison.Ordinal);
        Assert.False(drift.HasNonAssetDrift);
        Assert.Equal(
            ["amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c"],
            drift.DriftedAssetKeys);
    }

    [Fact]
    public void AMissingCheckpointFileIsNamedByFile()
    {
        const string missing = """
            {
              "amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c": [
                { "file": "config.json", "size": 1200, "blob": "cfg111" }
              ]
            }
            """;
        var drift = Drift(AdmittedReceiptJson, "sha256:aaa", AdmittedPackages, "3.12.7", missing);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("model.safetensors", sentence, StringComparison.Ordinal);
        Assert.Contains("missing", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void AReceiptWithoutAManifestSkipsAssetComparison()
    {
        const string legacyReceipt = """
            {
              "environment_identity": "sha256:aaa",
              "python_version": "3.12.7",
              "packages": { "torch": "2.9.1+cu130", "transformers": "4.40.1" }
            }
            """;
        var drift = Drift(legacyReceipt, "sha256:aaa", AdmittedPackages, "3.12.7", AdmittedManifestJson);
        Assert.Empty(drift.Sentences);
    }

    [Fact]
    public void AnIdentityOnlyDifferenceStillReportsDrift()
    {
        var drift = Drift(AdmittedReceiptJson, "sha256:bbb", AdmittedPackages, "3.12.7", AdmittedManifestJson);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("environment identity drifted", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastReportedPhaseIsTheOneThatRaised()
    {
        const string merged = """
            model-environment phase=python_start
            model-environment phase=imports
            model-environment phase=torch_cuda_check
            model-environment phase=checkpoint_local_check
            Traceback (most recent call last): ...
            """;
        Assert.Equal("checkpoint_local_check", ModelEnvironmentInstallation.LastReportedPhase(merged));
        Assert.Null(ModelEnvironmentInstallation.LastReportedPhase("no marker in this output"));
    }

    [Fact]
    public void AssetRepairScopeIsExactlyTheSharedModelCache()
    {
        Assert.Equal(["model-cache:huggingface"], ModelEnvironmentInstallation.AssetRepairMutationRoots());
    }

    /// <summary>
    /// The reader's size-preserving corruption case: a file that keeps its
    /// byte count but stops being a cache symlink (blob null on one side)
    /// must still read as drift.
    /// </summary>
    [Fact]
    public void ASizePreservingCorruptionThatDropsTheBlobIsStillDrift()
    {
        const string blobless = """
            {
              "amazon/chronos-2@29ec3766d36d6f73f0696f85560a422f50e8498c": [
                { "file": "config.json", "size": 1200, "blob": "cfg111" },
                { "file": "model.safetensors", "size": 100000, "blob": null }
              ]
            }
            """;
        var drift = Drift(AdmittedReceiptJson, "sha256:aaa", AdmittedPackages, "3.12.7", blobless);
        var sentence = Assert.Single(drift.Sentences);
        Assert.Contains("model.safetensors", sentence, StringComparison.Ordinal);
        Assert.False(drift.HasNonAssetDrift);
    }

    /// <summary>CHG 2026-09-11 night (slice 9): an admission lives on a registered Site's row, so the Home registers the Sites these tests admit on.</summary>
    static string Home()
    {
        var path = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        foreach (var site in new[] { "local-4070", "runpod-a4500" })
        {
            Assert.True(RegistryOperations.RegisterSite(writer, site, """{"device":"cuda","executor":"ssh","devices":["GPU-a"]}""", """{"slots":1}""").IsOk);
        }
        return path;
    }
}
