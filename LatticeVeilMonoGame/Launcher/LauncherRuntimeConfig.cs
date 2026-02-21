using System.Text.Json;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

internal sealed class LauncherRuntimeConfig
{
    private sealed class RawModel
    {
        public string? VeilnetFunctionsBaseUrl { get; set; }
        public string? GameHashesGetUrl { get; set; }
        public string? VeilnetLauncherPageUrl { get; set; }
    }

    public static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LatticeVeil");

    public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "launcher_config.json");

    public string VeilnetFunctionsBaseUrl { get; init; } = string.Empty;
    public string GameHashesGetUrl { get; init; } = string.Empty;
    public string VeilnetLauncherPageUrl { get; init; } = string.Empty;

    public static LauncherRuntimeConfig Empty { get; } = new();

    public static LauncherRuntimeConfig Load(Logger log)
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return Empty;

            var json = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(json))
                return Empty;

            var raw = JsonSerializer.Deserialize<RawModel>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (raw == null)
                return Empty;

            var loaded = new LauncherRuntimeConfig
            {
                VeilnetFunctionsBaseUrl = NormalizeUrl(raw.VeilnetFunctionsBaseUrl, trimTrailingSlash: true),
                GameHashesGetUrl = NormalizeUrl(raw.GameHashesGetUrl),
                VeilnetLauncherPageUrl = NormalizeUrl(raw.VeilnetLauncherPageUrl)
            };

            log.Info($"Loaded launcher runtime config from {ConfigPath}");
            return loaded;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load launcher runtime config ({ConfigPath}): {ex.Message}");
            return Empty;
        }
    }

    private static string NormalizeUrl(string? value, bool trimTrailingSlash = false)
    {
        var url = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        return trimTrailingSlash ? url.TrimEnd('/') : url;
    }
}
