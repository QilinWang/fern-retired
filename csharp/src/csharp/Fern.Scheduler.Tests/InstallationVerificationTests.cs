using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class InstallationVerificationTests
{
    [Fact]
    public void VerifyInstallationAcceptsExactPublishedBytes()
    {
        var fixture = InstallationFixture.Create();

        var accepted = Assert.IsType<InstallationVerificationOutcome.Accepted>(
            InstallationVerification.VerifyMaterialized(
                fixture.ReceiptPath,
                fixture.RegistryPath));

        Assert.Equal(fixture.BinarySetId, accepted.Installation.BinarySetId);
        // CHG 2026-09-14: fern-stage-runner left the set with the Rust-era
        // contracts its two verbs were built on, so this reads back the
        // operator binary instead of the runner's.
        Assert.Equal(fixture.RunPath, accepted.Installation.RunBinaryPath);
    }

    [Fact]
    public void VerifyInstallationReadsAReceiptThatCarriesAFieldThisReaderDoesNotDeclare()
    {
        // Owner, 2026-09-20: "get rid of the refusal if not needed". A newer
        // publisher wrote stage_runner_binary_sha256 into 26 of the 33 receipts
        // on the owner's machine; this reader does not declare that field and
        // refused the whole file, which blocked installing a study source while
        // protecting nothing -- the binary paths and digests it does read are
        // verified one by one either way.
        var fixture = InstallationFixture.Create();
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(fixture.ReceiptPath, Encoding.UTF8))!;
        document["stage_runner_binary_sha256"] = JsonSerializer.SerializeToElement(new string('a', 64));
        File.WriteAllText(fixture.ReceiptPath, JsonSerializer.Serialize(document), Encoding.UTF8);

        var accepted = Assert.IsType<InstallationVerificationOutcome.Accepted>(
            InstallationVerification.VerifyMaterialized(fixture.ReceiptPath, fixture.RegistryPath));

        Assert.Equal(fixture.BinarySetId, accepted.Installation.BinarySetId);
    }

    [Fact]
    public void VerifyInstallationRefusesReceiptForAnotherRegistry()
    {
        var fixture = InstallationFixture.Create();
        var otherRegistry = TestRegistry.CreateRegistry();

        var refused = Assert.IsType<InstallationVerificationOutcome.Refused>(
            InstallationVerification.VerifyMaterialized(
                fixture.ReceiptPath,
                otherRegistry));

        Assert.Contains("not requested Registry Home", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(otherRegistry, "SELECT COUNT(*) FROM supervisors"));
    }

    // CHG 2026-09-14: the tampered-binary check that stood here named
    // fern-stage-runner, which left the installed set with the Rust-era stage
    // contracts its verbs were built on. The same guarantee for the members
    // that remain is asserted by the refusal tests below, which name a binary
    // by path rather than by member.

    /// <summary>
    /// A machine installed before the card compiler joined the set is told which
    /// binary is missing, by name. Anything less reads as "reinstall something".
    /// </summary>
    [Fact]
    public void AnInstallationWithoutTheCardCompilerIsRefusedByName()
    {
        var fixture = InstallationFixture.Create(withCompiler: false);

        var refused = Assert.IsType<InstallationVerificationOutcome.Refused>(
            InstallationVerification.VerifyMaterialized(fixture.ReceiptPath, fixture.RegistryPath));

        Assert.Contains("fern-compile", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("Install the control plane again", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worker machine reads the three executables it runs out of the receipt
    /// at its installed root. It has the receipt and the binaries; it does not
    /// necessarily have the Home they were installed against, so this read is
    /// not bound to one.
    /// </summary>
    [Fact]
    public void AWorkerReadsItsExecutablesOutOfTheInstallationReceipt()
    {
        var fixture = InstallationFixture.Create();

        var members = InstalledControlReader.Members(fixture.ReceiptPath);

        Assert.True(members.IsOk, members.RefusalDetail);
        Assert.Equal(fixture.RunPath, members.Value.RunBinaryPath);
        Assert.Equal(fixture.CompilePath, members.Value.CompileBinaryPath);
        Assert.True(File.Exists(members.Value.SiteBinaryPath));
    }

    /// <summary>
    /// A receipt with no card compiler is refused by that name. The worker
    /// would otherwise prepare an environment whose first cell refuses at its
    /// identity call, with nothing saying which binary was missing.
    /// </summary>
    [Fact]
    public void AReceiptWithNoCardCompilerIsRefusedByName()
    {
        var fixture = InstallationFixture.Create(withCompiler: false);

        var members = InstalledControlReader.Members(fixture.ReceiptPath);

        Assert.False(members.IsOk);
        Assert.Contains("fern-compile", members.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("install the control plane on this machine again", members.RefusalDetail, StringComparison.Ordinal);
    }

    /// <summary>A receipt that names a binary this machine does not hold says so, with the path.</summary>
    [Fact]
    public void AReceiptNamingAnAbsentBinaryIsRefusedWithItsPath()
    {
        var fixture = InstallationFixture.Create();
        File.Delete(fixture.CompilePath);

        var members = InstalledControlReader.Members(fixture.ReceiptPath);

        Assert.False(members.IsOk);
        Assert.Contains(fixture.CompilePath, members.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("which this machine does not hold", members.RefusalDetail, StringComparison.Ordinal);
    }

    internal sealed record InstallationFixture(
        string ReceiptPath,
        string RegistryPath,
        string RunPath,
        string CompilePath,
        string BinarySetId)
    {
        public static InstallationFixture Create(bool withCompiler = true)
        {
            var root = Directory.CreateTempSubdirectory("fern-control-plane-installation-").FullName;
            var registry = TestRegistry.CreateRegistry();
            var members = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fern-experiment"] = Write(root, "fern-experiment", "experiment bytes"),
                ["fern-run"] = Write(root, "fern-run", "run bytes"),
                ["fern-compile"] = Write(root, "fern-compile", "compile bytes"),
                ["fern-site"] = Write(root, "fern-site", "site bytes"),
            };
            var digests = members.ToDictionary(item => item.Key, item => Sha256File(item.Value), StringComparer.Ordinal);
            var binarySetId = ComputeBinarySetId(digests);
            var receipt = new
            {
                schema = ControlPlaneInstallationContract.ReceiptSchema,
                installed_at_unix_ms = 1L,
                registry,
                work_root = root,
                deploy_root = root,
                profiles_root = root,
                ssh_helper = members["fern-run"],
                site_binary_sha256 = digests["fern-site"],
                site_binary_path = members["fern-site"],
                experiment_binary_sha256 = digests["fern-experiment"],
                experiment_binary_path = members["fern-experiment"],
                run_binary_sha256 = digests["fern-run"],
                run_binary_path = members["fern-run"],
                compile_binary_sha256 = withCompiler ? digests["fern-compile"] : null,
                compile_binary_path = withCompiler ? members["fern-compile"] : null,
                binary_set_id = binarySetId,
                activation = "test",
                // Slice 10: the installer records each member's input digest;
                // a verifier that refuses the field refuses every receipt the
                // installer writes now.
                member_inputs = new Dictionary<string, string>
                {
                    ["fern-run"] = "sha256:" + new string('1', 64),
                    ["fern-site"] = "sha256:" + new string('2', 64),
                },
            };
            var receiptDirectory = Directory.CreateDirectory(Path.Combine(root, "receipts"));
            var receiptPath = Path.Combine(receiptDirectory.FullName, binarySetId["sha256:".Length..] + ".json");
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt), Encoding.UTF8);
            return new InstallationFixture(
                receiptPath,
                registry,
                members["fern-run"],
                members["fern-compile"],
                binarySetId);
        }

        static string Write(string root, string name, string contents)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, contents, Encoding.UTF8);
            return Path.GetFullPath(path);
        }

        static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

        static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        static string ComputeBinarySetId(IReadOnlyDictionary<string, string> digests)
        {
            using var material = new MemoryStream();
            material.Write("fern.installed-binary-set.v1\0"u8);
            foreach (var member in digests.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                material.Write(Encoding.UTF8.GetBytes(member.Key));
                material.WriteByte(0);
                material.Write(Encoding.UTF8.GetBytes(member.Value));
                material.WriteByte(0);
            }

            return "sha256:" + Sha256(material.ToArray());
        }
    }

    static long Scalar(string registryPath, string sql)
    {
        using var reader = RegistryHome.OpenReader(registryPath);
        using var command = reader.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

}
