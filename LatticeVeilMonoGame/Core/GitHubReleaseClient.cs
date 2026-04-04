using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeilMonoGame.Core;

public sealed class GitHubReleaseClient
{
    private const string DefaultOwner = "latticeveil";
    private const string DefaultRepo = "Assets";
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GitHubReleaseClient(HttpClient? client = null)
    {
        _client = client ?? CreateHttpClient();
    }

    public async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        return await FetchLatestReleaseAsync(DefaultOwner, DefaultRepo, ct);
    }

    public async Task<GitHubRelease?> FetchReleaseByTagAsync(string tag, CancellationToken ct)
    {
        return await FetchReleaseByTagAsync(DefaultOwner, DefaultRepo, tag, ct);
    }

    public async Task<GitHubRelease?> FetchLatestReleaseAsync(string owner, string repo, CancellationToken ct)
    {
        var url = BuildLatestReleaseUrl(owner, repo);
        return await FetchReleaseAsync(url, ct);
    }

    public async Task<GitHubRelease?> FetchReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct)
    {
        var safeTag = Uri.EscapeDataString(tag);
        var safeOwner = Uri.EscapeDataString(owner);
        var safeRepo = Uri.EscapeDataString(repo);
        var url = $"https://api.github.com/repos/{safeOwner}/{safeRepo}/releases/tags/{safeTag}";
        return await FetchReleaseAsync(url, ct);
    }

    private static string BuildLatestReleaseUrl(string owner, string repo)
    {
        var safeOwner = Uri.EscapeDataString(owner);
        var safeRepo = Uri.EscapeDataString(repo);
        return $"https://api.github.com/repos/{safeOwner}/{safeRepo}/releases/latest";
    }

    private async Task<GitHubRelease?> FetchReleaseAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, _jsonOptions, ct);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LatticeVeilMonoGame", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

public sealed class GitHubRelease
{
    public string? tag_name { get; set; }
    public string? name { get; set; }
    public DateTimeOffset? published_at { get; set; }
    public GitHubAsset[]? assets { get; set; }
}

public sealed class GitHubAsset
{
    public string? name { get; set; }
    public string? browser_download_url { get; set; }
    public long size { get; set; }
}
