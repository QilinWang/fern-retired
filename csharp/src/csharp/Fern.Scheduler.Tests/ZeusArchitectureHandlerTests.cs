using System.Diagnostics;
using System.Text.Json;
using Fern.Experiment;
using Fern.Registry;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The V2 architecture vocabulary is declared by Python and placed by C#, so
/// the two must agree about which models exist and which of them needs an
/// interpreter of its own. These tests run the live capability provider — not
/// a saved copy of it — because a fixture would record the day it was written
/// rather than what the worker would execute now.
/// </summary>
public sealed class ZeusArchitectureHandlerTests
{
    [Fact]
    public void every_python_architecture_handler_names_a_model_csharp_can_place()
    {
        var handlers = LiveArchitectureHandlers();

        Assert.NotEmpty(handlers);
        foreach (var handler in handlers)
        {
            var name = handler.GetProperty("handler").GetString()!;
            var modelType = handler.GetProperty("emits").GetProperty("model.type").GetString()!;
            Assert.True(
                Placement.KnownModelTypes.Contains(modelType),
                $"architecture handler {name} emits model.type \"{modelType}\", which no Site can be told to admit");
        }
    }

    [Fact]
    public void zeus_is_the_one_architecture_that_asks_for_its_own_interpreter()
    {
        var handlers = LiveArchitectureHandlers();

        foreach (var handler in handlers)
        {
            var name = handler.GetProperty("handler").GetString()!;
            var environment = handler.GetProperty("requires_interpreter_environment").GetString();
            Assert.Equal(name == "forecast.zeus" ? "zeus_flash_attention" : "default", environment);
        }

        Assert.Contains(handlers, handler => handler.GetProperty("handler").GetString() == "forecast.zeus");
    }

    [Fact]
    public void the_zeus_card_pins_the_same_weights_the_v1_knobs_name()
    {
        var zeus = LiveArchitectureHandlers()
            .Single(handler => handler.GetProperty("handler").GetString() == "forecast.zeus");
        var properties = zeus.GetProperty("parameter_schema").GetProperty("properties");

        // Both routes must name the same published bytes: a V2 cell that
        // forecast from a different revision than the campaign's V1 Zeus cells
        // would be a different model wearing the same row.
        // CHG 2026-09-05: frozen published-weight identities, not runtime defaults.
        // The same values are in fixtures/study/config-builder-reference.json (Zeus 336/64).
        Assert.Equal("GestaltCog/zeus", properties.GetProperty("hf_model_id").GetProperty("const").GetString());
        Assert.Equal("4b458a2ec89b30c114da99d0837e33e4343070f3", properties.GetProperty("hf_revision").GetProperty("const").GetString());
        Assert.Equal("point", zeus.GetProperty("emits").GetProperty("forecast.representation").GetString());

        // Zeus fits nothing, so its card carries no optimizer knob and no
        // epoch; what it does carry is the fan it emits, which is fixed.
        var levels = properties.GetProperty("quantile_levels").GetProperty("const")
            .EnumerateArray().Select(level => level.GetDouble()).ToArray();
        Assert.Equal(new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 }, levels);
    }

    /// <summary>
    /// Import the declared capability provider and return its architecture
    /// handlers. This fails rather than skips when the interpreter is absent:
    /// a vocabulary test that quietly passes on a machine without Python is
    /// the decoration this pair exists to avoid.
    /// </summary>
    static IReadOnlyList<JsonElement> LiveArchitectureHandlers()
    {
        var root = RepositoryRoot();
        var python = Environment.GetEnvironmentVariable("FERN_TEST_PYTHON") ?? "/home/user/.venv/bin/python";
        Assert.True(File.Exists(python), $"the V2 capability vocabulary is Python's, and this needs {python}");

        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            "import json, fern_stage_runtime as r; print(json.dumps(r.fern_experiment_capabilities_v2()))");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"capability provider exited {process.ExitCode}: {stderr}");

        var capabilities = JsonDocument.Parse(stdout);
        return capabilities.RootElement.GetProperty("handlers")
            .EnumerateArray()
            .Where(handler => handler.GetProperty("role").GetString() == "architecture")
            .Select(handler => handler.Clone())
            .ToArray();
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
