using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Gate;

namespace LatticeVeilMonoGame.Online.Eos;

public static class EosRemoteConfigBootstrap
{
    private static readonly object Sync = new();
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool _attempted;
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(350);
    private const int InitialFetchAttempts = 2;
    private const int RetryFetchAttempts = 4;
    private const string DefaultFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";

    public static bool TryBootstrap(Logger log, OnlineGateClient gate, bool allowRetry = false, string? ticket = null)
    {
        if (gate == null)
        {
            log.Error("EOS remote config bootstrap failed: gate client is null");
            return false;
        }

        var hasPublic = EosConfig.HasPublicConfigSource();
        var hasSecret = EosConfig.HasSecretSource();
        if (hasPublic && hasSecret)
        {
            log.Info("EOS config already hydrated locally (public + secret).");
            return true;
        }

        if (!ShouldAttempt(allowRetry))
        {
            log.Warn("EOS remote config bootstrap skipped: should not attempt");
            return false;
        }

        var authTicket = ticket;
        if (string.IsNullOrEmpty(authTicket))
            gate.TryGetValidTicketForChildProcess(out authTicket, out _);
        authTicket = (authTicket ?? string.Empty).Trim();

        var needsPublic = !hasPublic;
        var needsSecret = !hasSecret;
        var veilnetAccessToken = ResolveVeilnetAccessToken();
        if (needsSecret && string.IsNullOrWhiteSpace(authTicket))
        {
            log.Warn("EOS remote config bootstrap cannot fetch secret: LV_GATE_TICKET missing.");
            return false;
        }
        if (needsSecret && !IsUsableToken(veilnetAccessToken))
        {
            log.Warn("EOS remote config bootstrap cannot fetch secret: LV_VEILNET_ACCESS_TOKEN missing.");
            return false;
        }

        log.Info($"EOS remote config bootstrap attempting (needsPublic={needsPublic}; needsSecret={needsSecret}).");

        var attempts = allowRetry ? RetryFetchAttempts : InitialFetchAttempts;
        var payload = new EosConfigPayload();
        if (needsPublic && !TryFetchPublicWithRetry(log, authTicket, attempts, out payload))
        {
            log.Error("EOS remote config bootstrap failed: unable to fetch public EOS config.");
            return false;
        }
        if (needsPublic)
        {
            ApplyPublicEnvironment(payload);
            log.Info("EOS public config hydrated from remote endpoint.");
        }

        var secret = string.Empty;
        if (needsSecret && !TryFetchSecretWithRetry(log, authTicket, veilnetAccessToken, attempts, out secret))
        {
            log.Error("EOS remote config bootstrap failed: unable to fetch EOS client secret.");
            return false;
        }
        if (needsSecret)
        {
            ApplySecretEnvironment(secret);
            log.Info("EOS secret hydrated from remote eos-secret endpoint.");
        }

        var readyPublic = EosConfig.HasPublicConfigSource();
        var readySecret = EosConfig.HasSecretSource();
        if (readyPublic && readySecret)
            return true;

        log.Error($"EOS remote config bootstrap incomplete: readyPublic={readyPublic}; readySecret={readySecret}");
        return false;
    }

    private static bool TryFetchPublicWithRetry(
        Logger log,
        string? ticket,
        int attempts,
        out EosConfigPayload payload)
    {
        payload = new EosConfigPayload();
        var maxAttempts = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TryFetchPublic(log, ticket, out payload))
                return true;

            if (attempt >= maxAttempts)
                break;

            Thread.Sleep(RetryDelay);
        }

        return false;
    }

    private static bool ShouldAttempt(bool allowRetry)
    {
        lock (Sync)
        {
            if (!_attempted)
            {
                _attempted = true;
                _lastAttemptUtc = DateTime.UtcNow;
                return true;
            }

            if (!allowRetry)
                return false;

            if (DateTime.UtcNow - _lastAttemptUtc < RetryCooldown)
                return false;

            _lastAttemptUtc = DateTime.UtcNow;
            return true;
        }
    }

    private static bool TryFetchPublic(Logger log, string? ticket, out EosConfigPayload payload)
    {
        payload = new EosConfigPayload();
        var endpoint = ResolveEosConfigEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            log.Warn("EOS remote config bootstrap skipped: config endpoint URL is not configured.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(ticket))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ticket);

            var anonKey = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(anonKey))
                request.Headers.TryAddWithoutValidation("apikey", anonKey);

            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            request.Headers.Pragma.ParseAdd("no-cache");

            using var response = Http.Send(request);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"EOS remote config request failed: HTTP {(int)response.StatusCode} - {body}");
                return false;
            }

            var parsed = JsonSerializer.Deserialize<EosConfigPayload>(body, JsonOptions);
            if (parsed == null || !parsed.IsValid())
            {
                log.Warn("EOS remote config request failed: payload missing required fields.");
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS remote config request failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryFetchSecretWithRetry(
        Logger log,
        string gateTicket,
        string accessToken,
        int attempts,
        out string clientSecret)
    {
        clientSecret = string.Empty;
        var maxAttempts = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TryFetchSecret(log, gateTicket, accessToken, out clientSecret))
                return true;

            if (attempt >= maxAttempts)
                break;

            Thread.Sleep(RetryDelay);
        }

        return false;
    }

    private static bool TryFetchSecret(
        Logger log,
        string gateTicket,
        string accessToken,
        out string clientSecret)
    {
        clientSecret = string.Empty;
        var endpoint = ResolveEosSecretEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            log.Warn("EOS remote config bootstrap skipped: secret endpoint URL is not configured.");
            return false;
        }

        if (!IsUsableToken(accessToken))
        {
            log.Warn("EOS secret request skipped: invalid LV_VEILNET_ACCESS_TOKEN.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(gateTicket))
        {
            log.Warn("EOS secret request skipped: LV_GATE_TICKET missing.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("x-gate-ticket", gateTicket);

            var anonKey = ResolveSupabaseAnonKey();
            if (!string.IsNullOrWhiteSpace(anonKey))
                request.Headers.TryAddWithoutValidation("apikey", anonKey);

            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            request.Headers.Pragma.ParseAdd("no-cache");

            using var response = Http.Send(request);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"EOS secret request failed: HTTP {(int)response.StatusCode} - {TrimSnippet(body, 220)}");
                return false;
            }

            var parsed = JsonSerializer.Deserialize<EosSecretPayload>(body, JsonOptions);
            var secret = (parsed?.ClientSecret ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(secret))
            {
                log.Warn("EOS secret request failed: response missing clientSecret.");
                return false;
            }

            clientSecret = secret;
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS secret request failed: {ex.Message}");
            return false;
        }
    }

    private static string ResolveEosConfigEndpoint()
    {
        var explicitUrl = (Environment.GetEnvironmentVariable("LV_EOS_CONFIG_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (IsDisabled(explicitUrl))
                return string.Empty;
            return explicitUrl;
        }

        var functionsBase = ResolveFunctionsBaseUrl();
        if (string.IsNullOrWhiteSpace(functionsBase))
            return string.Empty;

        return functionsBase + "/eos-config";
    }

    private static string ResolveEosSecretEndpoint()
    {
        var explicitUrl = (Environment.GetEnvironmentVariable("LV_EOS_SECRET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (IsDisabled(explicitUrl))
                return string.Empty;
            return explicitUrl;
        }

        var functionsBase = ResolveFunctionsBaseUrl();
        if (string.IsNullOrWhiteSpace(functionsBase))
            return string.Empty;

        return functionsBase + "/eos-secret";
    }

    private static string ResolveFunctionsBaseUrl()
    {
        var functionsBase = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(functionsBase))
            functionsBase = DefaultFunctionsBaseUrl;

        if (IsDisabled(functionsBase))
            return string.Empty;

        return functionsBase.TrimEnd('/');
    }

    private static bool IsDisabled(string value)
    {
        return value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyPublicEnvironment(EosConfigPayload payload)
    {
        Environment.SetEnvironmentVariable("EOS_PRODUCT_ID", payload.ProductId);
        Environment.SetEnvironmentVariable("EOS_SANDBOX_ID", payload.SandboxId);
        Environment.SetEnvironmentVariable("EOS_DEPLOYMENT_ID", payload.DeploymentId);
        Environment.SetEnvironmentVariable("EOS_CLIENT_ID", payload.ClientId);
        Environment.SetEnvironmentVariable("EOS_PRODUCT_NAME", string.IsNullOrWhiteSpace(payload.ProductName) ? "LatticeVeil" : payload.ProductName);
        Environment.SetEnvironmentVariable("EOS_PRODUCT_VERSION", string.IsNullOrWhiteSpace(payload.ProductVersion) ? "1.0.0" : payload.ProductVersion);
        Environment.SetEnvironmentVariable("EOS_LOGIN_MODE", string.IsNullOrWhiteSpace(payload.LoginMode) ? "deviceid" : payload.LoginMode);
    }

    private static void ApplySecretEnvironment(string clientSecret)
    {
        var secret = (clientSecret ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(secret))
            Environment.SetEnvironmentVariable("EOS_CLIENT_SECRET", secret);
    }

    private static string ResolveVeilnetAccessToken()
    {
        return (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
    }

    private static bool IsUsableToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSupabaseAnonKey()
    {
        var fromLv = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromLv))
            return fromLv;

        var fromSupabase = (Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromSupabase))
            return fromSupabase;

        return string.Empty;
    }

    private static string TrimSnippet(string value, int maxLen)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLen)
            return normalized;
        return normalized.Substring(0, maxLen);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        return client;
    }

    private sealed class EosConfigPayload
    {
        public string ProductId { get; set; } = string.Empty;
        public string SandboxId { get; set; } = string.Empty;
        public string DeploymentId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductVersion { get; set; } = string.Empty;
        public string LoginMode { get; set; } = string.Empty;

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(ProductId)
                && !string.IsNullOrWhiteSpace(SandboxId)
                && !string.IsNullOrWhiteSpace(DeploymentId)
                && !string.IsNullOrWhiteSpace(ClientId);
        }
    }

    private sealed class EosSecretPayload
    {
        public bool Ok { get; set; }
        public string? ClientSecret { get; set; }
    }
}
