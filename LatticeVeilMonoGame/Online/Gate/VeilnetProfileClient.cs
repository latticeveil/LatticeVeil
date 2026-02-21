using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Gate;

internal sealed class VeilnetProfileClient
{
    private const string DefaultFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
    private const string DefaultSupabaseAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";
    private const string DefaultProfileEndpoint = "launcher-me";
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900)
    };

    private readonly HttpClient _http;
    private readonly string _functionsBaseUrl;
    private readonly string _anonKey;
    private readonly string _profileEndpoint;
    private readonly Logger? _log;

    public VeilnetProfileClient(
        Logger? log = null,
        string? functionsBaseUrl = null,
        string? anonKey = null,
        string? profileEndpoint = null,
        HttpClient? http = null)
    {
        _log = log;
        _functionsBaseUrl = ResolveFunctionsBaseUrl(functionsBaseUrl);
        _anonKey = ResolveAnonKey(anonKey);
        _profileEndpoint = ResolveProfileEndpoint(profileEndpoint);
        _http = http ?? CreateHttpClient();
    }

    public async Task<VeilnetProfileFetchResult> GetProfileAsync(string accessToken, CancellationToken ct = default)
    {
        var token = (accessToken ?? string.Empty).Trim();
        if (!IsUsableToken(token))
            return VeilnetProfileFetchResult.Fail("missing_access_token");

        var result = await GetFromEndpointWithRetryAsync(_profileEndpoint, token, ct).ConfigureAwait(false);
        if (!result.Ok || result.Profile == null)
            return result;

        if (string.IsNullOrWhiteSpace(result.Profile.Username))
            return VeilnetProfileFetchResult.Fail("profile_lookup_failed");

        // Treat username-only payloads as incomplete so UI does not claim a successful profile sync.
        if (!result.Profile.PayloadIncludesProfileFields)
            return VeilnetProfileFetchResult.Fail("profile_payload_incomplete");

        return result;
    }

    private async Task<VeilnetProfileFetchResult> GetFromEndpointWithRetryAsync(string endpoint, string accessToken, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            var result = await GetFromEndpointAsync(endpoint, accessToken, ct).ConfigureAwait(false);
            if (result.Ok)
                return result;

            if (!result.IsTransient || attempt >= RetryDelays.Length)
                return result;

            await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }

        return VeilnetProfileFetchResult.Fail("profile_lookup_failed");
    }

    private async Task<VeilnetProfileFetchResult> GetFromEndpointAsync(string endpoint, string accessToken, CancellationToken ct)
    {
        var endpointPath = NormalizeEndpointPath(endpoint);
        var url = $"{_functionsBaseUrl}/{endpointPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(_anonKey))
            request.Headers.TryAddWithoutValidation("apikey", _anonKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        var hasXClientInfo = request.Headers.Contains("x-client-info");

        _log?.Info(
            $"[VeilnetProfileClient] request: method=GET url={url} " +
            $"hasAuthorization={!string.IsNullOrWhiteSpace(accessToken)} " +
            $"hasApikey={!string.IsNullOrWhiteSpace(_anonKey)} " +
            $"hasXClientInfo={hasXClientInfo}");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log?.Info($"[VeilnetProfileClient] response: status={(int)response.StatusCode} body={ClipForLog(body, 200)}");

            if (!response.IsSuccessStatusCode)
            {
                var isTransient = IsTransientStatus(response.StatusCode);
                var key = ParseErrorKey(body);
                var msg = string.IsNullOrWhiteSpace(key) ? $"HTTP {(int)response.StatusCode}" : key;
                return VeilnetProfileFetchResult.Fail(msg, isTransient);
            }

            if (!TryParseProfile(body, out var profile))
            {
                _log?.Warn($"[VeilnetProfileClient] parse failed: endpoint={endpointPath}");
                return VeilnetProfileFetchResult.Fail("invalid_profile_response");
            }

            return VeilnetProfileFetchResult.Success(profile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log?.Warn($"[VeilnetProfileClient] request error: endpoint={endpointPath} type={ex.GetType().Name} message={ex.Message} stack={ex.StackTrace}");
            return VeilnetProfileFetchResult.Fail(ex.Message, isTransient: true);
        }
        catch (Exception ex)
        {
            _log?.Warn($"[VeilnetProfileClient] request error: endpoint={endpointPath} type={ex.GetType().Name} message={ex.Message} stack={ex.StackTrace}");
            return VeilnetProfileFetchResult.Fail(ex.Message);
        }
    }

    private static bool TryParseProfile(string body, out VeilnetProfileDto profile)
    {
        profile = new VeilnetProfileDto();
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var profileObj = root;
            if (TryGetObject(root, "profile", out var nestedProfile))
                profileObj = nestedProfile;

            profile.Username = FirstString(root, profileObj, "username", "displayName", "display_name");
            if (string.IsNullOrWhiteSpace(profile.Username) && TryGetObject(root, "user", out var userObj))
                profile.Username = FirstString(userObj, "username", "displayName", "display_name");

            profile.PictureUrl = FirstString(root, profileObj, "picture", "avatar", "avatar_url", "picture_url");
            profile.BannerUrl = FirstString(root, profileObj, "banner", "banner_url");
            profile.AboutMe = FirstString(root, profileObj, "aboutme", "aboutMe", "about");
            profile.ThemeColor = FirstString(root, profileObj, "themecolor", "themeColor", "theme_color");
            profile.Theme = FirstString(root, profileObj, "theme");
            profile.UpdatedAtRaw = FirstString(root, profileObj, "updatedat", "updatedAt", "updated_at");

            profile.PayloadIncludesProfileFields =
                ContainsAnyProperty(root, "picture", "banner", "aboutme", "aboutMe", "themecolor", "themeColor", "theme", "updatedat", "updatedAt", "updated_at")
                || ContainsAnyProperty(profileObj, "picture", "banner", "aboutme", "aboutMe", "themecolor", "themeColor", "theme", "updatedat", "updatedAt", "updated_at");

            return !string.IsNullOrWhiteSpace(profile.Username);
        }
        catch
        {
            return false;
        }
    }

    private static string ParseErrorKey(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return string.Empty;
            return FirstString(doc.RootElement, "error", "reason", "message");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetObject(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object)
            return true;

        value = default;
        return false;
    }

    private static bool ContainsAnyProperty(JsonElement source, params string[] keys)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return false;

        for (var i = 0; i < keys.Length; i++)
        {
            if (source.TryGetProperty(keys[i], out _))
                return true;
        }

        return false;
    }

    private static string FirstString(JsonElement source, params string[] keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (!source.TryGetProperty(keys[i], out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return (value.GetString() ?? string.Empty).Trim();

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                return value.ToString().Trim();
        }

        return string.Empty;
    }

    private static string FirstString(JsonElement primary, JsonElement secondary, params string[] keys)
    {
        var fromSecondary = FirstString(secondary, keys);
        if (!string.IsNullOrWhiteSpace(fromSecondary))
            return fromSecondary;
        return FirstString(primary, keys);
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

        var envKeys = new[]
        {
            "LV_SUPABASE_ANON_KEY",
            "SUPABASE_ANON_KEY",
            "VEILNET_SUPABASE_ANON_KEY"
        };

        for (var i = 0; i < envKeys.Length; i++)
        {
            var value = (Environment.GetEnvironmentVariable(envKeys[i]) ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return DefaultSupabaseAnonKey;
    }

    private static string ResolveProfileEndpoint(string? overrideValue)
    {
        var explicitValue = (overrideValue ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitValue))
            return NormalizeEndpointPath(explicitValue);

        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_PROFILE_ENDPOINT") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return NormalizeEndpointPath(fromEnv);

        return DefaultProfileEndpoint;
    }

    private static string NormalizeEndpointPath(string endpoint)
    {
        var value = (endpoint ?? string.Empty).Trim();
        if (value.StartsWith("/", StringComparison.Ordinal))
            value = value.Substring(1);
        return string.IsNullOrWhiteSpace(value) ? DefaultProfileEndpoint : value;
    }

    private static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code == 408 || code == 425 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
    }

    private static bool IsUsableToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClipForLog(string input, int maxChars)
    {
        var value = (input ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= maxChars)
            return value;
        return value.Substring(0, Math.Max(0, maxChars));
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (handler.SupportsAutomaticDecompression)
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}

internal sealed class VeilnetProfileDto
{
    public string Username { get; set; } = string.Empty;
    public string PictureUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public string AboutMe { get; set; } = string.Empty;
    public string ThemeColor { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string UpdatedAtRaw { get; set; } = string.Empty;
    public bool PayloadIncludesProfileFields { get; set; }
}

internal readonly struct VeilnetProfileFetchResult
{
    public bool Ok { get; init; }
    public VeilnetProfileDto? Profile { get; init; }
    public string Message { get; init; }
    public bool IsTransient { get; init; }

    public static VeilnetProfileFetchResult Success(VeilnetProfileDto profile)
    {
        return new VeilnetProfileFetchResult
        {
            Ok = true,
            Profile = profile,
            Message = string.Empty,
            IsTransient = false
        };
    }

    public static VeilnetProfileFetchResult Fail(string message, bool isTransient = false)
    {
        return new VeilnetProfileFetchResult
        {
            Ok = false,
            Profile = null,
            Message = (message ?? string.Empty).Trim(),
            IsTransient = isTransient
        };
    }
}
