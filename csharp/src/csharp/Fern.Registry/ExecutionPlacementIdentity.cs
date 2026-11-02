using System.Security.Cryptography;
using System.Text;

namespace Fern.Registry;

/// <summary>
/// Site name, machine generation, GPU, SSH endpoint, and environment are
/// five different things. A profile is configuration for reaching them, not
/// any of them.
///
/// Site name is the human label (<c>runpod-a4500</c>). It survives a Pod
/// rebuild as a name only.
///
/// Machine generation is executor + observed hostname + GPU UUID. A new
/// UUID on the same name is a new machine and does not inherit the old
/// admission.
///
/// Device is the GPU UUID. It is the capacity key.
///
/// Endpoint session is executor + SSH host/port. It may change when a Pod
/// reconnects and is not a new Site or a new GPU.
///
/// Environment is a placement constraint on that machine, not a second GPU.
/// </summary>
public static class ExecutionPlacementIdentity
{
    public static string MachineGeneration(string executor, string hostname, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(executor))
        {
            throw new ArgumentException("machine generation requires an executor");
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new ArgumentException("machine generation requires an observed hostname");
        }

        return Hash(
            "fern.machine_generation.v1",
            executor.Trim(),
            hostname.Trim(),
            string.IsNullOrWhiteSpace(deviceId) ? "cpu" : deviceId.Trim());
    }

    public static string EndpointSession(string executor, string? sshDestination)
    {
        if (string.IsNullOrWhiteSpace(executor))
        {
            throw new ArgumentException("endpoint session requires an executor");
        }

        return Hash(
            "fern.endpoint_session.v1",
            executor.Trim(),
            string.IsNullOrWhiteSpace(sshDestination) ? "local" : sshDestination.Trim());
    }

    /// <summary>
    /// CPython major.minor, which is the ABI the catalog admits. Exact patch
    /// is an observation, not this identity.
    /// </summary>
    public static string PythonAbi(string pythonVersion)
    {
        if (string.IsNullOrWhiteSpace(pythonVersion))
        {
            throw new ArgumentException("Python ABI requires an observed version");
        }

        var token = pythonVersion.Trim();
        var space = token.IndexOf(' ');
        if (space > 0)
        {
            token = token[..space];
        }

        var parts = token.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(Digits(parts[1]), out var minor))
        {
            throw new ArgumentException($"Python version {pythonVersion} is not a CPython major.minor");
        }

        return $"cpython-{major}.{minor}";
    }

    static string Digits(string value)
    {
        var end = 0;
        while (end < value.Length && char.IsDigit(value[end]))
        {
            end++;
        }

        return end == 0 ? value : value[..end];
    }

    static string Hash(params string[] parts)
    {
        var material = string.Join('\u001f', parts);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
