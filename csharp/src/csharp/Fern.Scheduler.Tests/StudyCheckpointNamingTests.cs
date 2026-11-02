using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>Accepted producer bytes cross the public naming command, then Python's actual reader.</summary>
public sealed class StudyCheckpointNamingTests
{
    const string ConfigHash = "0123456789abcdef";
    const string Config = """{"schema":"fern.training.v2","execution":{"kind":"train","checkpoint_source":null},"model":{"type":"fern"},"runtime":{"dtype":"float32","amp_dtype":"none"}}""";
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed class Fixture : IDisposable
    {
        public string Registry { get; } = TestRegistry.CreateRegistry();
        public string Root => Path.GetDirectoryName(Registry)!;
        public string Store => Path.Combine(Root, "store");
        public string Result => Path.Combine(Root, "one.results.json");
        public string Weights => Path.Combine(Root, "one.model.safetensors");
        public long Attempt { get; }
        public long Epoch { get; }
        public byte[] Original { get; } = Encoding.UTF8.GetBytes("checkpoint-bytes");

        public Fixture(string? defect = null)
        {
            File.WriteAllBytes(Weights, Original);
            var config = JsonNode.Parse(Config)!;
            if (defect == "evaluation") config["execution"]!["kind"] = "evaluation_only";
            var result = new JsonObject
            {
                ["schema"] = "fern.study_unit_results.v4", ["study"] = "worked", ["cell"] = "one",
                ["config"] = config.DeepClone(), ["config_hash"] = ConfigHash,
                ["facts"] = new JsonObject
                {
                    ["retained_checkpoints"] = new JsonObject
                    {
                        ["one"] = new JsonObject
                        {
                            ["file"] = defect == "path" ? "../one.model.safetensors" : Path.GetFileName(Weights),
                            ["byte_size"] = Original.Length, ["sha256"] = Digest(Original), ["placement"] = "producer",
                        },
                    },
                },
            };
            if (defect == "config_body") result["config"]!["runtime"]!["dtype"] = "float64";
            if (defect == "null_digest") result["facts"]!["retained_checkpoints"]!["one"]!["sha256"] = null;
            File.WriteAllText(Result, result.ToJsonString());
            using var writer = RegistryHome.OpenWriter(Registry);
            // CHG 2026-09-26 (schema 21): a source is registered from a commit or a working tree, not under a digest.
            Assert.True(RegistryOperations.RegisterSource(writer, "test-commit", "[\"test.py\"]", RegistryOperations.RegisteredFromCommit).TryGetValue(out var source));
            Assert.True(RegistryOperations.RegisterSite(writer, "local", "{\"device\":\"cuda\",\"admitted_models\":[\"fern\"]}", "{\"slots\":1}").TryGetValue(out var site));
            TestRegistry.DeclareCodeFrom(writer, site.SiteId);
            var cell = new RegistryOperations.CompiledCell(0, "one", "data", 42, config.ToJsonString(), 
                "{\"device\":\"cuda\",\"requires_cuda\":true}", "{\"unit_results\":\"retained\"}");
            Assert.True(RegistryOperations.RegisterStudy(writer, "worked", 1, "{}", source.SourceId, [cell]).TryGetValue(out var study));
            Assert.True(RegistryOperations.TryStartAttempt(writer, study.CellIds[0], site.SiteId, "{}").TryGetValue(out var attempt));
            Attempt = attempt.AttemptId;
            Epoch = attempt.FencingEpoch;
        }

        public void Accept(bool reference)
        {
            using var writer = RegistryHome.OpenWriter(Registry);
            var result = reference ? new JsonObject
            {
                ["schema"] = "fern.saved_cell_result.v1", ["saved"] = true,
                ["database"] = "/not-opened/science.db", ["study"] = "worked", ["run"] = "one-run",
                ["reference"] = "retained-in-science-store", ["sha256"] = Digest(File.ReadAllBytes(Result)),
            }.ToJsonString() : File.ReadAllText(Result);
            Assert.True(RegistryOperations.FinishAttempt(writer, Attempt, Epoch, result).IsOk);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    static async Task<(int Code, string Output, string Error)> Command(Fixture fixture, string name = "shared", string revision = "1")
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Fern.Run.Cli/bin/Debug/net11.0/fern-run.dll"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { binary, "name-checkpoint", "--registry", fixture.Registry,
                     "--study", "worked", "--revision", revision, "--cell", "one", "--producer-results", fixture.Result,
                     "--checkpoint-store", fixture.Store, "--name", name }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "checkpoint naming did not terminate");
        return (process.ExitCode, await stdout, await stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedTrainingPublishesImmutableBytesThatPythonReads(bool reference)
    {
        using var fixture = new Fixture();
        fixture.Accept(reference);
        var registryBefore = File.ReadAllBytes(fixture.Registry);
        var named = await Command(fixture);
        Assert.True(named.Code == 0, named.Error);
        var directory = Path.Combine(fixture.Store, "checkpoints", "shared");
        Assert.Equal(fixture.Original, File.ReadAllBytes(Path.Combine(directory, "model.safetensors")));
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "receipt.json")));
        Assert.Equal(fixture.Attempt, receipt.RootElement.GetProperty("attempt").GetInt64());
        // CHG 2026-09-27 (config-hash-leaves, slice 4): the receipt carries the training configuration.
        Assert.True(JsonElement.DeepEquals(receipt.RootElement.GetProperty("config"), JsonDocument.Parse(Config).RootElement));
        Assert.Equal("test-commit", receipt.RootElement.GetProperty("commit").GetString());
        Assert.Equal("float32", receipt.RootElement.GetProperty("dtype").GetString());
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Weights));
        Assert.Equal(registryBefore, File.ReadAllBytes(fixture.Registry));
        var before = File.ReadAllBytes(Path.Combine(directory, "receipt.json"));
        var repeated = await Command(fixture);
        Assert.Equal(3, repeated.Code);
        Assert.Contains("already exists", repeated.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "receipt.json")));

        var python = new ProcessStartInfo("/home/user/.venv/bin/python") { RedirectStandardOutput = true, RedirectStandardError = true };
        python.Environment["PYTHONPATH"] = Path.Combine(TestRegistry.RepoRoot(), "python");
        python.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        foreach (var argument in new[] { "-c", "from pathlib import Path; import sys; from fern_train.checkpoint_store import verify_named_checkpoint; p,r=verify_named_checkpoint(Path(sys.argv[1]), 'shared'); assert isinstance(r['config'], dict) and r['config']['schema']; assert p.read_bytes()==b'checkpoint-bytes'", fixture.Store }) python.ArgumentList.Add(argument);
        using var reader = Process.Start(python)!;
        var readError = reader.StandardError.ReadToEndAsync();
        var exited = reader.WaitForExit(15000);
        if (!exited) reader.Kill(entireProcessTree: true);
        Assert.True(exited, "Python checkpoint reader did not terminate");
        Assert.True(reader.ExitCode == 0, await readError);
    }

    [Theory]
    [InlineData("unaccepted", "accepted")]
    [InlineData("result", "result")]
    [InlineData("weights", "changed")]
    // CHG 2026-09-27 (config-hash-leaves, slice 1): a differing hash beside an unchanged
    // configuration is no longer a defect; the configuration body is compared whole.
    [InlineData("config_body", "configuration")]
    [InlineData("evaluation", "training")]
    [InlineData("path", "filename")]
    [InlineData("link", "symlink")]
    [InlineData("result_link", "symlink")]
    [InlineData("store_link", "symlink")]
    [InlineData("null_digest", "sha256")]
    [InlineData("revision", "accepted")]
    [InlineData("name", "one path segment")]
    public async Task InvalidProducersNeverPublishAName(string defect, string reason)
    {
        using var fixture = new Fixture(defect);
        if (defect != "unaccepted") fixture.Accept(reference: true);
        if (defect == "result") File.AppendAllText(fixture.Result, " ");
        if (defect == "weights") File.WriteAllBytes(fixture.Weights, Encoding.UTF8.GetBytes("CHECKPOINT-BYTES"));
        if (defect == "link")
        {
            var other = Path.Combine(fixture.Root, "foreign-weights");
            File.Move(fixture.Weights, other);
            File.CreateSymbolicLink(fixture.Weights, other);
        }
        if (defect == "result_link")
        {
            var other = Path.Combine(fixture.Root, "foreign-result");
            File.Move(fixture.Result, other);
            File.CreateSymbolicLink(fixture.Result, other);
        }
        if (defect == "store_link")
        {
            var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "foreign-store"));
            Directory.CreateSymbolicLink(fixture.Store, other.FullName);
        }
        var named = await Command(fixture, defect == "name" ? "../escape" : "shared", defect == "revision" ? "2" : "1");
        Assert.Equal(3, named.Code);
        Assert.Contains(reason, named.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Store, "checkpoints", "shared")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Store, "escape")));
    }

    [Fact]
    public async Task RacingNamesPublishExactlyOneCompleteDirectory()
    {
        using var fixture = new Fixture();
        fixture.Accept(reference: false);
        var results = await Task.WhenAll(Command(fixture), Command(fixture));
        Assert.Equal([0, 3], results.Select(result => result.Code).Order().ToArray());
        var directory = Path.Combine(fixture.Store, "checkpoints", "shared");
        Assert.Equal(fixture.Original, File.ReadAllBytes(Path.Combine(directory, "model.safetensors")));
        Assert.True(File.Exists(Path.Combine(directory, "receipt.json")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(directory)!));
    }
}
