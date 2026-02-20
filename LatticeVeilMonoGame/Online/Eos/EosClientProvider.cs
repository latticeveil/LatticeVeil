using System;
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

    private static bool _launcherGateWarned;

    /// <summary>
    /// Non-blocking EOS client accessor.
    /// 
    /// This is called from update loops and screen constructors; it must not perform network I/O.
    /// If EOS config must be fetched from the gate service, a background bootstrap task is started
    /// and this method returns null until the config is available.
    /// </summary>
    public static EosClient? GetOrCreate(Logger log, string? loginModeOverride = null, bool allowRetry = false, bool autoLogin = true)
    {
        if (IsGameProcess() && !IsLauncherOnlineAuthorized())
        {
            if (!_launcherGateWarned)
            {
                _launcherGateWarned = true;
                log.Warn("Online features require launching from Lattice Launcher.");
            }

            return null;
        }

        if (IsGameProcess() && IsLauncherOnlineAuthorized() && !HasVeilnetLogin())
        {
            if (!_launcherGateWarned)
            {
                _launcherGateWarned = true;
                log.Warn("Online features require Veilnet login.");
            }

            return null;
        }

        // Fast path.
        lock (Sync)
        {
            if (_client != null)
                return _client;
        }

        // If no environment config and no local public config is available, bootstrap asynchronously.
        if (!HasBootstrapEnvironment() && !EosConfig.HasPublicConfigSource())
        {
            EnsureBootstrapTask(log, allowRetry);
            return null;
        }

        // Config is present; client creation is local and can run on the calling thread.
        var created = EosClient.TryCreate(log, loginModeOverride, autoLogin);
        if (created == null)
            return null;

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

    private static bool IsLauncherOnlineAuthorized()
    {
        static bool IsTrue(string? value)
        {
            return string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        static bool HasGateTicket()
        {
            var ticket = (Environment.GetEnvironmentVariable("LV_GATE_TICKET") ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(ticket);
        }

        var authorized = Environment.GetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH");
        var official = Environment.GetEnvironmentVariable("LV_OFFICIAL_BUILD_VERIFIED");
        var servicesOk = Environment.GetEnvironmentVariable("LV_ONLINE_SERVICES_OK");

        var hasDetailedFlags = !string.IsNullOrWhiteSpace(official) || !string.IsNullOrWhiteSpace(servicesOk);
        if (hasDetailedFlags)
            return IsTrue(authorized) && IsTrue(official) && IsTrue(servicesOk) && HasGateTicket();

        return IsTrue(authorized) && HasGateTicket();
    }

    private static bool HasVeilnetLogin()
    {
        var token = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(token);
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
