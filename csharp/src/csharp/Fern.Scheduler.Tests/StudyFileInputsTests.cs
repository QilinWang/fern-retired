using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Fern.Scheduler.Tests;

public sealed class StudyFileInputsTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("fern-study-inputs-").FullName;
    string Compiled(string path, string hash, ulong size)
    {
        var output = Path.Combine(root, "compiled.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new { schema = "fern.compiled-study.v1",
            dataset = new { inputs = new { source = new { path, sha256 = hash, byte_count = size } } } }));
        return output;
    }

    [Fact]
    public void DeclaredIgnoredDataIsVerifiedByBytesWithoutKnowingTheDatasetName()
    {
        Directory.CreateDirectory(Path.Combine(root, "datasets"));
        File.WriteAllText(Path.Combine(root, ".gitignore"), "datasets/\n");
        var bytes = "state\n3\n"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "datasets", "sample.csv"), bytes);
        var declared = StudyFileInputs.Read(Compiled("datasets/sample.csv", Convert.ToHexStringLower(SHA256.HashData(bytes)), (ulong)bytes.Length), root);
        StudyFileInputs.Verify(root, declared);
        Assert.Equal("datasets/sample.csv", Assert.Single(declared).Path);
        File.WriteAllText(Path.Combine(root, "datasets", "sample.csv"), "state\n4\n");
        Assert.Throws<StudySourceRegistration.Refusal>(() => StudyFileInputs.Verify(root, declared));
    }

    [Theory]
    [InlineData("../outside.csv")]
    [InlineData("../../outside.csv")]
    public void DeclaredInputsCannotEscapeTheSourceRoot(string path)
    {
        Assert.Throws<StudySourceRegistration.Refusal>(() => StudyFileInputs.Read(Compiled(path, new string('0', 64), 0), root));
        Assert.Throws<StudySourceRegistration.Refusal>(() => StudyFileInputs.Verify(root, [new(path, new string('0', 64), 0)]));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
