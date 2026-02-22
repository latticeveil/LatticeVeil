using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class AssetResolver
{
    public static string Resolve(string relativePath) => Path.Combine(Paths.GetAssetsDir(), relativePath);

    public static bool TryResolve(string relativePath, out string fullPath)
    {
        fullPath = Path.Combine(Paths.GetAssetsDir(), relativePath);
        return File.Exists(fullPath);
    }

    public static string DescribeInstallLocation() => Paths.GetAssetsDir();
}
