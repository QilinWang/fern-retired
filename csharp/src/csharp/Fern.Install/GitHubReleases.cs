using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fern.Install;

/// <summary>
/// The GitHub release API, for the one thing Fern uses it for: a private
/// repository's release holding this machine's built control binaries, so a
/// rented machine downloads its runtime instead of being pushed one.
///
/// CHG 2026-09-12: measured 2026-09-09 by the owner, a Pod pulls a release
/// asset at 22.6 MB/s and an AutoDL machine at 4.9 MB/s, while pushing 45 MB
/// up from a home connection is the slow direction and a Pod restart destroys
/// what was pushed. The repository stays private, because a single-file .NET
/// binary decompiles cleanly and a public release of it is a public source
/// release; a private asset needs a token on the request, which is a hundred
/// bytes the machine can hold rather than 45 MB it cannot keep.
///
/// This is HTTP, not the `gh` command: the machine that downloads has no `gh`
/// and no `dotnet`, only the one binary it just fetched. The token is read
/// from a file or the environment, and on the publishing machine it may come
/// from `gh auth token`, which is where the owner's credential already lives.
/// </summary>
public sealed class GitHubReleases : IDisposable
{
    public const string TokenVariable = "FERN_GITHUB_TOKEN";

    const string Api = "https://api.github.com";
    const string Uploads = "https://uploads.github.com";

    readonly HttpClient client;

    public GitHubReleases(string token, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("a GitHub token is required to read or write a private repository's releases");
        }
        client = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fern-run");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Dispose() => client.Dispose();

    /// <summary>
    /// The token, from the file named, then the environment, then `gh auth
    /// token` on a machine that has it. Refuses by naming all three rather
    /// than reading a credential nobody chose.
    /// </summary>
    public static string ResolveToken(string? tokenFile)
    {
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            var path = Path.GetFullPath(tokenFile);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"token file {path} does not exist");
            }
            var fromFile = File.ReadAllText(path).Trim();
            if (fromFile.Length == 0)
            {
                throw new InvalidOperationException($"token file {path} is empty");
            }
            return fromFile;
        }
        if (Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment.Trim();
        }
        var borrowed = GhAuthToken();
        if (borrowed is not null)
        {
            return borrowed;
        }
        throw new InvalidOperationException(
            $"no GitHub token: pass --token-file, set {TokenVariable}, or sign in with `gh auth login` on this machine");
    }

    static string? GhAuthToken()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            var token = output.Trim();
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>The release with this tag, or null when the repository has none.</summary>
    public JsonObject? Release(string repository, string tag)
    {
        using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}"));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return Object(response, $"read release {tag} of {repository}");
    }

    /// <summary>Create the release, tagging <paramref name="target"/>; the tag must not already exist.</summary>
    public JsonObject CreateRelease(string repository, string tag, string target, string name, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/repos/{repository}/releases")
        {
            Content = Json(new JsonObject
            {
                ["tag_name"] = tag,
                ["target_commitish"] = target,
                ["name"] = name,
                ["body"] = body,
                ["draft"] = false,
                ["prerelease"] = false,
            }),
        };
        using var response = client.Send(request);
        return Object(response, $"create release {tag} of {repository}")!;
    }

    /// <summary>The head commit of a branch, or null when the repository has no such branch.</summary>
    public string? BranchHead(string repository, string branch)
    {
        using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{repository}/commits/{Uri.EscapeDataString(branch)}"));
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return null;
        return Object(response, $"read {branch} of {repository}")?["sha"]?.GetValue<string>();
    }

    /// <summary>The release's assets by name, with their id and byte size.</summary>
    public static IReadOnlyDictionary<string, (long Id, long Size)> Assets(JsonObject release)
    {
        var assets = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (release["assets"] is JsonArray listed)
        {
            foreach (var entry in listed.OfType<JsonObject>())
            {
                var name = entry["name"]?.GetValue<string>();
                if (name is null) continue;
                assets[name] = (entry["id"]!.GetValue<long>(), entry["size"]?.GetValue<long>() ?? -1);
            }
        }
        return assets;
    }

    /// <summary>Upload one file as a release asset under the name given.</summary>
    public void UploadAsset(string repository, long releaseId, string assetName, string path, string contentType)
    {
        using var content = new StreamContent(File.OpenRead(path));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Uploads}/repos/{repository}/releases/{releaseId}/assets?name={Uri.EscapeDataString(assetName)}")
        {
            Content = content,
        };
        using var response = client.Send(request);
        Object(response, $"upload {assetName} to release {releaseId} of {repository}");
    }

    /// <summary>Remove one asset, so a replacement can take its name.</summary>
    public void DeleteAsset(string repository, long assetId)
    {
        using var response = client.Send(new HttpRequestMessage(HttpMethod.Delete, $"{Api}/repos/{repository}/releases/assets/{assetId}"));
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
        Object(response, $"delete asset {assetId} of {repository}");
    }

    /// <summary>
    /// Download one asset to the path given. A private asset is fetched
    /// through the API with <c>Accept: application/octet-stream</c>, which
    /// redirects to storage with a short-lived signature; the token never
    /// travels to that redirect, and HttpClient drops the header for us.
    /// </summary>
    public long DownloadAsset(string repository, long assetId, string destination)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{repository}/releases/assets/{assetId}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"download asset {assetId} of {repository}: {Detail(response)}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using (var target = File.Create(destination))
        {
            response.Content.ReadAsStream().CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        return new FileInfo(destination).Length;
    }

    /// <summary>Read one asset's bytes, for the small manifest documents.</summary>
    public string ReadAssetText(string repository, long assetId)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"fern-release-{Guid.NewGuid():N}.json");
        try
        {
            DownloadAsset(repository, assetId, temporary);
            return File.ReadAllText(temporary);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static StringContent Json(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    static JsonObject? Object(HttpResponseMessage response, string what)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{what}: {Detail(response, body)}");
        }
        if (body.Length == 0) return null;
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"{what}: GitHub returned a body that is not an object ({error.Message})");
        }
    }

    static string Detail(HttpResponseMessage response, string? body = null)
    {
        body ??= response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var message = body;
        try
        {
            if (JsonNode.Parse(body) is JsonObject problem && problem["message"] is { } stated)
            {
                message = stated.GetValue<string>();
            }
        }
        catch (JsonException)
        {
            // The body is whatever it is; report it as text.
        }
        if (message.Length > 400) message = message[..400];
        return $"HTTP {(int)response.StatusCode} {response.StatusCode}: {message}";
    }
}
