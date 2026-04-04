using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Eos;

public sealed class EosIdentityStore
{
    private const string FileName = "eos.identity.json";
    private const string FriendCodePrefix = "RC-";
    private const string FriendCodeSalt = "RC-FRIENDCODE-V1";
    private const string FriendCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int FriendCodeLength = 8;
    private const int DefaultMaxDisplayNameLength = 16;

    public string ProductUserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ReservedUsername { get; set; } = "";

    public static string IdentityPath => Path.Combine(Paths.ConfigDir, FileName);

    public static int MaxDisplayNameLength
    {
        get
        {
#if EOS_SDK
            return Epic.OnlineServices.Connect.ConnectInterface.USERLOGININFO_DISPLAYNAME_MAX_LENGTH;
#else
            return DefaultMaxDisplayNameLength;
#endif
        }
    }

    public static EosIdentityStore LoadOrCreate(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            if (!File.Exists(IdentityPath))
                return new EosIdentityStore();

            var json = File.ReadAllText(IdentityPath);
            var store = JsonSerializer.Deserialize<EosIdentityStore>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new EosIdentityStore();

            store.ProductUserId ??= "";
            store.DisplayName ??= "";
            store.ReservedUsername ??= "";
            return store;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load EOS identity: {ex.Message}");
            return new EosIdentityStore();
        }
    }

    public void Save(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IdentityPath, json);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save EOS identity: {ex.Message}");
        }
    }

    public void Clear(Logger log)
    {
        ProductUserId = "";
        DisplayName = "";
        ReservedUsername = "";
        Save(log);
    }

    public string GetFriendCode()
    {
        if (string.IsNullOrWhiteSpace(ProductUserId))
            return "";
        return GenerateFriendCode(ProductUserId);
    }

    public string GetDisplayNameOrDefault(string fallback)
    {
        var name = NormalizeDisplayName(DisplayName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return NormalizeDisplayName(fallback);
    }

    public static string NormalizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var trimmed = name.Trim().Replace("\r", "").Replace("\n", "");
        var max = MaxDisplayNameLength;
        if (trimmed.Length > max)
            trimmed = trimmed.Substring(0, max);
        return trimmed;
    }

    public static string GenerateFriendCode(string productUserId)
    {
        if (string.IsNullOrWhiteSpace(productUserId))
            return "";

        var data = Encoding.UTF8.GetBytes($"{productUserId.Trim()}:{FriendCodeSalt}");
        var hash = SHA256.HashData(data);

        var chars = new char[FriendCodeLength];
        for (var i = 0; i < FriendCodeLength; i++)
            chars[i] = FriendCodeAlphabet[hash[i] % FriendCodeAlphabet.Length];

        return FriendCodePrefix + new string(chars);
    }
}
