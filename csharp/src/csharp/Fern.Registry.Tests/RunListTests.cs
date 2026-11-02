using System.Text.Json;
using Fern.RunData;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Ports every test function in <c>tools/fern-control/registry/run_list_test.go</c>
/// at 79af3ca5 (10 functions) and the two list-runs functions in
/// <c>cmd/fern-home/main_test.go</c>, plus the one main golden captured from the
/// fern-home binary built at that commit (Fixtures/run_list/golden.json).
///
/// Two of the twelve are NOT repeated here because their destination already
/// existed with the same assertion, and a second copy would be a duplicate
/// rather than a proof:
/// <list type="bullet">
/// <item><description><c>TestRunListRustExecutableGoldenMatchesCompleteReportAtZeroSemanticTolerance</c>
/// was migration evidence. Its durable assertions now live in
/// <c>RunViewsReaderTests.list_preserves_complete_rows_default_status_filters_order_and_exact_nullable_values</c>;
/// the active suite does not launch the retired implementation as an oracle.</description></item>
/// <item><description><c>Test_run_list_refusal_leaves_production_registry_bytes_unchanged</c>
/// is <c>RunViewsReaderTests.list_refusal_leaves_production_migrated_lane_bytes_unchanged</c>.
/// The refusal on the NEW process route is a separate addition below.</description></item>
/// </list>
///
/// The projection itself is Fern.RunData's RunViewsReader, which was ported from
/// the same retired Rust reader the Go package was ported from; this file proves
/// the Go verb's public contract over it. Every registry here is created by the
/// production migration chain.
/// </summary>
public sealed class RunListTests
{
    /// <summary>A migrated lane database named the way a campaign root expects, carrying the golden's five rows.</summary>
    static string Fixture()
    {
        var directory = Directory.CreateTempSubdirectory("run-list-").FullName;
        return Lane(directory, "lane");
    }

    /// <summary>One <c>&lt;root&gt;/&lt;lane&gt;/fern.db</c>, migrated by the production chain and filled from the golden's SQL.</summary>
    static string Lane(string root, string lane)
    {
        var directory = Path.Combine(root, lane);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fern.db");
        File.WriteAllBytes(path, []);
        ProductionHome.Initialize(path);
        // CHG 2026-09-12: a lane database is an archive that also carries the
        // Home's schema, which is how these were made. The per-run tables left
        // the Home at schema version 8, so the lane gets them here; the golden
        // SQL below is unchanged, and so is what `list-runs` answers.
        RunArchive.AddTo(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // The literal fixture SQL the Go golden was captured over, so the C#
        // fixture cannot drift from the one the old executable answered about.
        command.CommandText = LoadGolden().FixtureSql;
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void test_enroll_and_complete()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path));

        Assert.Equal(3, report.Runs.Count);
        Assert.Equal("done", report.Runs[0].Status);
        Assert.NotNull(report.Runs[0].Mse);
    }

    // Named for the Go function; RunViewsReaderTests carries a same-named test
    // of the invocation WRITER, where this one is the Go verb's read projection.
    [Fact]
    public void test_update_run_invocation()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path, Status: "pending"));

        var row = Assert.Single(report.Runs);
        Assert.NotNull(row.InvocationJson);
        Assert.NotNull(row.ConfigName);
    }

    [Fact]
    public void test_mark_failed()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path, Status: "failed"));

        var row = Assert.Single(report.Runs);
        Assert.Equal("OOM at epoch 5", row.ErrorMessage);
    }

    [Fact]
    public void test_reenroll_resets_status()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path, Status: "pending"));

        var row = Assert.Single(report.Runs);
        Assert.Equal("reenrolled", row.RunHash);
        Assert.Null(row.ErrorMessage);
    }

    // Named for the Go function; RunViewsReaderTests carries a same-named test
    // of the pin WRITER, where this one asserts the flag survives the projection.
    [Fact]
    public void test_pin_prevents_slim()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path));

        var pinned = Assert.Single(report.Runs, row => row.RunHash == "pinned-done");
        Assert.Equal(1, pinned.Pinned);
    }

    [Fact]
    public void one_lane_or_every_lane_parses_but_never_both()
    {
        var root = Directory.CreateTempSubdirectory("run-list-campaign-").FullName;
        var path = Lane(root, "lane-a");

        Assert.Equal(3, RunViewsReader.List(new RunListRequest(DatabasePath: path)).Runs.Count);
        Assert.Equal(3, RunViewsReader.List(new RunListRequest(CampaignRoot: root)).Runs.Count);
        Assert.Throws<RunDataFault>(() =>
            RunViewsReader.List(new RunListRequest(DatabasePath: path, CampaignRoot: root)));
        Assert.Throws<RunDataFault>(() => RunViewsReader.List(new RunListRequest()));
    }

    [Fact]
    public void RunListOrderingFiltersAndCampaignLimitUseProductionMigrations()
    {
        var path = Fixture();

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path, Dataset: "alpha", Limit: 1));

        var row = Assert.Single(report.Runs);
        Assert.Equal("a-model", row.ModelType);
    }

    // `TestRunListGivesEveryCampaignDatabaseAnIndependentDeadline` is OPEN, not
    // ported and not waived. Go proved it through a package-private observer
    // (`runListContextObserver`) that read each campaign reader's context
    // deadline and required the two to differ. Fern.RunData has no such seam,
    // and the fact has no other observable consequence than timing: each lane
    // gets its own SqliteConnection whose DefaultTimeout and CommandTimeout are
    // two seconds, so only a read that outlasts a single shared budget can tell
    // the two designs apart. Blocking a lane to force that is not available
    // either -- a production lane Home is in WAL, where an ordinary writer never
    // blocks a reader, and `locking_mode = EXCLUSIVE` fails the reader at
    // prepare time with SQLITE_BUSY instead of waiting out its budget. The
    // proof needs the same seam Go has, inside Fern.RunData, which is outside
    // this plan's writable scope; the exact edit is in the report.

    // Ported from cmd/fern-home/main_test.go
    // `Test_list_runs_process_reads_production_migrated_registry_without_mutation`.
    [Fact]
    public void list_runs_process_reads_production_migrated_registry_without_mutation()
    {
        var path = Fixture();
        var before = File.ReadAllBytes(path);

        var result = ProductionHome.RunFernRegistry(["list-runs", "--db", path, "--limit", "0"], "", 20_000);

        Assert.Equal(0, result.Exit);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("fern.run_list.v1", document.RootElement.GetProperty("schema").GetString());
        var runs = document.RootElement.GetProperty("runs");
        Assert.Equal(3, runs.GetArrayLength());
        Assert.Equal("done-a", runs[0].GetProperty("run_hash").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Ported from cmd/fern-home/main_test.go
    // `Test_list_runs_rejects_unknown_closed_status`: the closed five-name
    // vocabulary is refused before any database is named or opened.
    [Fact]
    public void list_runs_rejects_unknown_closed_status()
    {
        var result = ProductionHome.RunFernRegistry(
            ["list-runs", "--db", "ignored.db", "--status", "invented"], "", 20_000);

        Assert.NotEqual(0, result.Exit);
        Assert.Equal("", result.Stdout);
        Assert.Contains("--status must be one of pending, running, done, failed, stale", result.Stderr);
    }

    // An addition with no Go twin: the Go refusal test covered the library call,
    // and this covers the new process route the callers will spawn.
    [Fact]
    public void ListRunsRefusalLeavesTheRegistryBytesUnchanged()
    {
        var path = Fixture();
        var before = File.ReadAllBytes(path);

        var missing = ProductionHome.RunFernRegistry(
            ["list-runs", "--db", Path.Combine(Path.GetTempPath(), "run-list-missing.db")], "", 20_000);
        var bothScopes = ProductionHome.RunFernRegistry(
            ["list-runs", "--db", path, "--campaign-root", Path.GetDirectoryName(Path.GetDirectoryName(path))!], "", 20_000);

        Assert.NotEqual(0, missing.Exit);
        Assert.NotEqual(0, bothScopes.Exit);
        Assert.Contains("exactly one of", bothScopes.Stderr);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    sealed record GoldenInvocation(string[] Arguments, int Exit, string Stdout);

    sealed record Golden(
        string OldExecutableCommit,
        string OldExecutableSha256,
        string FixtureSql,
        Dictionary<string, GoldenInvocation> Invocations);

    static Golden LoadGolden()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fern.Registry.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("no test project root above " + AppContext.BaseDirectory);
        var path = Path.Combine(root, "Fixtures", "run_list", "golden.json");
        var golden = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }) ?? throw new InvalidOperationException($"{path} is empty");
        Assert.Equal("79af3ca5", golden.OldExecutableCommit);
        Assert.Equal(64, golden.OldExecutableSha256.Length);
        return golden;
    }

    /// <summary>The two columns the Recipe retirement removed from `runs`.</summary>
    static readonly string[] RetiredWithRecipe = ["recipe_hash", "recipe_name"];

    /// <summary>
    /// Go's report with <see cref="RetiredWithRecipe"/> removed wherever they
    /// appear, at any depth, matched by NAME rather than by position so a
    /// reordered report cannot strip the wrong field.
    /// </summary>
    static string WithoutRetiredRecipeFields(string goldenJson)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(goldenJson);
        return Strip(root)?.ToJsonString() ?? "null";

        static System.Text.Json.Nodes.JsonNode? Strip(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject source:
                    var kept = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (key, value) in source)
                    {
                        if (!RetiredWithRecipe.Contains(key))
                        {
                            kept[key] = Strip(value?.DeepClone());
                        }
                    }

                    return kept;
                case System.Text.Json.Nodes.JsonArray source:
                    var items = new System.Text.Json.Nodes.JsonArray();
                    foreach (var item in source)
                    {
                        items.Add(Strip(item?.DeepClone()));
                    }

                    return items;
                default:
                    return node?.DeepClone();
            }
        }
    }

    // The one main golden: the four reports the fern-home binary built at
    // 79af3ca5 printed over a production-migrated fixture Home, reproduced by the
    // real fern-registry process. Compared as typed JSON values, not bytes: the
    // two members carry the same names and values, and only their order inside
    // the object differs, which every caller parses rather than diffs.
    [Fact]
    public void ListRunsGoldenMatchesThePinnedFernHome()
    {
        var golden = LoadGolden();
        var path = Fixture();

        foreach (var (name, invocation) in golden.Invocations)
        {
            var arguments = invocation.Arguments
                .Select(argument => argument == "<fixture>" ? path : argument)
                .ToArray();
            var result = ProductionHome.RunFernRegistry(arguments, "", 20_000);
            Assert.True(result.Exit == invocation.Exit, $"{name}: exit {result.Exit}: {result.Stderr}");

            // recipe_hash and recipe_name are dropped from GO's report before the
            // comparison, and only those two: they left with the Recipe runtime on
            // 2026-09-08 (1e918d06), which also initialized fresh Registry Homes,
            // so no reader of the old shape survives. Their absence from the C#
            // report is asserted rather than assumed, so this cannot quietly
            // absorb a field that goes missing for some other reason — every
            // remaining key and value is still compared exactly.
            foreach (var retired in RetiredWithRecipe)
            {
                Assert.DoesNotContain(retired, result.Stdout, StringComparison.Ordinal);
            }

            using var old = JsonDocument.Parse(WithoutRetiredRecipeFields(invocation.Stdout));
            using var current = JsonDocument.Parse(result.Stdout);
            Assert.True(JsonElement.DeepEquals(old.RootElement, current.RootElement),
                $"{name} differs from the pinned fern-home report{Environment.NewLine}Go: {invocation.Stdout}{Environment.NewLine}C#: {result.Stdout}");
        }
    }
}
