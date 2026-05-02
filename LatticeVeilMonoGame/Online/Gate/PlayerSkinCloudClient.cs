using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Gate;

internal sealed class PlayerSkinCloudClient
{
    private const string DefaultFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
    private const string DefaultSupabaseAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _functionsBaseUrl;
    private readonly string _anonKey;
    private readonly Logger? _log;

    public PlayerSkinCloudClient(Logger? log = null, string? functionsBaseUrl = null, string? anonKey = null, HttpClient? http = null)
    {
        _log = log;
        _functionsBaseUrl = ResolveFunctionsBaseUrl(functionsBaseUrl);
        _anonKey = ResolveAnonKey(anonKey);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<PlayerSkinCloudResult> UploadActiveSkinAsync(string accessToken, CancellationToken ct = default)
    {
        accessToken = (accessToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            return PlayerSkinCloudResult.Fail("missing_access_token");

        if (!SkinLibrary.TryReadActiveSkinBytes(out var hash, out var pngBytes))
            return await ClearRemoteSkinAsync(accessToken, ct).ConfigureAwait(false);

        var entry = SkinLibrary.ListSkins().FirstOrDefault(item => string.Equals(item.Hash, hash, StringComparison.OrdinalIgnoreCase));
        var payload = new PlayerSkinSetRequest
        {
            Action = "set",
            Hash = hash,
            PngBase64 = Convert.ToBase64String(pngBytes),
            DisplayName = entry?.DisplayName ?? "Account Skin",
            HasLayers = entry?.HasLayers ?? false
        };

        var result = await PostAsync<PlayerSkinSetResponse>("player-skin-set", accessToken, payload, ct).ConfigureAwait(false);
        if (result.Ok)
            _log?.Info($"Cloud skin uploaded: hash={ShortHash(hash)}, bytes={pngBytes.Length}");
        return result;
    }

    public async Task<PlayerSkinCloudResult> ClearRemoteSkinAsync(string accessToken, CancellationToken ct = default)
    {
        accessToken = (accessToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            return PlayerSkinCloudResult.Fail("missing_access_token");

        var result = await PostAsync<PlayerSkinSetResponse>("player-skin-set", accessToken, new PlayerSkinSetRequest { Action = "clear" }, ct).ConfigureAwait(false);
        if (result.Ok)
            _log?.Info("Cloud skin cleared.");
        return result;
    }

    public async Task<PlayerSkinCloudResult> FetchAndApplyAccountSkinAsync(string accessToken, CancellationToken ct = default)
    {
        accessToken = (accessToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            return PlayerSkinCloudResult.Fail("missing_access_token");

        var result = await GetAsync<PlayerSkinGetResponse>("player-skin-get", accessToken, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is not PlayerSkinGetResponse response)
            return result;

        if (!response.HasSkin || response.Skin == null)
            return PlayerSkinCloudResult.Success("no_remote_skin");

        var skin = response.Skin;
        if (!SkinLibrary.IsValidHash(skin.Hash))
            return PlayerSkinCloudResult.Fail("invalid_remote_skin_hash");

        byte[] pngBytes;
        try
        {
            pngBytes = Convert.FromBase64String((skin.PngBase64 ?? string.Empty).Trim());
        }
        catch
        {
            return PlayerSkinCloudResult.Fail("invalid_remote_skin_base64");
        }

        if (!SkinLibrary.EnsureLocalSkinFromCloud(skin.Hash, pngBytes, skin.DisplayName, setActive: true, out _, out var error))
            return PlayerSkinCloudResult.Fail(error ?? "remote_skin_import_failed");

        _log?.Info($"Cloud skin applied locally: hash={ShortHash(skin.Hash)}, bytes={pngBytes.Length}");
        return PlayerSkinCloudResult.Success("ok");
    }

    private static string ShortHash(string? hash)
    {
        var value = (hash ?? string.Empty).Trim();
        if (value.Length <= 16)
            return string.IsNullOrWhiteSpace(value) ? "none" : value;
        return $"{value[..8]}...{value[^8..]}";
    }

    private async Task<PlayerSkinCloudResult> GetAsync<T>(string endpoint, string accessToken, CancellationToken ct) where T : class
    {
        var url = $"{_functionsBaseUrl}/{endpoint}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, accessToken);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return PlayerSkinCloudResult.Fail(ParseErrorKey(body, response.StatusCode));

            var parsed = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return parsed == null
                ? PlayerSkinCloudResult.Fail("invalid_skin_cloud_response")
                : PlayerSkinCloudResult.Success("ok", parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Cloud skin request failed: endpoint={endpoint}, error={ex.Message}");
            return PlayerSkinCloudResult.Fail(ex.Message);
        }
    }

    private async Task<PlayerSkinCloudResult> PostAsync<T>(string endpoint, string accessToken, object payload, CancellationToken ct) where T : class
    {
        var url = $"{_functionsBaseUrl}/{endpoint}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyHeaders(request, accessToken);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return PlayerSkinCloudResult.Fail(ParseErrorKey(body, response.StatusCode));

            var parsed = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return parsed == null
                ? PlayerSkinCloudResult.Fail("invalid_skin_cloud_response")
                : PlayerSkinCloudResult.Success("ok", parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Cloud skin request failed: endpoint={endpoint}, error={ex.Message}");
            return PlayerSkinCloudResult.Fail(ex.Message);
        }
    }

    private void ApplyHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(_anonKey))
            request.Headers.TryAddWithoutValidation("apikey", _anonKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
    }

    private static string ParseErrorKey(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? $"HTTP {(int)status}";
            }
        }
        catch
        {
        }

        return $"HTTP {(int)status}";
    }

    private static string ResolveFunctionsBaseUrl(string? overrideValue)
    {
        var explicitValue = (overrideValue ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitValue))
            return explicitValue.TrimEnd('/');

        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.TrimEnd('/');

        return DefaultFunctionsBaseUrl;
    }

    private static string ResolveAnonKey(string? overrideValue)
    {
        var explicitValue = (overrideValue ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitValue))
            return explicitValue;

        var fromLv = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromLv))
            return fromLv;

        var fromSupabase = (Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromSupabase))
            return fromSupabase;

        return DefaultSupabaseAnonKey;
    }

    private sealed class PlayerSkinSetRequest
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = "set";

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("pngBase64")]
        public string PngBase64 { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "Account Skin";

        [JsonPropertyName("hasLayers")]
        public bool HasLayers { get; set; }
    }

    private sealed class PlayerSkinSetResponse
    {
        public bool Ok { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    private sealed class PlayerSkinGetResponse
    {
        public bool Ok { get; set; }
        public bool HasSkin { get; set; }
        public PlayerSkinDto? Skin { get; set; }
    }

    private sealed class PlayerSkinDto
    {
        public string Hash { get; set; } = string.Empty;
        public string PngBase64 { get; set; } = string.Empty;
        public string DisplayName { get; set; } = "Account Skin";
    }
}

internal readonly struct PlayerSkinCloudResult
{
    public bool Ok { get; init; }
    public string Message { get; init; }
    public object? Response { get; init; }

    public static PlayerSkinCloudResult Success(string message, object? response = null)
    {
        return new PlayerSkinCloudResult { Ok = true, Message = message ?? string.Empty, Response = response };
    }

    public static PlayerSkinCloudResult Fail(string message)
    {
        return new PlayerSkinCloudResult { Ok = false, Message = (message ?? string.Empty).Trim() };
    }
}
