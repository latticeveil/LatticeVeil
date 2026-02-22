using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Eos;

public static class EosClientProvider
{
    private static readonly object Sync = new();
    private static EosClient? _client;
    private static bool _attempted;
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(1);
    private static bool _launcherGateWarned;

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

        lock (Sync)
        {
            if (_client != null)
                return _client;

            if (_attempted && !allowRetry)
                return null;

            if (allowRetry && _lastAttemptUtc != DateTime.MinValue &&
                DateTime.UtcNow - _lastAttemptUtc < RetryCooldown)
                return null;

            _attempted = true;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        var created = EosClient.TryCreate(log, loginModeOverride, autoLogin);
        if (created == null)
            return null;

        lock (Sync)
        {
            _client ??= created;
            return _client;
        }
    }

    private static bool IsGameProcess()
    {
        var processKind = Environment.GetEnvironmentVariable("LV_PROCESS_KIND");
        return string.Equals(processKind, "game", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLauncherOnlineAuthorized()
    {
        var authorized = Environment.GetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH");
        return string.Equals(authorized, "1", StringComparison.Ordinal)
            || string.Equals(authorized, "true", StringComparison.OrdinalIgnoreCase);
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
