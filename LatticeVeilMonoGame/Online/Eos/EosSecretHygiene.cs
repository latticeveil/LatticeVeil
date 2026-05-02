using System;
using System.IO;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Eos;

internal static class EosSecretHygiene
{
    public static void PurgeLocalSecrets(Logger log)
    {
        ClearSecretEnvironment();
        DeleteLocalSecretFile(log, Path.Combine(AppContext.BaseDirectory, "eos.private.json"));
        DeleteLocalSecretFile(log, Path.Combine(Paths.AppStateDir, "eos.private.json"));
        DeleteLocalSecretFile(log, Path.Combine(Paths.SystemStateDir, "eos.private.json"));
        DeleteLocalSecretFile(log, Path.Combine(Paths.RoamingAppDataDir, "LatticeVeil", "eos.private.json"));
        DeleteLocalSecretFile(log, Path.Combine(Paths.LegacyLocalAppStateDir, "eos.private.json"));
        DeleteLocalSecretFile(log, Path.Combine(Paths.LegacyLocalAppStateDir, "System", "eos.private.json"));
    }

    public static void ClearSecretEnvironment()
    {
        Environment.SetEnvironmentVariable("EOS_CLIENT_SECRET", null);
    }

    private static void DeleteLocalSecretFile(Logger log, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            File.Delete(path);
            log.Info($"Deleted local EOS secret file: {path}");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to delete local EOS secret file '{path}': {ex.Message}");
        }
    }
}
