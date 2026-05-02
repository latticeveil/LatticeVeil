using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatticeVeilMonoGame.Launcher;

internal sealed class VeilnetClient
{
    private readonly HttpClient _http;
    private readonly string _functionsBaseUrl;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal sealed class ExchangeResponse
    {
        public string Token { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("user_id")]
        public string UserIdSnakeCase { get; set; } = string.Empty;

        public void NormalizeUserId()
        {
            if (string.IsNullOrWhiteSpace(UserId))
                UserId = (UserIdSnakeCase ?? string.Empty).Trim();
            else
                UserId = UserId.Trim();
        }
    }

    internal sealed class MeResponse
    {
        public string Username { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("user_id")]
        public string UserIdSnakeCase { get; set; } = string.Empty;

        public void NormalizeUserId()
        {
            if (string.IsNullOrWhiteSpace(UserId))
                UserId = (UserIdSnakeCase ?? string.Empty).Trim();
            else
                UserId = UserId.Trim();
        }
    }

    private sealed class ErrorResponse
    {
        public string Error { get; set; } = string.Empty;
    }

    public VeilnetClient(string functionsBaseUrl, HttpClient? http = null)
    {
        _functionsBaseUrl = (functionsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(_functionsBaseUrl))
            throw new ArgumentException("functionsBaseUrl is required", nameof(functionsBaseUrl));

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ExchangeResponse> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
            throw new Exception("Please paste a valid Veilnet link code.");

        var url = $"{_functionsBaseUrl}/launcher-exchange";
        var payload = JsonSerializer.Serialize(new { code = normalizedCode });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new Exception(MapExchangeError(response.StatusCode, ParseErrorKey(body)));

        var parsed = JsonSerializer.Deserialize<ExchangeResponse>(body, JsonOptions);
        parsed?.NormalizeUserId();

        if (parsed == null
            || string.IsNullOrWhiteSpace(parsed.Token)
            || string.IsNullOrWhiteSpace(parsed.Username)
            || string.IsNullOrWhiteSpace(parsed.UserId))
        {
            throw new Exception("Veilnet returned an invalid exchange response.");
        }

        return parsed;
    }

    public async Task<MeResponse> GetMeAsync(string token, CancellationToken ct = default)
    {
        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Missing launcher token.");

        var url = $"{_functionsBaseUrl}/launcher-me";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new Exception(MapMeError(response.StatusCode, ParseErrorKey(body)));

        var parsed = JsonSerializer.Deserialize<MeResponse>(body, JsonOptions);
        parsed?.NormalizeUserId();

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Username) || string.IsNullOrWhiteSpace(parsed.UserId))
            throw new Exception("Veilnet returned an invalid profile response.");

        return parsed;
    }

    private static string NormalizeCode(string code)
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 0) return string.Empty;
        return string.Concat(value.Where(ch => !char.IsWhiteSpace(ch)));
    }

    private static string ParseErrorKey(string body)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
            return (parsed?.Error ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string MapExchangeError(HttpStatusCode status, string errorKey)
    {
        if (status == HttpStatusCode.BadRequest && errorKey == "invalid_or_expired")
            return "Code is invalid or expired. Generate a new code on Veilnet.";

        if (status == HttpStatusCode.Conflict && errorKey == "username_required")
            return "Set your Veilnet username on the website first, then try again.";

        if (status == HttpStatusCode.NotFound)
            return "Launcher exchange endpoint is not available yet.";

        if ((int)status >= 500)
            return "Veilnet service error. Please try again.";

        if (!string.IsNullOrWhiteSpace(errorKey))
            return errorKey;

        return $"Launcher exchange failed (HTTP {(int)status}).";
    }

    private static string MapMeError(HttpStatusCode status, string errorKey)
    {
        if (status == HttpStatusCode.Unauthorized && (errorKey == "invalid_launcher_token" || errorKey == "missing_launcher_token"))
            return "Saved Veilnet login has expired.";

        if (status == HttpStatusCode.Conflict && errorKey == "username_required")
            return "Your Veilnet account needs a username before linking.";

        if (!string.IsNullOrWhiteSpace(errorKey))
            return errorKey;

        return $"Launcher profile lookup failed (HTTP {(int)status}).";
    }
}
