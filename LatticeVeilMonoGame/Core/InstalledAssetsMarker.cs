namespace LatticeVeilMonoGame.Core;

public sealed class InstalledAssetsMarker
{
    public string? Tag { get; set; }
    public string? PublishedAt { get; set; }
    public string AssetName { get; set; } = "Assets.zip";
    public long Size { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InstalledAt { get; set; }
}
