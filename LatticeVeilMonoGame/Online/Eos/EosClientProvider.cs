using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Gate;

namespace LatticeVeilMonoGame.Online.Eos;

public static class EosClientProvider
{
    private static readonly object Sync = new();
    private static EosClient? _client;

    // Config bootstrap is potentially network-bound. Keep it off the game loop thread.
    private static bool _attemptedBootstrap;
    private static DateTime _lastBootstrapAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(1);
    private static Task<bool>? _bootstrapTask;
    private static readonly TimeSpan DiagnosticLogCooldown = TimeSpan.FromSeconds(4);
    private static DateTime _lastInitContextLogUtc = DateTime.MinValue;
    private static string _lastInitContext = string.Empty;
    private static DateTime _lastBlockedLogUtc = DateTime.MinValue;
    private static string _lastBlockedSignature = string.Empty;

    /// <summary>
    /// Non-blocking EOS client accessor.
    /// 
    /// This is called from update loops and screen constructors; it must not perform network I/O.
    /// If EOS config must be fetched from the gate service, a background bootstrap task is started
    /// and this method returns null until the config is available.
    /// </summary>
    public static EosClient? GetOrCreate(Logger log, string? loginModeOverride = null, bool allowRetry = false, bool autoLogin = true)
    {
        LogInitAttempt(log);

        if (IsGameProcess() && !IsLauncherOnlineAuthorized(out var missingOnlineContext))
        {
            // Do not hard-block client creation here; launcher already enforces Online readiness.
            // Keep discovery/login resilient on machines with stale launcher env propagation.
            LogClientCreationBlocked(log, "ClientContextIncomplete", missingOnlineContext);
        }

        if (IsGameProcess() && !HasVeilnetLogin(out var missingLogin))
        {
            // Same as above: warn but allow EOS bootstrap/client creation.
            LogClientCreationBlocked(log, "ClientContextIncomplete", missingLogin);
        }

        // Fast path.
        lock (Sync)
        {
            if (_client != null)
                return _client;
        }

        var hasPublicConfig = HasBootstrapEnvironment() || EosConfig.HasPublicConfigSource();
        var hasSecret = EosConfig.HasSecretSource();

        // Bootstrap when either public config or client secret is missing.
        if (!hasPublicConfig || !hasSecret)
        {
            var missing = new List<string>();
            if (!hasPublicConfig)
            {
                missing.Add("HardcodedDefaults/EOS_PRODUCT_ID/EOS_SANDBOX_ID/EOS_DEPLOYMENT_ID/EOS_CLIENT_ID or eos.public.json");
            }
            if (!hasSecret)
            {
                missing.Add("EOS_CLIENT_SECRET (remote eos-secret bootstrap required)");
            }
            LogClientCreationBlocked(log, "ConfigMissing", missing);
            EnsureBootstrapTask(log, allowRetry);
            return null;
        }

        // Config is present; client creation is local and can run on the calling thread.
        var created = EosClient.TryCreate(log, loginModeOverride, autoLogin);
        if (created == null)
        {
            var missing = new List<string>();
            if (!EosConfig.HasSecretSource())
                missing.Add("EOS_CLIENT_SECRET (remote eos-secret bootstrap required)");
            LogClientCreationBlocked(log, "ClientCreateFailed", missing);
            return null;
        }

        lock (Sync)
        {
            _client ??= created;
            return _client;
        }
    }

    /// <summary>
    /// Awaitable version for call sites that want to wait for bootstrap completion (e.g., launcher flows).
    /// </summary>
    public static async Task<EosClient?> GetOrCreateAsync(Logger log, string? loginModeOverride = null, bool allowRetry = false, bool autoLogin = true)
    {
        var immediate = GetOrCreate(log, loginModeOverride, allowRetry, autoLogin);
        if (immediate != null)
            return immediate;

        Task<bool>? bootstrap;
        lock (Sync)
            bootstrap = _bootstrapTask;

        if (bootstrap != null && !bootstrap.IsCompleted)
            await bootstrap.ConfigureAwait(false);

        return GetOrCreate(log, loginModeOverride, allowRetry, autoLogin);
    }

    private static void EnsureBootstrapTask(Logger log, bool allowRetry)
    {
        lock (Sync)
        {
            if (_bootstrapTask != null && !_bootstrapTask.IsCompleted)
                return;

            // If config became available since the last check, nothing to do.
            if (HasBootstrapEnvironment())
                return;

            if (_attemptedBootstrap && !allowRetry)
                return;

            if (allowRetry && _lastBootstrapAttemptUtc != DateTime.MinValue &&
                DateTime.UtcNow - _lastBootstrapAttemptUtc < RetryCooldown)
                return;

            _attemptedBootstrap = true;
            _lastBootstrapAttemptUtc = DateTime.UtcNow;

            _bootstrapTask = Task.Run(() => BootstrapRemoteConfig(log, allowRetry));
        }
    }

    private static bool BootstrapRemoteConfig(Logger log, bool allowRetry)
    {
        try
        {
            var gate = OnlineGateClient.GetOrCreate();
            string? ticket = null;
            if (!gate.TryGetValidTicketForChildProcess(out ticket, out _))
                log.Warn("No ticket available for EOS config bootstrap");

            return EosRemoteConfigBootstrap.TryBootstrap(log, gate, allowRetry, ticket);
        }
        catch (Exception ex)
        {
            log.Warn($"EOS remote config bootstrap failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasBootstrapEnvironment()
    {
        static string Get(string key) => (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();

        return !string.IsNullOrWhiteSpace(Get("EOS_PRODUCT_ID"))
            && !string.IsNullOrWhiteSpace(Get("EOS_SANDBOX_ID"))
            && !string.IsNullOrWhiteSpace(Get("EOS_DEPLOYMENT_ID"))
            && !string.IsNullOrWhiteSpace(Get("EOS_CLIENT_ID"));
    }

    private static bool IsGameProcess()
    {
        var processKind = Environment.GetEnvironmentVariable("LV_PROCESS_KIND");
        return string.Equals(processKind, "game", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLauncherOnlineAuthorized(out List<string> missing)
    {
        missing = new List<string>();

        var launchMode = GetEnv("LV_LAUNCH_MODE");
        var authorized = GetEnv("LV_LAUNCHER_ONLINE_AUTH");
        var official = GetEnv("LV_OFFICIAL_BUILD_VERIFIED");
        var servicesOk = GetEnv("LV_ONLINE_SERVICES_OK");
        var ticket = GetEnv("LV_GATE_TICKET");

        if (!IsTrue(authorized))
            missing.Add("LV_LAUNCHER_ONLINE_AUTH");

        var requireDetailedFlags = string.Equals(launchMode, "online", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(official)
            || !string.IsNullOrWhiteSpace(servicesOk);

        if (requireDetailedFlags)
        {
            if (!IsTrue(official))
                missing.Add("LV_OFFICIAL_BUILD_VERIFIED");
            if (!IsTrue(servicesOk))
                missing.Add("LV_ONLINE_SERVICES_OK");
        }

        if (string.IsNullOrWhiteSpace(ticket))
            missing.Add("LV_GATE_TICKET");

        return missing.Count == 0;
    }

    private static bool HasVeilnetLogin(out List<string> missing)
    {
        missing = new List<string>();
        var token = GetEnv("LV_VEILNET_ACCESS_TOKEN");
        if (!IsUsableToken(token))
            missing.Add("LV_VEILNET_ACCESS_TOKEN");

        return missing.Count == 0;
    }

    private static void LogInitAttempt(Logger log)
    {
        var launchMode = GetEnv("LV_LAUNCH_MODE");
        if (string.IsNullOrWhiteSpace(launchMode))
            launchMode = "unknown";

        var official = IsTrue(GetEnv("LV_OFFICIAL_BUILD_VERIFIED"));
        var servicesOk = IsTrue(GetEnv("LV_ONLINE_SERVICES_OK"));
        var hasTicket = !string.IsNullOrWhiteSpace(GetEnv("LV_GATE_TICKET"));
        var hasVeilnetToken = IsUsableToken(GetEnv("LV_VEILNET_ACCESS_TOKEN"));
        var configSource = EosConfig.DescribePublicConfigSource();

        var summary =
            $"LaunchMode={launchMode}; OfficialVerified={official}; OnlineServicesOK={servicesOk}; " +
            $"HasTicket={hasTicket}; HasVeilnetToken={hasVeilnetToken}; ConfigSource={configSource}";

        var now = DateTime.UtcNow;
        if (string.Equals(summary, _lastInitContext, StringComparison.Ordinal)
            && now - _lastInitContextLogUtc < DiagnosticLogCooldown)
        {
            return;
        }

        _lastInitContext = summary;
        _lastInitContextLogUtc = now;
        log.Info($"EOS init attempt: {summary}");
    }

    private static void LogClientCreationBlocked(Logger log, string reason, IReadOnlyCollection<string> missing)
    {
        var missingList = missing.Count > 0 ? string.Join(", ", missing) : "none";
        var signature = $"{reason}|{missingList}";
        var now = DateTime.UtcNow;
        if (string.Equals(signature, _lastBlockedSignature, StringComparison.Ordinal)
            && now - _lastBlockedLogUtc < DiagnosticLogCooldown)
        {
            return;
        }

        _lastBlockedSignature = signature;
        _lastBlockedLogUtc = now;
        log.Warn($"EOS client creation failed: reason={reason}; missing={missingList}");
    }

    private static string GetEnv(string key)
    {
        return (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
    }

    private static bool IsTrue(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    public static EosClient? Current
    {
        get
        {
            lock (Sync)
                return _client;
        }
    }
}
