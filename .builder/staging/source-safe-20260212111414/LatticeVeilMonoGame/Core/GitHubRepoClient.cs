using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Lightweight GitHub REST client for fetching repository metadata and commit info.
/// Used by AssetPackInstaller when pulling assets directly from the repo (not Releases).
/// </summary>
public sealed class GitHubRepoClient
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GitHubRepoClient(HttpClient? client = null)
    {
        _client = client ?? CreateHttpClient();
    }

    public async Task<GitHubRepoMeta?> FetchRepoMetaAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}";
        return await FetchAsync<GitHubRepoMeta>(url, ct);
    }

    public async Task<GitHubCommitResponse?> FetchCommitAsync(string owner, string repo, string @ref, CancellationToken ct)
    {
        var safe = Uri.EscapeDataString(@ref);
        var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{safe}";
        return await FetchAsync<GitHubCommitResponse>(url, ct);
    }

    private async Task<T?> FetchAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, ct);
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

public sealed class GitHubRepoMeta
{
    public string? default_branch { get; set; }
}

public sealed class GitHubCommitResponse
{
    public string? sha { get; set; }
    public GitHubCommitDetails? commit { get; set; }
}

public sealed class GitHubCommitDetails
{
    public GitHubCommitPerson? committer { get; set; }
    public GitHubCommitPerson? author { get; set; }
}

public sealed class GitHubCommitPerson
{
    public DateTimeOffset? date { get; set; }
}
