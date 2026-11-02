using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Fern.Registry;

namespace Fern.Install;

/// <summary>Selects existing verified installed artifacts; it never builds or initializes a Home.</summary>
public static class RuntimeSelectionPublication
{
    public static RuntimeSelection Select(string receiptPath, string registryPath, string registryBinary, string deploymentRoot)
    {
        var verified = InstallationVerification.VerifyMaterialized(receiptPath, registryPath);
        if (verified is not InstallationVerificationOutcome.Accepted accepted)
            throw new InvalidOperationException(((InstallationVerificationOutcome.Refused)verified).Detail);
        using var reader = RegistryHome.OpenReader(registryPath);
        registryBinary = Path.GetFullPath(registryBinary);
        using var receipt = JsonDocument.Parse(File.ReadAllText(registryBinary + ".receipt.json"));
        var root = receipt.RootElement;
        using var binary = File.OpenRead(registryBinary);
        var digest = Convert.ToHexStringLower(SHA256.HashData(binary));
        // CHG 2026-09-11 night (slice 10): v3 adds input_digest to the receipt;
        // a v2 receipt from an earlier publish still names a clean build and
        // still selects.
        if (root.GetProperty("schema").GetString() is not ("fern.registry-binary-publish.v2" or "fern.registry-binary-publish.v3")
            || root.GetProperty("published_path").GetString() != registryBinary
            || root.GetProperty("sha256").GetString() != digest
            || root.GetProperty("built_from_dirty_tree").GetBoolean()
            || root.GetProperty("byte_size").GetInt64() != binary.Length)
            throw new InvalidOperationException("Registry binary does not match a clean published installation receipt");
        var installation = accepted.Installation;
        var selection = new RuntimeSelection(RuntimeSelection.CurrentSchema, Path.GetFullPath(registryPath),
            installation.RunBinaryPath, registryBinary, installation.ExperimentBinaryPath, installation.CompileBinaryPath, Path.GetFullPath(receiptPath),
            installation.BinarySetId, digest, root.GetProperty("built_from_commit").GetString()!,
            root.GetProperty("publisher_version").GetString()!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(selection);
        var identity = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Path.GetFullPath(deploymentRoot), "runtime-selections", identity);
        var current = Path.Combine(Path.GetFullPath(deploymentRoot), "current");
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        using var exclusion = new FileStream(Path.Combine(deploymentRoot, "runtime-selection.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is null)
            throw new InvalidOperationException("Runtime selection refuses to replace a real current directory");
        if (!Directory.Exists(directory))
        {
            var staging = directory + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                File.WriteAllBytes(Path.Combine(staging, "runtime.json"), bytes);
                File.CreateSymbolicLink(Path.Combine(staging, "fern-run"), selection.RunBinary);
                File.CreateSymbolicLink(Path.Combine(staging, "fern-registry"), selection.RegistryBinary);
                File.CreateSymbolicLink(Path.Combine(staging, "fern-experiment"), selection.ExperimentBinary);
                Directory.Move(staging, directory);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        if (RuntimeSelection.Read(Path.Combine(directory, "runtime.json")) != selection)
            throw new InvalidOperationException("Existing runtime selection differs from its declared identity");
        var temporary = current + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateSymbolicLink(temporary, directory);
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException("Runtime selection publication requires the Linux installation host");
            if (Rename(temporary, current) != 0)
                throw new IOException($"Atomic runtime selection failed: {Marshal.GetLastPInvokeError()}");
        }
        finally { if (new DirectoryInfo(temporary).LinkTarget is not null) Directory.Delete(temporary); }
        return selection;
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    static extern int Rename(string source, string destination);
}
