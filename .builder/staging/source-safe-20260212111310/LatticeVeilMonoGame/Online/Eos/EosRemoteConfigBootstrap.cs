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

    public static bool TryBootstrap(Logger log, OnlineGateClient gate, bool allowRetry = false)
    {
        if (gate == null)
            return false;

        if (EosConfig.HasPublicConfigSource() && EosConfig.HasSecretSource())
            return true;

        if (!ShouldAttempt(allowRetry))
            return false;

        if (!gate.TryGetValidTicketForChildProcess(out var ticket, out _))
        {
            log.Warn("EOS remote config bootstrap skipped: no valid gate ticket.");
            return false;
        }

        var attempts = allowRetry ? RetryFetchAttempts : InitialFetchAttempts;
        if (!TryFetchWithGateTicketWithRetry(log, ticket, attempts, out var payload))
            return false;

        ApplyEnvironment(payload);
        log.Info("EOS config hydrated from remote gate endpoint.");
        return true;
    }

    private static bool TryFetchWithGateTicketWithRetry(
        Logger log,
        string ticket,
        int attempts,
        out EosConfigPayload payload)
    {
        payload = new EosConfigPayload();
        var maxAttempts = Math.Max(1, attempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TryFetchWithGateTicket(log, ticket, out payload))
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

    private static bool TryFetchWithGateTicket(Logger log, string ticket, out EosConfigPayload payload)
    {
        payload = new EosConfigPayload();
        var endpoint = ResolveGateConfigEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            log.Warn("EOS remote config bootstrap skipped: gate endpoint URL is not configured.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ticket);
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
                log.Warn($"EOS remote config request failed: HTTP {(int)response.StatusCode}.");
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

    private static string ResolveGateConfigEndpoint()
    {
        var explicitUrl = (Environment.GetEnvironmentVariable("LV_EOS_CONFIG_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (IsDisabled(explicitUrl))
                return string.Empty;
            return explicitUrl;
        }

        var gateUrl = (Environment.GetEnvironmentVariable("LV_GATE_URL") ?? string.Empty).Trim();
        if (IsDisabled(gateUrl))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(gateUrl))
            gateUrl = (Environment.GetEnvironmentVariable("LV_GATE_DEFAULT_URL") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(gateUrl))
            gateUrl = "https://eos-service.onrender.com";

        return gateUrl.TrimEnd('/') + "/eos/config/gate";
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
        Environment.SetEnvironmentVariable("EOS_CLIENT_SECRET", payload.ClientSecret);
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
        public string ClientSecret { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductVersion { get; set; } = string.Empty;
        public string LoginMode { get; set; } = string.Empty;

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(ProductId)
                && !string.IsNullOrWhiteSpace(SandboxId)
                && !string.IsNullOrWhiteSpace(DeploymentId)
                && !string.IsNullOrWhiteSpace(ClientId)
                && !string.IsNullOrWhiteSpace(ClientSecret);
        }
    }
}
