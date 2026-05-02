using System;
using LatticeVeilMonoGame.Online.Eos;

namespace LatticeVeilMonoGame.Core;

public enum EosRuntimeReason
{
    Ready,
    DisabledByEnvironment,
    SdkNotCompiled,
    ConfigMissing,
    ClientUnavailable,
    Connecting
}

public readonly struct EosRuntimeSnapshot
{
    public bool IsSdkCompiled { get; init; }
    public bool HasConfig { get; init; }
    public bool IsClientCreated { get; init; }
    public bool IsLoggedIn { get; init; }
    public EosRuntimeReason Reason { get; init; }
    public string StatusText { get; init; }
}

public static class EosRuntimeStatus
{
    public static EosRuntimeSnapshot Evaluate(EosClient? client)
    {
        var sdkCompiled = IsSdkCompiled();
        var disabled = IsDisabledByEnvironment();
        var hasConfig = HasAnyConfigSource();
        var hasClient = client != null;
        var isLoggedIn = client?.IsLoggedIn == true;

        var reason = ResolveReason(sdkCompiled, disabled, hasConfig, hasClient, isLoggedIn);
        var status = reason switch
        {
            EosRuntimeReason.Ready => "EOS READY",
            EosRuntimeReason.DisabledByEnvironment => "EOS DISABLED BY ENV",
            EosRuntimeReason.SdkNotCompiled => "EOS SDK NOT COMPILED",
            EosRuntimeReason.ConfigMissing => "EOS CONFIG MISSING",
            EosRuntimeReason.ClientUnavailable => "EOS CLIENT UNAVAILABLE",
            _ => "EOS CONNECTING"
        };

        return new EosRuntimeSnapshot
        {
            IsSdkCompiled = sdkCompiled,
            HasConfig = hasConfig,
            IsClientCreated = hasClient,
            IsLoggedIn = isLoggedIn,
            Reason = reason,
            StatusText = status
        };
    }

    public static string DescribeConfigSource()
    {
        if (IsDisabledByEnvironment())
            return "disabled-by-env";
        return EosConfig.DescribePublicConfigSource();
    }

    public static bool IsSdkCompiled()
    {
#if EOS_SDK
        return true;
#else
        return false;
#endif
    }

    public static bool IsEosLibraryPresent()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EOSSDK-Win64-Shipping.dll");
            return System.IO.File.Exists(path);
        }
        catch { return false; }
    }

    public static bool HasAnyConfigSource()
    {
        if (IsDisabledByEnvironment())
            return false;
        return EosConfig.HasPublicConfigSource();
    }

    private static EosRuntimeReason ResolveReason(bool sdkCompiled, bool disabled, bool hasConfig, bool hasClient, bool loggedIn)
    {
        if (disabled)
            return EosRuntimeReason.DisabledByEnvironment;
        if (!sdkCompiled)
            return EosRuntimeReason.SdkNotCompiled;
        if (!hasConfig)
            return EosRuntimeReason.ConfigMissing;
        if (!hasClient)
            return EosRuntimeReason.ClientUnavailable;
        if (!loggedIn)
            return EosRuntimeReason.Connecting;
        return EosRuntimeReason.Ready;
    }

    private static bool IsDisabledByEnvironment()
    {
        var disabled = Environment.GetEnvironmentVariable("EOS_DISABLED") ?? Environment.GetEnvironmentVariable("EOS_DISABLE");
        if (string.IsNullOrWhiteSpace(disabled))
            return false;

        disabled = disabled.Trim();
        return disabled == "1"
            || disabled.Equals("true", StringComparison.OrdinalIgnoreCase)
            || disabled.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

}
