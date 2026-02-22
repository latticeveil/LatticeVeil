using System;

namespace LatticeVeilMonoGame.Core;

public sealed class AssetReleaseInfo
{
    public string AssetName { get; set; } = "Assets.zip";
    public string DownloadUrl { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public long? Size { get; set; }
    public bool IsFallback { get; set; }
}
