using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Eos;

internal static class EosPublicDefaults
{
    public const string SourceName = "HardcodedDefaults";
    public const string ProductId = "0794da3c598d467c9ac126231f132351";
    public const string SandboxId = "f9417e71fd2645f4af2d072c6ca5b63d";
    public const string DeploymentId = "843fd58fa18545eaa1a7c8232eb7522b";
    public const string ClientId = "xyza7891M5Mc8NNr3Bln7pSpVXN7252e";
    public const string ProductName = "RedactedCraft";
    public const string ProductVersion = "1.0";
}

public sealed class EosConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly object LoadLogSync = new();
    private static readonly TimeSpan LoadLogCooldown = TimeSpan.FromSeconds(8);
    private static string _lastLoadLogSignature = string.Empty;
    private static DateTime _lastLoadLogUtc = DateTime.MinValue;

    private const string AppDataVendorFolder = "RedactedCraft";
    private const string DefaultProductName = "RedactedCraft";
    private const string DefaultProductVersion = "1.0";
    private const string DefaultLoginMode = "deviceid";

    public string ProductId { get; set; } = string.Empty;
    public string SandboxId { get; set; } = string.Empty;
    public string DeploymentId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string ProductName { get; set; } = DefaultProductName;
    public string ProductVersion { get; set; } = DefaultProductVersion;
    public string LoginMode { get; set; } = DefaultLoginMode;
    public string PublicSource { get; set; } = "none";
    public string SecretSource { get; set; } = "none";

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);

    public static EosConfig? Load(Logger log)
    {
        try
        {
            var config = LoadOrThrow();
            if (ShouldLogLoad(config))
            {
                if (string.Equals(config.PublicSource, EosPublicDefaults.SourceName, StringComparison.Ordinal))
                    log.Info("EOS public config source = HardcodedDefaults");
                log.Info($"EOS public config loaded from: {config.PublicSource}");
                log.Info($"EOS config loaded. {config.GetSourceSummary()}");
            }
            return config;
        }
        catch (FileNotFoundException ex)
        {
            log.Warn($"EOS public config missing: {ex.Message}");
            return null;
        }
    }

    public static EosConfig LoadOrThrow()
    {
        var publicConfig = LoadPublicConfigOrThrow(out var publicSource);
        var secret = ResolveClientSecret(out var secretSource);

        var resolved = new EosConfig
        {
            ProductId = Normalize(publicConfig.ProductId),
            SandboxId = Normalize(publicConfig.SandboxId),
            DeploymentId = Normalize(publicConfig.DeploymentId),
            ClientId = Normalize(publicConfig.ClientId),
            ClientSecret = string.IsNullOrWhiteSpace(secret) ? null : secret,
            ProductName = Normalize(publicConfig.ProductName, DefaultProductName),
            ProductVersion = Normalize(publicConfig.ProductVersion, DefaultProductVersion),
            LoginMode = Normalize(GetEnv("EOS_LOGIN_MODE"), DefaultLoginMode),
            PublicSource = publicSource,
            SecretSource = secretSource
        };

        ValidateRequiredPublicFields(resolved);
        return resolved;
    }

    public bool IsValid(out string? error)
    {
        var missing = GetMissingRequiredPublicFields(this);
        if (missing.Count > 0)
        {
            error = $"Missing required EOS public field(s): {string.Join(", ", missing)}.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool HasPublicConfigSource()
    {
        try
        {
            _ = LoadPublicConfigOrThrow(out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasSecretSource()
    {
        var secret = ResolveClientSecret(out _);
        return !string.IsNullOrWhiteSpace(secret);
    }

    public static string DescribePublicConfigSource()
    {
        try
        {
            _ = LoadPublicConfigOrThrow(out var source);
            return source;
        }
        catch
        {
            return "none";
        }
    }

    public static bool TryGetPublicIdentifiers(out string sandboxId, out string deploymentId)
    {
        sandboxId = string.Empty;
        deploymentId = string.Empty;

        try
        {
            var publicConfig = LoadPublicConfigOrThrow(out _);
            sandboxId = Normalize(publicConfig.SandboxId);
            deploymentId = Normalize(publicConfig.DeploymentId);
            return !string.IsNullOrWhiteSpace(sandboxId) || !string.IsNullOrWhiteSpace(deploymentId);
        }
        catch
        {
            return false;
        }
    }

    public string GetSourceSummary()
    {
        return $"public={PublicSource}; secret={(HasClientSecret ? SecretSource : "none")}; hasClientSecret={HasClientSecret}";
    }

    public override string ToString()
    {
        return $"EosConfig(ProductId={Mask(ProductId)}, SandboxId={Mask(SandboxId)}, DeploymentId={Mask(DeploymentId)}, ClientId={Mask(ClientId)}, HasClientSecret={HasClientSecret}, ProductName={ProductName}, ProductVersion={ProductVersion}, PublicSource={PublicSource}, SecretSource={(HasClientSecret ? SecretSource : "none")})";
    }

    private static PublicEosConfigFile LoadPublicConfigOrThrow(out string source)
    {
        var resolved = BuildHardcodedPublicConfig();
        var hasEnvOverrides = ApplyPublicEnvironmentOverrides(resolved);
        if (HasRequiredPublicFields(resolved))
        {
            source = hasEnvOverrides
                ? "environment overrides + HardcodedDefaults"
                : EosPublicDefaults.SourceName;
            return resolved;
        }

        // Optional legacy fallback for developer machines.
        var searched = new List<string>();

        foreach (var candidate in EnumeratePublicConfigCandidates())
        {
            searched.Add(candidate.DisplayPath);
            if (!File.Exists(candidate.Path))
                continue;

            if (!TryReadJson(candidate.Path, out PublicEosConfigFile? parsed, out var parseError) || parsed == null)
                throw new InvalidDataException($"Invalid EOS public config at {candidate.DisplayPath}: {parseError}");
            if (!HasRequiredPublicFields(parsed))
                continue;

            source = candidate.DisplayPath;
            return parsed;
        }

        throw new FileNotFoundException($"Could not resolve EOS public config (defaults/env/file). Searched files: {string.Join(", ", searched)}");
    }

    private static PublicEosConfigFile BuildHardcodedPublicConfig()
    {
        return new PublicEosConfigFile
        {
            ProductId = EosPublicDefaults.ProductId,
            SandboxId = EosPublicDefaults.SandboxId,
            DeploymentId = EosPublicDefaults.DeploymentId,
            ClientId = EosPublicDefaults.ClientId,
            ProductName = EosPublicDefaults.ProductName,
            ProductVersion = EosPublicDefaults.ProductVersion
        };
    }

    private static bool ApplyPublicEnvironmentOverrides(PublicEosConfigFile config)
    {
        var allowOverrides = Normalize(GetEnv("EOS_ALLOW_PUBLIC_ENV_OVERRIDES"));
        if (!string.Equals(allowOverrides, "1", StringComparison.Ordinal)
            && !string.Equals(allowOverrides, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var used = false;

        used |= TryOverrideRequiredPublicField("EOS_PRODUCT_ID", v => config.ProductId = v);
        used |= TryOverrideRequiredPublicField("EOS_SANDBOX_ID", v => config.SandboxId = v);
        used |= TryOverrideRequiredPublicField("EOS_DEPLOYMENT_ID", v => config.DeploymentId = v);
        used |= TryOverrideRequiredPublicField("EOS_CLIENT_ID", v => config.ClientId = v);

        var productName = Normalize(GetEnv("EOS_PRODUCT_NAME"));
        if (!string.IsNullOrWhiteSpace(productName))
        {
            config.ProductName = productName;
            used = true;
        }

        var productVersion = Normalize(GetEnv("EOS_PRODUCT_VERSION"));
        if (!string.IsNullOrWhiteSpace(productVersion))
        {
            config.ProductVersion = productVersion;
            used = true;
        }

        return used;
    }

    private static bool TryOverrideRequiredPublicField(string envName, Action<string> apply)
    {
        var raw = GetEnv(envName);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = Normalize(raw);
        if (IsMissingRequiredPublicValue(value))
            return false;

        apply(value);
        return true;
    }

    private static string? ResolveClientSecret(out string source)
    {
        var envSecret = GetEnv("EOS_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            source = "environment variable EOS_CLIENT_SECRET";
            return envSecret;
        }

        foreach (var candidate in EnumeratePrivateConfigCandidates())
        {
            if (!File.Exists(candidate.Path))
                continue;

            if (!TryReadJson(candidate.Path, out PrivateEosConfigFile? parsed, out _) || parsed == null)
                continue;

            if (string.IsNullOrWhiteSpace(parsed.ClientSecret))
                continue;

            source = candidate.DisplayPath;
            return parsed.ClientSecret.Trim();
        }

        source = "none";
        return null;
    }

    private static void ValidateRequiredPublicFields(EosConfig config)
    {
        var missing = GetMissingRequiredPublicFields(config);
        if (missing.Count == 0)
            return;

        throw new InvalidDataException($"Invalid EOS public config ({config.PublicSource}). Missing required field(s): {string.Join(", ", missing)}.");
    }

    private static List<string> GetMissingRequiredPublicFields(EosConfig config)
    {
        var missing = new List<string>();
        if (IsMissingRequiredPublicValue(config.ProductId)) missing.Add("ProductId");
        if (IsMissingRequiredPublicValue(config.SandboxId)) missing.Add("SandboxId");
        if (IsMissingRequiredPublicValue(config.DeploymentId)) missing.Add("DeploymentId");
        if (IsMissingRequiredPublicValue(config.ClientId)) missing.Add("ClientId");
        return missing;
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

        foreach (var candidate in BuildPublicConfigCandidates())
        {
            if (seen.Add(candidate.Path))
                yield return candidate;
        }
    }

    private static IEnumerable<ConfigCandidate> BuildPublicConfigCandidates()
    {
        yield return BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos.public.json"), "AppBase/eos.public.json");
        yield return BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos", "eos.public.json"), "AppBase/eos/eos.public.json");
        yield return BuildCandidate(Path.Combine(AppContext.BaseDirectory, "Defaults", "eos.public.json"), "AppBase/Defaults/eos.public.json");

        var currentDir = SafeGetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            yield return BuildCandidate(Path.Combine(currentDir, "eos.public.json"), "CWD/eos.public.json");
            yield return BuildCandidate(Path.Combine(currentDir, "eos", "eos.public.json"), "CWD/eos/eos.public.json");
            yield return BuildCandidate(Path.Combine(currentDir, "Defaults", "eos.public.json"), "CWD/Defaults/eos.public.json");
        }

        var projectRoot = ResolveProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            yield return BuildCandidate(Path.Combine(projectRoot, "eos", "eos.public.json"), "ProjectRoot/eos/eos.public.json");
            yield return BuildCandidate(Path.Combine(projectRoot, "eos.public.json"), "ProjectRoot/eos.public.json");
            yield return BuildCandidate(Path.Combine(projectRoot, "LatticeVeilMonoGame", "Defaults", "eos.public.json"), "ProjectRoot/LatticeVeilMonoGame/Defaults/eos.public.json");
            yield return BuildCandidate(Path.Combine(projectRoot, "Defaults", "eos.public.json"), "ProjectRoot/Defaults/eos.public.json");
        }
    }

    private static IEnumerable<ConfigCandidate> EnumeratePrivateConfigCandidates()
    {
        yield return BuildCandidate(Path.Combine(AppContext.BaseDirectory, "eos.private.json"), "AppBase/eos.private.json");

        var appDataPath = GetAppDataPrivateConfigPath();
        if (!string.IsNullOrWhiteSpace(appDataPath))
            yield return BuildCandidate(appDataPath, "%APPDATA%/RedactedCraft/eos.private.json");
    }

    private static string? GetAppDataPrivateConfigPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                return null;

            return Path.Combine(appData, AppDataVendorFolder, "eos.private.json");
        }
        catch
        {
            return null;
        }
    }

    private static ConfigCandidate BuildCandidate(string path, string displayPath)
    {
        return new ConfigCandidate(path.Trim(), displayPath);
    }

    private static string? ResolveProjectRoot()
    {
        foreach (var start in new[] { SafeGetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            var root = FindProjectRoot(start);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }

        return null;
    }

    private static string? FindProjectRoot(string startPath)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            for (var depth = 0; depth < 8 && dir != null; depth++)
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

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = Normalize(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        var trimmed = value.Trim();
        if (trimmed.Length <= 6)
            return "***";

        return trimmed.Substring(0, 3) + "***" + trimmed.Substring(trimmed.Length - 2);
    }

    private static bool ShouldLogLoad(EosConfig config)
    {
        var signature = $"{config.PublicSource}|{config.SecretSource}|{config.HasClientSecret}";
        var now = DateTime.UtcNow;

        lock (LoadLogSync)
        {
            if (string.Equals(signature, _lastLoadLogSignature, StringComparison.Ordinal)
                && now - _lastLoadLogUtc < LoadLogCooldown)
            {
                return false;
            }

            _lastLoadLogSignature = signature;
            _lastLoadLogUtc = now;
            return true;
        }
    }

    private static bool HasRequiredPublicFields(PublicEosConfigFile config)
    {
        return !IsMissingRequiredPublicValue(config.ProductId)
            && !IsMissingRequiredPublicValue(config.SandboxId)
            && !IsMissingRequiredPublicValue(config.DeploymentId)
            && !IsMissingRequiredPublicValue(config.ClientId);
    }

    private static bool IsMissingRequiredPublicValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim();
        if (normalized.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(normalized, "CHANGEME", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private sealed class PublicEosConfigFile
    {
        public string? ProductId { get; set; }
        public string? SandboxId { get; set; }
        public string? DeploymentId { get; set; }
        public string? ClientId { get; set; }
        public string? ProductName { get; set; }
        public string? ProductVersion { get; set; }
    }

    private sealed class PrivateEosConfigFile
    {
        public string? ClientSecret { get; set; }
    }

    private readonly struct ConfigCandidate
    {
        public ConfigCandidate(string path, string displayPath)
        {
            Path = path;
            DisplayPath = displayPath;
        }

        public string Path { get; }
        public string DisplayPath { get; }
    }
}
