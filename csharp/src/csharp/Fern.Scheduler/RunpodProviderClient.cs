using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;

namespace Fern.Scheduler;

public sealed record RunpodSshEndpoint(string Host, int PublicPort)
{
    public static RunpodSshEndpoint Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ProviderLifecycleRefusal("--ssh-endpoint must name host:public-port");
        }

        var endpoint = input.Trim();
        var userSeparator = endpoint.LastIndexOf('@');
        if (userSeparator >= 0)
        {
            endpoint = endpoint[(userSeparator + 1)..];
        }

        var portSeparator = endpoint.LastIndexOf(':');
        if (portSeparator <= 0
            || portSeparator == endpoint.Length - 1
            || endpoint.IndexOf(':') != portSeparator)
        {
            throw new ProviderLifecycleRefusal("--ssh-endpoint must be host:public-port with an explicit non-IPv6 public port");
        }

        var host = endpoint[..portSeparator];
        if (host.Any(char.IsWhiteSpace)
            || !int.TryParse(endpoint[(portSeparator + 1)..], out var port)
            || port is < 1 or > 65535)
        {
            throw new ProviderLifecycleRefusal("--ssh-endpoint must have a non-empty host and a public port from 1 through 65535");
        }

        return new RunpodSshEndpoint(host, port);
    }
}

/// <summary>Only outcomes relevant to establishing a provider terminal receipt.</summary>
public enum RunpodPodObservation
{
    Exited,
    PresentNotExited,
    Missing,
}

/// <summary>
/// Credentials are a local input.  This is intentionally absent from every
/// Registry record and report type, and callers must never render it.
/// </summary>
public sealed record RunpodProviderConfig(string ApiKey, Uri ApiUrl)
{
    public static RunpodProviderConfig ReadExisting(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProviderLifecycleRefusal("cannot read the explicit RunPod config path");
        }

        if (lines.Length > 1_024)
        {
            throw new ProviderLifecycleRefusal("RunPod config is too large for the two-key lifecycle credential reader");
        }

        string? apiKey = null;
        string? apiUrl = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = TomlScalar(trimmed[(separator + 1)..]);
            switch (key)
            {
                case "apikey" when apiKey is null:
                    apiKey = value;
                    break;
                case "apiurl" when apiUrl is null:
                    apiUrl = value;
                    break;
                case "apikey" or "apiurl":
                    throw new ProviderLifecycleRefusal("RunPod config repeats a lifecycle credential key");
            }
        }

        return FromValues(apiKey, apiUrl, "RunPod config");
    }

    public static RunpodProviderConfig ReadExplicitEnvironment(string variableName, string apiUrl)
    {
        if (variableName.Any(char.IsWhiteSpace))
        {
            throw new ProviderLifecycleRefusal("--api-token-env must name one environment variable without whitespace");
        }

        return FromValues(Environment.GetEnvironmentVariable(variableName), apiUrl, "--api-token-env");
    }

    static RunpodProviderConfig FromValues(string? apiKey, string? apiUrl, string source)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderLifecycleRefusal($"{source} did not provide a RunPod API token");
        }

        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || endpoint.UserInfo.Length != 0
            || endpoint.Query.Contains("api_key=", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderLifecycleRefusal("RunPod apiurl must be a credential-free absolute HTTPS GraphQL endpoint");
        }

        return new RunpodProviderConfig(apiKey, endpoint);
    }

    static string TomlScalar(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
        {
            var closing = value.IndexOf(value[0], 1);
            if (closing < 0)
            {
                throw new ProviderLifecycleRefusal("RunPod config contains an unterminated credential scalar");
            }

            var tail = value[(closing + 1)..].Trim();
            if (tail.Length != 0 && !tail.StartsWith('#'))
            {
                throw new ProviderLifecycleRefusal("RunPod config has unexpected text after a credential scalar");
            }

            return value[1..closing];
        }

        var comment = value.IndexOf('#');
        return (comment >= 0 ? value[..comment] : value).Trim();
    }
}

/// <summary>
/// Bounded GraphQL adapter.  It reduces provider JSON at the HTTP boundary:
/// a caller gets a unique immutable Pod id, an exact EXITED response, or a
/// typed absence/non-terminal observation.  Raw JSON and credentials do not
/// leave this file.
/// </summary>
public sealed class RunpodGraphQlClient
{
    const string ResolveEndpointQuery =
        "query ResolvePodForSshEndpoint { myself { pods { id runtime { ports { ip publicPort } } } } }";
    const string StopPodQuery =
        "mutation StopPod($podId: String!) { podStop(input: { podId: $podId }) { id desiredStatus } }";
    const string ObservePodQuery =
        "query ObservePod { myself { pods { id desiredStatus } } }";

    readonly HttpClient client;

    public RunpodGraphQlClient(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string ResolvePodForSshEndpoint(
        RunpodProviderConfig config,
        RunpodSshEndpoint endpoint,
        TimeSpan deadline)
    {
        using var document = Send(config, ResolveEndpointQuery, variables: null, deadline);
        var pods = Pods(document.RootElement);
        var matchingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pod in pods.EnumerateArray())
        {
            var podId = RequiredString(pod, "id", "RunPod inventory Pod");

            if (!pod.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object
                || !runtime.TryGetProperty("ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            {
                // A stopped or provisioning Pod can have no live runtime ports.
                // It cannot be the configured SSH endpoint, so it is not an
                // inventory contract failure for another Pod that is.
                continue;
            }

            foreach (var port in ports.EnumerateArray())
            {
                if (port.ValueKind == JsonValueKind.Object
                    && port.TryGetProperty("ip", out var ip)
                    && ip.ValueKind == JsonValueKind.String
                    && string.Equals(ip.GetString(), endpoint.Host, StringComparison.OrdinalIgnoreCase)
                    && port.TryGetProperty("publicPort", out var publicPort)
                    && publicPort.TryGetInt32(out var value)
                    && value == endpoint.PublicPort)
                {
                    matchingIds.Add(podId);
                }
            }
        }

        return matchingIds.Count switch
        {
            1 => matchingIds.Single(),
            0 => throw new RunpodProviderProtocolException("RunPod inventory has no Pod matching the configured SSH endpoint"),
            _ => throw new RunpodProviderProtocolException("RunPod inventory maps the configured SSH endpoint to more than one Pod"),
        };
    }

    public ProviderTerminalResponse StopPod(RunpodProviderConfig config, string podId, TimeSpan deadline)
    {
        using var document = Send(config, StopPodQuery, new StopPodVariables(podId), deadline);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("podStop", out var stopped)
            || stopped.ValueKind != JsonValueKind.Object)
        {
            throw new RunpodProviderProtocolException("RunPod stop response omitted podStop");
        }

        var responsePodId = RequiredString(stopped, "id", "RunPod stop response");
        var desiredStatus = RequiredString(stopped, "desiredStatus", "RunPod stop response");
        if (!string.Equals(responsePodId, podId, StringComparison.Ordinal)
            || !string.Equals(desiredStatus, "EXITED", StringComparison.Ordinal))
        {
            throw new RunpodProviderProtocolException("RunPod stop response did not match the requested Pod id and desiredStatus EXITED");
        }

        return new ProviderTerminalResponse(responsePodId, ProviderDesiredStatus.Exited);
    }

    public RunpodPodObservation ObserveStoredPod(RunpodProviderConfig config, string storedPodId, TimeSpan deadline)
    {
        using var document = Send(config, ObservePodQuery, variables: null, deadline);
        var pods = Pods(document.RootElement);
        var matching = pods.EnumerateArray()
            .Where(pod => string.Equals(RequiredString(pod, "id", "RunPod reconciliation Pod"), storedPodId, StringComparison.Ordinal))
            .ToList();
        if (matching.Count == 0)
        {
            return RunpodPodObservation.Missing;
        }

        if (matching.Count != 1)
        {
            throw new RunpodProviderProtocolException("RunPod reconciliation returned the stored Pod id more than once");
        }

        return string.Equals(
            RequiredString(matching[0], "desiredStatus", "RunPod reconciliation Pod"),
            "EXITED",
            StringComparison.Ordinal)
            ? RunpodPodObservation.Exited
            : RunpodPodObservation.PresentNotExited;
    }

    JsonDocument Send(RunpodProviderConfig config, string query, object? variables, TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero)
        {
            throw new RunpodProviderProtocolException("RunPod request deadline must be positive");
        }

        using var cancellation = new CancellationTokenSource(deadline);
        using var request = new HttpRequestMessage(HttpMethod.Post, WithApiKey(config));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new GraphQlRequest(query, variables), RegistryJson.Options),
            Encoding.UTF8,
            "application/json");
        try
        {
            using var response = client.Send(request, cancellation.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new RunpodProviderProtocolException(
                    "RunPod did not return an HTTP 200 GraphQL response; no terminal receipt was accepted");
            }

            using var body = response.Content.ReadAsStream(cancellation.Token);
            var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() != 0)
            {
                document.Dispose();
                throw new RunpodProviderProtocolException("RunPod GraphQL returned errors; no terminal receipt was accepted");
            }

            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new RunpodProviderProtocolException("RunPod GraphQL response omitted data");
            }

            return document;
        }
        catch (OperationCanceledException)
        {
            throw new RunpodProviderNetworkException("RunPod request exceeded its declared deadline");
        }
        catch (HttpRequestException)
        {
            throw new RunpodProviderNetworkException("RunPod request could not complete on the provider network");
        }
        catch (IOException)
        {
            throw new RunpodProviderNetworkException("RunPod response could not be read before its terminal receipt was verified");
        }
        catch (JsonException)
        {
            throw new RunpodProviderProtocolException("RunPod response was not valid typed GraphQL JSON");
        }
    }

    static Uri WithApiKey(RunpodProviderConfig config)
    {
        var builder = new UriBuilder(config.ApiUrl);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing)
            ? "api_key=" + Uri.EscapeDataString(config.ApiKey)
            : existing + "&api_key=" + Uri.EscapeDataString(config.ApiKey);
        return builder.Uri;
    }

    static JsonElement Pods(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("myself", out var myself)
            || myself.ValueKind != JsonValueKind.Object
            || !myself.TryGetProperty("pods", out var pods)
            || pods.ValueKind != JsonValueKind.Array)
        {
            throw new RunpodProviderProtocolException("RunPod inventory response omitted myself.pods");
        }

        return pods;
    }

    static string RequiredString(JsonElement value, string name, string context)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new RunpodProviderProtocolException($"{context} omitted {name}");
        }

        var result = property.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new RunpodProviderProtocolException($"{context} gave an empty {name}");
        }

        return result;
    }

    sealed record GraphQlRequest(string Query, object? Variables);

    sealed record StopPodVariables([property: JsonPropertyName("podId")] string PodId);
}

public sealed class ProviderLifecycleRefusal(string message) : Exception(message);

public abstract class RunpodProviderException(string message) : Exception(message);

public sealed class RunpodProviderNetworkException(string message) : RunpodProviderException(message);

public sealed class RunpodProviderProtocolException(string message) : RunpodProviderException(message);
