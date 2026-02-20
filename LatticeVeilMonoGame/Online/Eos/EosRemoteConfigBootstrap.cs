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

        // EOS secret is intentionally not required on client.
        if (EosConfig.HasPublicConfigSource())
        {
            log.Info("EOS public config already available locally.");
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

        log.Info("EOS remote config bootstrap attempting.");

        var attempts = allowRetry ? RetryFetchAttempts : InitialFetchAttempts;
        if (!TryFetchWithRetry(log, authTicket, attempts, out var payload))
        {
            log.Error("EOS remote config bootstrap failed: unable to fetch config");
            return false;
        }

        ApplyEnvironment(payload);
        log.Info("EOS config hydrated from remote gate endpoint successfully");
        return true;
    }

    private static bool TryFetchWithRetry(
        Logger log,
        string? ticket,
        int attempts,
        out EosConfigPayload payload)
    {
        payload = new EosConfigPayload();
        var maxAttempts = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TryFetch(log, ticket, out payload))
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

    private static bool TryFetch(Logger log, string? ticket, out EosConfigPayload payload)
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

    private static string ResolveEosConfigEndpoint()
    {
        var explicitUrl = (Environment.GetEnvironmentVariable("LV_EOS_CONFIG_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (IsDisabled(explicitUrl))
                return string.Empty;
            return explicitUrl;
        }

        var functionsBase = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(functionsBase))
            functionsBase = DefaultFunctionsBaseUrl;

        if (IsDisabled(functionsBase))
            return string.Empty;

        return functionsBase.TrimEnd('/') + "/eos-config";
    }

    private static bool IsDisabled(string value)
    {
        return value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyEnvironment(EosConfigPayload payload)
    {
        Environment.SetEnvironmentVariable("EOS_PRODUCT_ID", payload.ProductId);
        Environment.SetEnvironmentVariable("EOS_SANDBOX_ID", payload.SandboxId);
        Environment.SetEnvironmentVariable("EOS_DEPLOYMENT_ID", payload.DeploymentId);
        Environment.SetEnvironmentVariable("EOS_CLIENT_ID", payload.ClientId);
        Environment.SetEnvironmentVariable("EOS_PRODUCT_NAME", string.IsNullOrWhiteSpace(payload.ProductName) ? "LatticeVeil" : payload.ProductName);
        Environment.SetEnvironmentVariable("EOS_PRODUCT_VERSION", string.IsNullOrWhiteSpace(payload.ProductVersion) ? "1.0.0" : payload.ProductVersion);
        Environment.SetEnvironmentVariable("EOS_LOGIN_MODE", string.IsNullOrWhiteSpace(payload.LoginMode) ? "deviceid" : payload.LoginMode);
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
}
