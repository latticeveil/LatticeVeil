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
            log.Info($"EOS config loaded. {config.GetSourceSummary()}");
            return config;
        }
        catch (FileNotFoundException ex)
        {
            log.Warn($"EOS config not found: {ex.Message}");
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
        if (TryBuildPublicConfigFromEnvironment(out var envConfig))
        {
            source = "environment variables EOS_PRODUCT_ID/EOS_SANDBOX_ID/EOS_DEPLOYMENT_ID/EOS_CLIENT_ID";
            return envConfig;
        }

        var searched = new List<string>();

        foreach (var candidate in EnumeratePublicConfigCandidates())
        {
            searched.Add(candidate.DisplayPath);
            if (!File.Exists(candidate.Path))
                continue;

            if (!TryReadJson(candidate.Path, out PublicEosConfigFile? parsed, out var parseError) || parsed == null)
                throw new InvalidDataException($"Invalid EOS public config at {candidate.DisplayPath}: {parseError}");

            source = candidate.DisplayPath;
            return parsed;
        }

        throw new FileNotFoundException($"Could not find eos.public.json. Searched: {string.Join(", ", searched)}");
    }

    private static bool TryBuildPublicConfigFromEnvironment(out PublicEosConfigFile config)
    {
        config = new PublicEosConfigFile();

        var productId = GetEnv("EOS_PRODUCT_ID");
        var sandboxId = GetEnv("EOS_SANDBOX_ID");
        var deploymentId = GetEnv("EOS_DEPLOYMENT_ID");
        var clientId = GetEnv("EOS_CLIENT_ID");

        if (string.IsNullOrWhiteSpace(productId)
            || string.IsNullOrWhiteSpace(sandboxId)
            || string.IsNullOrWhiteSpace(deploymentId)
            || string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        config.ProductId = productId;
        config.SandboxId = sandboxId;
        config.DeploymentId = deploymentId;
        config.ClientId = clientId;
        config.ProductName = Normalize(GetEnv("EOS_PRODUCT_NAME"), DefaultProductName);
        config.ProductVersion = Normalize(GetEnv("EOS_PRODUCT_VERSION"), DefaultProductVersion);
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
        if (string.IsNullOrWhiteSpace(config.ProductId)) missing.Add("ProductId");
        if (string.IsNullOrWhiteSpace(config.SandboxId)) missing.Add("SandboxId");
        if (string.IsNullOrWhiteSpace(config.DeploymentId)) missing.Add("DeploymentId");
        if (string.IsNullOrWhiteSpace(config.ClientId)) missing.Add("ClientId");
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

        var currentDir = SafeGetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            yield return BuildCandidate(Path.Combine(currentDir, "eos.public.json"), "CWD/eos.public.json");
            yield return BuildCandidate(Path.Combine(currentDir, "eos", "eos.public.json"), "CWD/eos/eos.public.json");
        }

        var projectRoot = ResolveProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            yield return BuildCandidate(Path.Combine(projectRoot, "eos", "eos.public.json"), "ProjectRoot/eos/eos.public.json");
            yield return BuildCandidate(Path.Combine(projectRoot, "eos.public.json"), "ProjectRoot/eos.public.json");
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
