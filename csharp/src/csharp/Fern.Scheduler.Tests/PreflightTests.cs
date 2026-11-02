using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Product-contract tests for profile validation, its three projections, the
/// storage probe, and the execution-profile declaration rules.
///
/// `fern-run profile validate` prints one `fern.profile_validation.v1`
/// report. The retired Rust executable is no longer an oracle: these tests
/// cross the real C# process boundary, parse its typed JSON, and require the
/// human, agent, and JSON projections plus the independently read storage facts
/// to retain their contract.
///
/// The remote SSH probe is deliberately not ported and is not tested here.
/// </summary>
public sealed class PreflightTests
{
    const string Profile = "local-4070";

    [Fact]
    public void CurrentProfileValidateHumanProjectionIsAnsiFree()
    {
        var human = CurrentProfileValidate(TestRegistry.RepoRoot(), Profile, "human");
        Assert.StartsWith("profile validate: ready\n", human, StringComparison.Ordinal);
        Assert.Contains("GPU work started: no", human, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", human, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentProfileValidateAgentProjectionIsConciseAndAnsiFree()
    {
        var agent = CurrentProfileValidate(TestRegistry.RepoRoot(), Profile, "agent");
        Assert.StartsWith("profile validate: ready; profile=local-4070;", agent, StringComparison.Ordinal);
        Assert.Contains("gpu_work_started=no; problems=0", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("observed_execution", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", agent, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentProfileValidateJsonProjectionHasOnlyItsOperationSchema()
    {
        var output = CurrentProfileValidate(TestRegistry.RepoRoot(), Profile, "json");
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("fern.profile_validation.v1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("gpu_work_started").GetBoolean());
        foreach (var removed in new[]
                 { "command", "selected_preset", "selected_model", "canary", "recipes", "stages", "estimated_total_seconds" })
        {
            Assert.False(root.TryGetProperty(removed, out _), $"profile validation still prints retired field {removed}");
        }
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ready report is not evidence that the rules can refuse. This one is
    /// deliberately wrong in many ways at once, so the active declaration
    /// rules still refuse in one stable order after the worker fields retired.
    /// </summary>
    [Fact]
    public void DeclarationIssuesRemainOrderedAfterWorkerFieldRetirement()
    {
        _ = BrokenProfileWorkspace(out var profileId, out var declaration);

        using var document = JsonDocument.Parse(declaration);
        var mine = Preflight.DeclarationProblems(
                profileId,
                $"profiles/{profileId}.json",
                "local",
                ExecutionProfileDeclaration.Issues(document.RootElement))
            .Select(problem => (problem.Meaning, Fix: problem.FixCommand))
            .ToList();

        Assert.NotEmpty(mine);
        Assert.DoesNotContain(mine, problem => problem.Meaning.Contains("worker_binary", StringComparison.Ordinal));
        Assert.Contains(mine, problem => problem.Meaning.Contains("profile field schema", StringComparison.Ordinal));
        Assert.Contains(mine, problem => problem.Meaning.Contains("profile field worker.slots", StringComparison.Ordinal));
        Assert.All(mine, problem => Assert.Equal(
            $"edit profiles/{profileId}.json and run fern-run profile validate {profileId} --observer host",
            problem.Fix));
    }

    [Fact]
    public void AValidDeclarationHasNoIssues()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(WorktreeControlPaths.Profiles(TestRegistry.RepoRoot()), Profile + ".json")));
        Assert.Empty(ExecutionProfileDeclaration.Issues(document.RootElement));
    }

    /// <summary>
    /// The storage rows the product printed name real volumes; reading them again here
    /// must find the same filesystems under the same inspected paths, and reach
    /// the same warning and hard-stop verdicts. The free-space NUMBERS move
    /// between two processes, so they are compared loosely and the verdicts
    /// exactly — the verdicts are what gate a launch.
    /// </summary>
    [Fact]
    public void TheStorageProbeReadsTheSameVolumesAndVerdictsTheProductPrinted()
    {
        using var report = JsonDocument.Parse(CurrentProfileValidate(TestRegistry.RepoRoot(), Profile, "json"));
        var expectedChecks = report.RootElement.GetProperty("storage").EnumerateArray()
            .Select(check => new StorageCheck(
                check.GetProperty("inspected_path").GetString()!,
                check.GetProperty("filesystem").GetString()!,
                check.GetProperty("available_gib").GetDouble(),
                check.GetProperty("free_percent").GetDouble(),
                check.GetProperty("warning").GetBoolean(),
                check.GetProperty("hard_stop").GetBoolean()))
            .ToList();
        Assert.NotEmpty(expectedChecks);
        foreach (var expected in expectedChecks)
        {
            var reading = StorageProbe.Inspect(expected.InspectedPath);
            Assert.Null(reading.Refusal);
            var mine = reading.Check!;
            Assert.Equal(expected.InspectedPath, mine.InspectedPath);
            Assert.Equal(expected.Filesystem, mine.Filesystem);
            Assert.Equal(expected.Warning, mine.Warning);
            Assert.Equal(expected.HardStop, mine.HardStop);
            Assert.True(
                Math.Abs(expected.AvailableGib - mine.AvailableGib) < 5.0,
                $"{mine.Filesystem} moved from {expected.AvailableGib} to {mine.AvailableGib} GiB between two reads");
        }
    }

    /// <summary>
    /// A RELATIVE declared root is the case with no existing ancestor: an
    /// absolute one always walks up to "/", which exists, so Rust inspects it
    /// rather than refusing. The broken-declaration golden takes this branch
    /// with its relative work_root and prints exactly this sentence.
    /// </summary>
    [Fact]
    public void APathWithNoExistingAncestorIsRefusedByName()
    {
        var reading = StorageProbe.Inspect("no-such-relative-root-for-fern");
        Assert.Null(reading.Check);
        Assert.Equal(
            "no existing ancestor for declared path no-such-relative-root-for-fern", reading.Refusal);

        // The absolute twin, for the same reason stated the other way round.
        var root = StorageProbe.Inspect("/no-such-root-for-fern/campaign");
        Assert.Null(root.Refusal);
        Assert.Equal("/", root.Check!.InspectedPath);
    }

    /// <summary>
    /// The verdict thresholds, asked about numbers rather than about this
    /// machine's disks. The live volumes sit far from every threshold — 679 GiB
    /// and 47.9 GiB against a 50 GiB warning — so comparing verdicts on them
    /// agrees even when a threshold is wrong by a factor of ten. Only the
    /// boundaries can say the rule is the same rule.
    /// </summary>
    [Theory]
    [InlineData(49.9, 50.0, true, false)]
    [InlineData(50.0, 50.0, false, false)]
    [InlineData(500.0, 9.9, true, false)]
    [InlineData(500.0, 10.0, false, false)]
    [InlineData(4.9, 50.0, true, true)]
    [InlineData(5.0, 50.0, true, false)]
    public void TheStorageVerdictWarnsAtFiftyGibOrTenPercentAndStopsAtFive(
        double availableGib, double freePercent, bool warning, bool hardStop)
    {
        Assert.Equal((warning, hardStop), StorageProbe.Verdict(availableGib, freePercent));
    }

    /// <summary>
    /// A sandbox that cannot see the declared GPU asks for a host check and
    /// nothing more; anything else in the list makes the whole report blocked.
    /// The process tests below own exit codes; this owner decides the status.
    /// </summary>
    [Fact]
    public void OnlyAHostCheckProblemProducesNeedsHostCheck()
    {
        var report = Preflight.Blocked(
            Profile, $"profiles/{Profile}.json", ObserverContext.CodexSandbox, []) with
        {
            Problems =
            [
                new PreflightProblem(PreflightProblemCode.HostCheckRequired, "cannot see the declared GPU", "run on the host"),
            ],
        };
        var closed = Preflight.Close(report);
        Assert.Equal(ProfileValidationStatus.NeedsHostCheck, closed.Status);
        Assert.Equal(
            $"Repeat this read-only check outside the Codex sandbox: fern-run profile validate {Profile} --observer host. No GPU work has started.",
            closed.NextStep);

        var mixed = Preflight.Close(report with
        {
            Problems =
            [
                .. report.Problems,
                new PreflightProblem(PreflightProblemCode.CapacityBlocked, "disk full", "free space"),
            ],
        });
        Assert.Equal(ProfileValidationStatus.Blocked, mixed.Status);

        var clean = Preflight.Close(report with { Problems = [], Status = ProfileValidationStatus.Ready });
        Assert.Equal(ProfileValidationStatus.Ready, clean.Status);
    }

    /// <summary>
    /// One workspace whose single profile breaks every declaration rule that can
    /// be broken at once, so the golden exercises the list rather than one row.
    /// </summary>
    static string BrokenProfileWorkspace(out string profileId, out byte[] declaration)
    {
        profileId = "broken-declaration";
        var workspace = Directory.CreateTempSubdirectory("fern-preflight-").FullName;
        Directory.CreateDirectory(WorktreeControlPaths.Profiles(workspace));
        var text = """
            {
              "schema": "fern.execution_profile.v4",
              "profile_id": "broken-declaration",
              "executor": "local",
              "ssh_destination": "nowhere",
              "registry_home_profile_id": "other",
              "device": "cuda",
              "source_root": "/tmp",
              "python": "/tmp/python",
              "worker": { "module": "9bad-module", "slots": 2 },
              "supervisor": {
                "max_restarts": 2,
                "restart_delay_seconds": 0,
                "max_target_seconds": 0,
                "max_sweep_seconds": 0
              },
              "campaign_root": "/tmp/c",
              "artifact_root": "/tmp/a",
              "work_root": "rel",
              "known_data_root": "rel/known",
              "warn_free_gib": 1,
              "warn_free_percent": 0
            }
            """;
        declaration = new UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(Path.Combine(WorktreeControlPaths.Profiles(workspace), profileId + ".json"), declaration);
        return workspace;
    }

    /// <summary>
    /// One read-only invocation of the current C# product boundary. It changes
    /// nothing and starts nothing.
    /// </summary>
    static string CurrentProfileValidate(string workspace, string profileId, string format)
    {
        var executable = Path.Combine(
            workspace, "src", "csharp", "Fern.Run.Cli", "bin", "Debug", "net11.0", "fern-run");
        Assert.True(File.Exists(executable), $"build the current fern-run before this test: {executable}");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[]
                 { "profile", "validate", profileId, "--observer", "host", "--format", format })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start fern-run");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"current profile validate exited {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
        return output;
    }
}
