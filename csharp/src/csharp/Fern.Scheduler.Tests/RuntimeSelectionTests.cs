using System.Security.Cryptography;
using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class RuntimeSelectionTests
{
    [Fact]
    public void SelectionPublishesOneContextAndRefusesChangedInstalledBytes()
    {
        var fixture = InstallationVerificationTests.InstallationFixture.Create();
        var root = Path.GetDirectoryName(fixture.RunPath)!;
        var registryBinary = Path.Combine(root, "fern-registry");
        File.Copy(fixture.RunPath, registryBinary);
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(registryBinary)));
        File.WriteAllText(registryBinary + ".receipt.json", JsonSerializer.Serialize(new
        {
            schema = "fern.registry-binary-publish.v2", published_path = registryBinary,
            sha256 = digest, built_from_commit = "test-source", built_from_dirty_tree = false,
            publisher_version = "test", byte_size = new FileInfo(registryBinary).Length
        }));
        var selected = RuntimeSelectionPublication.Select(fixture.ReceiptPath, fixture.RegistryPath,
            registryBinary, root);
        var path = Path.Combine(root, "current", "runtime.json");
        Assert.Equal(selected, RuntimeSelection.Read(path));
        Assert.Equal(fixture.CompilePath, selected.CompileBinary);  // the compiler is pinned with the rest: a cell never mints with another commit's
        Assert.Equal(fixture.RunPath, File.ResolveLinkTarget(Path.Combine(root, "current", "fern-run"), true)!.FullName);
        Assert.Equal(registryBinary, File.ResolveLinkTarget(Path.Combine(root, "current", "fern-registry"), true)!.FullName);
        Assert.Equal(selected, RuntimeSelectionPublication.Select(fixture.ReceiptPath, fixture.RegistryPath, registryBinary, root));
        File.AppendAllText(fixture.RunPath, "changed");
        Assert.Throws<InvalidOperationException>(() => RuntimeSelectionPublication.Select(
            fixture.ReceiptPath, fixture.RegistryPath, registryBinary, root));
        Assert.Equal(selected, RuntimeSelection.Read(path));
    }

    [Fact]
    public void MissingInvalidAndForeignSelectionDoNotFallBackOrCreateADatabase()
    {
        var root = Directory.CreateTempSubdirectory("fern-selection-refusal-").FullName;
        try
        {
            var path = Path.Combine(root, "runtime.json");
            Assert.Throws<RegistryFault>(() => RuntimeSelection.Read(path));
            File.WriteAllText(path, "{}");
            Assert.Throws<RegistryFault>(() => RuntimeSelection.Read(path));
            File.WriteAllText(path, "{bad");
            Assert.Throws<RegistryFault>(() => RuntimeSelection.Read(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
