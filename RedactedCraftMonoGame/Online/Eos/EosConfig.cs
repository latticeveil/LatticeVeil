using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RedactedCraftMonoGame.Core;

namespace RedactedCraftMonoGame.Online.Eos;

public sealed class EosConfig
{
    private const string DefaultRemoteConfigUrl = "https://eos-config-service.onrender.com/eos/config";
    private static readonly object RemoteSync = new();
    private static Task? _remoteFetchTask;
    private static EosConfig? _remoteCached;
    private static bool _remoteFetchLogged;
    private static bool _missingLogged;
    private const int RemoteFetchTimeoutSeconds = 6;

    public string ProductId { get; set; } = "";
    public string SandboxId { get; set; } = "";
    public string DeploymentId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ProductName { get; set; } = "RedactedCraft";
    public string ProductVersion { get; set; } = "1.0";
    public string LoginMode { get; set; } = "device";

    public static string ConfigPath =>
        Path.Combine(Paths.ConfigDir, "eos.local.json");

    public static EosConfig? Load(Logger log)
    {
        EosConfig? envConfig = null;
        if (TryLoadFromEnvironment(out var tempEnv))
        {
            envConfig = tempEnv;
            if (envConfig.IsValid(out var envError))
                return envConfig;

            log.Warn($"EOS env config invalid: {envError}");
        }

        var path = ResolveConfigPath(log);
        if (path == null)
        {
            var cached = TryGetRemoteCached();
            if (cached != null)
                return cached;

            EnsureRemoteFetch(log);
            if (!_missingLogged)
            {
                log.Info("EOS config not found; skipping EOS login.");
                _missingLogged = true;
            }
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<EosConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (cfg == null)
            {
                log.Warn("EOS config parse failed.");
                return null;
            }

            if (!cfg.IsValid(out var error))
            {
                log.Warn($"EOS config invalid: {error}");
                return null;
            }

            return cfg;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS config load failed: {ex.Message}");
            return null;
        }
    }

    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(ProductId))
        {
            error = "ProductId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(SandboxId))
        {
            error = "SandboxId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(DeploymentId))
        {
            error = "DeploymentId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            error = "ClientId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            error = "ClientSecret missing.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryLoadFromEnvironment(out EosConfig config)
    {
        config = new EosConfig
        {
            ProductId = GetEnv("EOS_PRODUCT_ID") ?? "",
            SandboxId = GetEnv("EOS_SANDBOX_ID") ?? "",
            DeploymentId = GetEnv("EOS_DEPLOYMENT_ID") ?? "",
            ClientId = GetEnv("EOS_CLIENT_ID") ?? "",
            ClientSecret = GetEnv("EOS_CLIENT_SECRET") ?? "",
            ProductName = GetEnv("EOS_PRODUCT_NAME") ?? "RedactedCraft",
            ProductVersion = GetEnv("EOS_PRODUCT_VERSION") ?? "1.0",
            LoginMode = GetEnv("EOS_LOGIN_MODE") ?? "device"
        };

        return !string.IsNullOrWhiteSpace(config.ProductId)
            || !string.IsNullOrWhiteSpace(config.SandboxId)
            || !string.IsNullOrWhiteSpace(config.DeploymentId)
            || !string.IsNullOrWhiteSpace(config.ClientId)
            || !string.IsNullOrWhiteSpace(config.ClientSecret);
    }

    private static string? ResolveConfigPath(Logger log)
    {
        var primary = ConfigPath;
        if (File.Exists(primary))
            return primary;

        var envPath = GetEnv("EOS_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var local = Path.Combine(AppContext.BaseDirectory, "eos.local.json");
        if (File.Exists(local))
        {
            return TryCopyConfigToPrimary(local, primary, log) ?? local;
        }

        var devConfig = FindConfigUpwards(AppContext.BaseDirectory, Path.Combine(".config", "eos.local.json"));
        if (!string.IsNullOrWhiteSpace(devConfig) && File.Exists(devConfig))
            return TryCopyConfigToPrimary(devConfig, primary, log) ?? devConfig;

        return null;
    }

    private static EosConfig? TryLoadRemoteConfig(Logger log)
    {
        // Deprecated synchronous path - keep for compatibility but avoid direct use.
        var remote = LoadRemoteConfigSource(log);
        if (remote == null)
            return null;
        return TryLoadRemoteConfigAsync(log, remote).GetAwaiter().GetResult();
    }

    private static RemoteConfigSource? LoadRemoteConfigSource(Logger log)
    {
        var envUrl = GetEnv("EOS_CONFIG_URL");
        var envKey = GetEnv("EOS_CONFIG_API_KEY");
        if (!string.IsNullOrWhiteSpace(envUrl))
            return new RemoteConfigSource(envUrl, envKey);

        var remotePath = Path.Combine(Paths.ConfigDir, "eos.remote.json");
        if (!File.Exists(remotePath))
            return new RemoteConfigSource(DefaultRemoteConfigUrl, null);

        try
        {
            var json = File.ReadAllText(remotePath);
            var remote = JsonSerializer.Deserialize<RemoteConfigSource>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (remote == null || string.IsNullOrWhiteSpace(remote.Url))
                return null;
            return remote;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS remote config parse failed: {ex.Message}");
            return null;
        }
    }

    private sealed class RemoteConfigSource
    {
        public string Url { get; }
        public string? ApiKey { get; }

        public RemoteConfigSource(string url, string? apiKey)
        {
            Url = url;
            ApiKey = apiKey;
        }
    }

    public static bool IsRemoteFetchPending
    {
        get
        {
            lock (RemoteSync)
                return _remoteFetchTask != null && !_remoteFetchTask.IsCompleted;
        }
    }

    private static void EnsureRemoteFetch(Logger log)
    {
        lock (RemoteSync)
        {
            if (_remoteCached != null)
                return;
            if (_remoteFetchTask != null && !_remoteFetchTask.IsCompleted)
                return;

            var remote = LoadRemoteConfigSource(log);
            if (remote == null)
                return;

            _remoteFetchTask = Task.Run(async () =>
            {
                var cfg = await TryLoadRemoteConfigAsync(log, remote).ConfigureAwait(false);
                if (cfg == null)
                    return;
                lock (RemoteSync)
                    _remoteCached = cfg;
            });
        }

        if (!_remoteFetchLogged)
        {
            log.Info("EOS remote config fetch queued.");
            _remoteFetchLogged = true;
        }
    }

    private static EosConfig? TryGetRemoteCached()
    {
        lock (RemoteSync)
            return _remoteCached;
    }

    private static async Task<EosConfig?> TryLoadRemoteConfigAsync(Logger log, RemoteConfigSource remote)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(RemoteFetchTimeoutSeconds)
            };

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, remote.Url);
            if (!string.IsNullOrWhiteSpace(remote.ApiKey))
                req.Headers.Add("X-Api-Key", remote.ApiKey);

            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log.Warn($"EOS remote config fetch failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var cfg = JsonSerializer.Deserialize<EosConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (cfg == null)
            {
                log.Warn("EOS remote config parse failed.");
                return null;
            }

            if (!cfg.IsValid(out var error))
            {
                log.Warn($"EOS remote config invalid: {error}");
                return null;
            }

            log.Info("EOS config loaded from remote service.");
            return cfg;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS remote config load failed: {ex.Message}");
            return null;
        }
    }

    private static string? TryCopyConfigToPrimary(string source, string primary, Logger log)
    {
        try
        {
            var dir = Path.GetDirectoryName(primary);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.Copy(source, primary, true);
            log.Info($"EOS config copied to {Paths.ToUiPath(primary)}");
            return primary;
        }
        catch (Exception ex)
        {
            log.Warn($"EOS config copy failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindConfigUpwards(string startDir, string relativePath)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            for (var i = 0; i < 6 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        catch
        {
            // Best-effort.
        }

        return null;
    }

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
