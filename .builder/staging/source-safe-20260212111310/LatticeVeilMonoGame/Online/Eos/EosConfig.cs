using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Eos;

public sealed class EosConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool _disabledLogged;
    private static bool _missingPublicLogged;
    private static bool _missingSecretLogged;
    private static bool _invalidEnvLogged;
    private static bool _invalidPublicLogged;
    private static bool _invalidSecretFileLogged;
    private static bool _invalidLegacyLogged;
    private static bool _missingPathWarningLogged;

    public string ProductId { get; set; } = "";
    public string SandboxId { get; set; } = "";
    public string DeploymentId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ProductName { get; set; } = "LatticeVeil";
    public string ProductVersion { get; set; } = "1.0.0";
    public string LoginMode { get; set; } = "deviceid";

    private sealed class PublicEosConfig
    {
        public string ProductId { get; set; } = "";
        public string SandboxId { get; set; } = "";
        public string DeploymentId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ProductName { get; set; } = "LatticeVeil";
        public string ProductVersion { get; set; } = "1.0.0";
        public string LoginMode { get; set; } = "deviceid";
    }

    private sealed class PrivateEosConfig
    {
        public string ClientSecret { get; set; } = "";
    }

    public static EosConfig? Load(Logger log)
    {
        if (IsDisabled())
        {
            if (!_disabledLogged)
            {
                log.Info("EOS disabled; LAN-only.");
                _disabledLogged = true;
            }
            return null;
        }

        if (TryLoadFullEnvironmentConfig(out var envConfig))
        {
            if (envConfig.IsValid(out var envError))
                return envConfig;

            if (!_invalidEnvLogged)
            {
                log.Warn($"EOS environment config invalid: {envError}");
                _invalidEnvLogged = true;
            }
        }

        if (TryLoadLegacyConfig(out var legacyConfig, out var legacySource, out var legacyError))
        {
            if (string.IsNullOrWhiteSpace(legacyConfig.ProductName))
                legacyConfig.ProductName = "LatticeVeil";

            if (string.IsNullOrWhiteSpace(legacyConfig.ProductVersion))
                legacyConfig.ProductVersion = "1.0.0";

            if (legacyConfig.IsValid(out var legacyValidationError))
            {
                if (string.IsNullOrWhiteSpace(legacyConfig.LoginMode))
                    legacyConfig.LoginMode = "deviceid";

                log.Info($"EOS config loaded ({legacySource}).");
                return legacyConfig;
            }

            if (!_invalidLegacyLogged)
            {
                log.Warn($"EOS legacy config invalid: {legacyValidationError}");
                _invalidLegacyLogged = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(legacyError) && !_invalidLegacyLogged)
        {
            log.Warn(legacyError);
            _invalidLegacyLogged = true;
        }

        if (!TryLoadPublicConfig(out var publicConfig, out var publicSource, out var publicError))
        {
            if (!_missingPublicLogged)
            {
                log.Info("EOS disabled; LAN-only.");
                _missingPublicLogged = true;
            }

            if (!string.IsNullOrWhiteSpace(publicError) && !_invalidPublicLogged)
            {
                log.Warn(publicError);
                _invalidPublicLogged = true;
            }

            return null;
        }

        if (!TryResolveClientSecret(out var clientSecret, out var secretSource, out var secretError))
        {
            if (!_missingSecretLogged)
            {
                log.Warn("EOS public config found but secret missing; set EOS_CLIENT_SECRET or provide eos.private.json.");
                _missingSecretLogged = true;
            }

            if (!string.IsNullOrWhiteSpace(secretError) && !_invalidSecretFileLogged)
            {
                log.Warn(secretError);
                _invalidSecretFileLogged = true;
            }

            return null;
        }

        var config = new EosConfig
        {
            ProductId = publicConfig.ProductId.Trim(),
            SandboxId = publicConfig.SandboxId.Trim(),
            DeploymentId = publicConfig.DeploymentId.Trim(),
            ClientId = publicConfig.ClientId.Trim(),
            ClientSecret = clientSecret.Trim(),
            ProductName = string.IsNullOrWhiteSpace(publicConfig.ProductName) ? "LatticeVeil" : publicConfig.ProductName.Trim(),
            ProductVersion = string.IsNullOrWhiteSpace(publicConfig.ProductVersion) ? "1.0.0" : publicConfig.ProductVersion.Trim(),
            LoginMode = string.IsNullOrWhiteSpace(publicConfig.LoginMode) ? "deviceid" : publicConfig.LoginMode.Trim()
        };

        if (!config.IsValid(out var error))
        {
            if (!_invalidPublicLogged)
            {
                log.Warn($"EOS config invalid: {error}");
                _invalidPublicLogged = true;
            }
            return null;
        }

        log.Info($"EOS config loaded ({publicSource}; secret={secretSource}).");
        return config;
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

    public static bool HasPublicConfigSource()
    {
        if (TryLoadFullEnvironmentConfig(out var envConfig) && !string.IsNullOrWhiteSpace(envConfig.ProductId))
            return true;

        if (TryLoadLegacyConfig(out var legacyConfig, out _, out _) && !string.IsNullOrWhiteSpace(legacyConfig.ProductId))
            return true;

        return TryLoadPublicConfig(out _, out _, out _);
    }

    public static bool HasSecretSource()
    {
        if (TryLoadLegacyConfig(out var legacyConfig, out _, out _) && !string.IsNullOrWhiteSpace(legacyConfig.ClientSecret))
            return true;

        return TryResolveClientSecret(out _, out _, out _);
    }

    public static string DescribePublicConfigSource()
    {
        if (TryLoadFullEnvironmentConfig(out var envConfig) && !string.IsNullOrWhiteSpace(envConfig.ProductId))
            return "environment";

        if (TryLoadLegacyConfig(out var legacyConfig, out var legacySource, out _) && !string.IsNullOrWhiteSpace(legacyConfig.ProductId))
            return legacySource;

        if (TryLoadPublicConfig(out _, out var source, out _))
            return source;

        return "none";
    }

    public static bool TryGetPublicIdentifiers(out string sandboxId, out string deploymentId)
    {
        sandboxId = string.Empty;
        deploymentId = string.Empty;

        if (TryLoadPublicConfig(out var publicConfig, out _, out _))
        {
            sandboxId = publicConfig.SandboxId;
            deploymentId = publicConfig.DeploymentId;
            return !string.IsNullOrWhiteSpace(sandboxId) || !string.IsNullOrWhiteSpace(deploymentId);
        }

        if (TryLoadLegacyConfig(out var legacyConfig, out _, out _))
        {
            sandboxId = legacyConfig.SandboxId;
            deploymentId = legacyConfig.DeploymentId;
            return !string.IsNullOrWhiteSpace(sandboxId) || !string.IsNullOrWhiteSpace(deploymentId);
        }

        if (TryLoadFullEnvironmentConfig(out var envConfig))
        {
            sandboxId = envConfig.SandboxId;
            deploymentId = envConfig.DeploymentId;
            return !string.IsNullOrWhiteSpace(sandboxId) || !string.IsNullOrWhiteSpace(deploymentId);
        }

        return false;
    }

    private static bool TryLoadLegacyConfig(out EosConfig config, out string source, out string? error)
    {
        config = new EosConfig();
        source = "none";
        error = null;

        foreach (var candidate in EnumerateLegacyConfigCandidates())
        {
            if (!candidate.Exists)
                continue;

            if (!TryReadJson(candidate.Path, out EosConfig? parsed, out var parseError) || parsed == null)
            {
                error = $"EOS legacy config parse failed at {candidate.DisplayPath}: {parseError}";
                continue;
            }

            config = parsed;
            source = candidate.DisplayPath;
            return true;
        }

        return false;
    }

    private static bool TryLoadFullEnvironmentConfig(out EosConfig config)
    {
        config = new EosConfig
        {
            ProductId = GetEnv("EOS_PRODUCT_ID") ?? "",
            SandboxId = GetEnv("EOS_SANDBOX_ID") ?? "",
            DeploymentId = GetEnv("EOS_DEPLOYMENT_ID") ?? "",
            ClientId = GetEnv("EOS_CLIENT_ID") ?? "",
            ClientSecret = GetEnv("EOS_CLIENT_SECRET") ?? "",
            ProductName = GetEnv("EOS_PRODUCT_NAME") ?? "LatticeVeil",
            ProductVersion = GetEnv("EOS_PRODUCT_VERSION") ?? "1.0.0",
            LoginMode = GetEnv("EOS_LOGIN_MODE") ?? "deviceid"
        };

        return !string.IsNullOrWhiteSpace(config.ProductId)
            || !string.IsNullOrWhiteSpace(config.SandboxId)
            || !string.IsNullOrWhiteSpace(config.DeploymentId)
            || !string.IsNullOrWhiteSpace(config.ClientId)
            || !string.IsNullOrWhiteSpace(config.ClientSecret);
    }

    private static bool TryLoadPublicConfig(out PublicEosConfig config, out string source, out string? error)
    {
        config = new PublicEosConfig();
        source = "none";
        error = null;

        foreach (var candidate in EnumeratePublicConfigCandidates())
        {
            if (!candidate.Exists)
                continue;

            if (candidate.IsExampleFile)
            {
                // Example files are hints only, never runtime config.
                continue;
            }

            if (!TryReadJson(candidate.Path, out PublicEosConfig? parsed, out var parseError) || parsed == null)
            {
                error = $"EOS public config parse failed at {candidate.DisplayPath}: {parseError}";
                continue;
            }

            if (!ValidatePublicConfig(parsed, out var validationError))
            {
                error = $"EOS public config invalid at {candidate.DisplayPath}: {validationError}";
                continue;
            }

            config = parsed;
            source = candidate.DisplayPath;
            return true;
        }

        return false;
    }

    private static bool TryResolveClientSecret(out string secret, out string source, out string? error)
    {
        secret = string.Empty;
        source = "none";
        error = null;

        var envSecret = GetEnv("EOS_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            secret = envSecret.Trim();
            source = "environment";
            return true;
        }

        foreach (var candidate in EnumeratePrivateConfigCandidates())
        {
            if (!candidate.Exists)
                continue;

            if (!TryReadJson(candidate.Path, out PrivateEosConfig? parsed, out var parseError) || parsed == null)
            {
                error = $"EOS private config parse failed at {candidate.DisplayPath}: {parseError}";
                continue;
            }

            if (string.IsNullOrWhiteSpace(parsed.ClientSecret))
            {
                error = $"EOS private config missing ClientSecret at {candidate.DisplayPath}.";
                continue;
            }

            secret = parsed.ClientSecret.Trim();
            source = candidate.DisplayPath;
            return true;
        }

        return false;
    }

    private static bool ValidatePublicConfig(PublicEosConfig config, out string? error)
    {
        if (string.IsNullOrWhiteSpace(config.ProductId))
        {
            error = "ProductId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.SandboxId))
        {
            error = "SandboxId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.DeploymentId))
        {
            error = "DeploymentId missing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            error = "ClientId missing.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadJson<T>(string path, out T? value, out string? error) where T : class
    {
        value = null;
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value == null)
            {
                error = "deserialized null";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<ConfigCandidate> EnumeratePublicConfigCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var envPath = GetEnv("EOS_PUBLIC_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var candidate = BuildCandidate(envPath, "EOS_PUBLIC_CONFIG_PATH");
            if (seen.Add(candidate.Path))
                yield return candidate;
        }

        var projectRoot = ResolveProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectPublic = BuildCandidate(Path.Combine(projectRoot, "eos.public.json"), "project/eos.public.json");
            if (seen.Add(projectPublic.Path))
                yield return projectPublic;

            var projectExample = BuildCandidate(Path.Combine(projectRoot, "eos.public.example.json"), "project/eos.public.example.json", isExampleFile: true);
            if (seen.Add(projectExample.Path))
                yield return projectExample;
        }

        var rootCandidate = BuildCandidate(Path.Combine(Paths.RootDir, "eos.public.json"), "Root/eos.public.json");
        if (seen.Add(rootCandidate.Path))
            yield return rootCandidate;

        var configCandidate = BuildCandidate(Path.Combine(Paths.ConfigDir, "eos.public.json"), "Config/eos.public.json");
        if (seen.Add(configCandidate.Path))
            yield return configCandidate;

        var appBaseCandidate = BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos.public.json"), "AppBase/eos.public.json");
        if (seen.Add(appBaseCandidate.Path))
            yield return appBaseCandidate;

        foreach (var candidate in EnumerateAncestorFileCandidates("eos.public.json", "Ancestor", seen))
            yield return candidate;
    }

    private static IEnumerable<ConfigCandidate> EnumeratePrivateConfigCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var envPath = GetEnv("EOS_PRIVATE_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var envCandidate = BuildCandidate(envPath, "EOS_PRIVATE_CONFIG_PATH");
            if (seen.Add(envCandidate.Path))
                yield return envCandidate;
        }

        var projectRoot = ResolveProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectPrivate = BuildCandidate(Path.Combine(projectRoot, "eos.private.json"), "project/eos.private.json");
            if (seen.Add(projectPrivate.Path))
                yield return projectPrivate;
        }

        var rootCandidate = BuildCandidate(Path.Combine(Paths.RootDir, "eos.private.json"), "Root/eos.private.json");
        if (seen.Add(rootCandidate.Path))
            yield return rootCandidate;

        var configCandidate = BuildCandidate(Path.Combine(Paths.ConfigDir, "eos.private.json"), "Config/eos.private.json");
        if (seen.Add(configCandidate.Path))
            yield return configCandidate;

        if (IsOfficialRuntime())
        {
            var appBaseCandidate = BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos.private.json"), "AppBase/eos.private.json");
            if (seen.Add(appBaseCandidate.Path))
                yield return appBaseCandidate;
        }

        foreach (var candidate in EnumerateAncestorFileCandidates("eos.private.json", "Ancestor", seen))
            yield return candidate;
    }

    private static IEnumerable<ConfigCandidate> EnumerateLegacyConfigCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var projectRoot = ResolveProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var projectLegacy = BuildCandidate(Path.Combine(projectRoot, "eos.config.json"), "project/eos.config.json");
            if (seen.Add(projectLegacy.Path))
                yield return projectLegacy;
        }

        var rootLegacy = BuildCandidate(Path.Combine(Paths.RootDir, "eos.config.json"), "Root/eos.config.json");
        if (seen.Add(rootLegacy.Path))
            yield return rootLegacy;

        var configLegacy = BuildCandidate(Path.Combine(Paths.ConfigDir, "eos.config.json"), "Config/eos.config.json");
        if (seen.Add(configLegacy.Path))
            yield return configLegacy;

        var appBaseLegacy = BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos.config.json"), "AppBase/eos.config.json");
        if (seen.Add(appBaseLegacy.Path))
            yield return appBaseLegacy;

        foreach (var candidate in EnumerateAncestorFileCandidates("eos.config.json", "Ancestor", seen))
            yield return candidate;
    }

    private static IEnumerable<ConfigCandidate> EnumerateAncestorFileCandidates(string fileName, string sourcePrefix, HashSet<string> seen)
    {
        var starts = new List<(string Path, string Label)>
        {
            (AppContext.BaseDirectory, "appbase"),
            (SafeGetCurrentDirectory(), "cwd")
        };

        for (var i = 0; i < starts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(starts[i].Path))
                continue;

            var dir = SafeCreateDirectoryInfo(starts[i].Path);
            for (var depth = 0; depth < 10 && dir != null; depth++)
            {
                var candidatePath = Path.Combine(dir.FullName, fileName);
                var candidate = BuildCandidate(candidatePath, $"{sourcePrefix}/{starts[i].Label}/{depth}/{fileName}");
                if (seen.Add(candidate.Path))
                    yield return candidate;

                dir = dir.Parent;
            }
        }
    }

    private static DirectoryInfo? SafeCreateDirectoryInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return new DirectoryInfo(path);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGetCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ConfigCandidate BuildCandidate(string path, string displayPath, bool isExampleFile = false)
    {
        var normalized = path.Trim();
        var exists = false;
        try
        {
            exists = File.Exists(normalized);
        }
        catch
        {
            exists = false;
        }

        if (!exists && (displayPath == "EOS_PUBLIC_CONFIG_PATH" || displayPath == "EOS_PRIVATE_CONFIG_PATH") && !_missingPathWarningLogged)
        {
            _missingPathWarningLogged = true;
        }

        return new ConfigCandidate(normalized, displayPath, exists, isExampleFile);
    }

    private static bool IsOfficialRuntime()
    {
        var flavor = GetEnv("LV_BUILD_FLAVOR");
        if (!string.IsNullOrWhiteSpace(flavor) && flavor.Equals("official", StringComparison.OrdinalIgnoreCase))
            return true;

        var proofPath = Path.Combine(AppContext.BaseDirectory, "official_build.sig");
        return File.Exists(proofPath);
    }

    private static string? ResolveProjectRoot()
    {
        var candidates = new List<string>();
        try { candidates.Add(Directory.GetCurrentDirectory()); } catch { }
        candidates.Add(AppContext.BaseDirectory);

        for (var i = 0; i < candidates.Count; i++)
        {
            var root = FindProjectRoot(candidates[i]);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }

        return null;
    }

    private static string? FindProjectRoot(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        try
        {
            var dir = new DirectoryInfo(startPath);
            for (var i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LatticeVeil.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // Best-effort only.
        }

        return null;
    }

    private static bool IsDisabled()
    {
        var disabled = GetEnv("EOS_DISABLED") ?? GetEnv("EOS_DISABLE");
        if (string.IsNullOrWhiteSpace(disabled))
            return false;

        disabled = disabled.Trim();
        return disabled == "1"
            || disabled.Equals("true", StringComparison.OrdinalIgnoreCase)
            || disabled.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private readonly struct ConfigCandidate
    {
        public ConfigCandidate(string path, string displayPath, bool exists, bool isExampleFile)
        {
            Path = path;
            DisplayPath = displayPath;
            Exists = exists;
            IsExampleFile = isExampleFile;
        }

        public string Path { get; }
        public string DisplayPath { get; }
        public bool Exists { get; }
        public bool IsExampleFile { get; }
    }
}
