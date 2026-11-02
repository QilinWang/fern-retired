using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Fern.Experiment;

// BDN 0.15.8 does not recognize net11.0 for generated out-of-process projects.
// Mutate only the toolchain so --job Dry remains the caller's selected run length.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).AsMutator());
BenchmarkSwitcher.FromAssembly(typeof(CanonicalJsonBenchmarks).Assembly).Run(args, config);

// The benchmark is a separate executable: production binaries never load its dependencies.
[MemoryDiagnoser]
public class CanonicalJsonBenchmarks
{
    JsonDocument document = null!;

    [Params(8, 128)]
    public int Fields { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Reverse insertion order exercises sorting; nested values include Unicode and raw numbers.
        document = JsonDocument.Parse("{" + string.Join(",", Enumerable.Range(0, Fields).Reverse()
            .Select(index => $"\"field_{index:D4}\":{{\"研究\":[-0.0,1e-7,\"Ω <&>\"],\"enabled\":true}}")) + "}");
    }

    [Benchmark]
    public byte[] CanonicalBytes() => CanonicalJson.Bytes(document.RootElement);

    [Benchmark]
    public string CanonicalDigest() => CanonicalJson.DigestHex(document.RootElement);

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();
}
