using System;
using RedactedCraftMonoGame.Core;

namespace RedactedCraftMonoGame.Online.Eos;

public static class EosClientProvider
{
    private static readonly object Sync = new();
    private static EosClient? _client;
    private static bool _attempted;
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(5);

    public static EosClient? GetOrCreate(Logger log, string? loginModeOverride = null, bool allowRetry = false, bool autoLogin = true)
    {
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

    public static EosClient? Current
    {
        get
        {
            lock (Sync)
                return _client;
        }
    }
}
