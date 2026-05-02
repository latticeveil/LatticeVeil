using System;
using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core;

public static class FriendPresenceFormatter
{
    public static string BuildLabel(
        string? rawStatus,
        bool isHosting = false,
        bool isInWorld = false,
        bool isMultiplayer = false,
        string? worldName = null,
        string? gameMode = null)
    {
        var normalizedRawStatus = NormalizeStatus(rawStatus);
        var normalizedWorld = NormalizeWorldName(worldName);
        if (IsOffline(normalizedRawStatus))
            return "OFFLINE";

        if (IsWorldState(normalizedRawStatus, isHosting, isInWorld, isMultiplayer, normalizedWorld, gameMode))
        {
            return string.IsNullOrWhiteSpace(normalizedWorld)
                ? "ONLINE - IN WORLD"
                : $"ONLINE - IN WORLD \"{normalizedWorld}\"";
        }

        if (IsMenuState(normalizedRawStatus))
            return "ONLINE - IN MENU";

        return "ONLINE - IN LAUNCHER";
    }

    public static string BuildPrimaryStatus(
        string? rawStatus,
        bool isHosting = false,
        bool isInWorld = false,
        bool isMultiplayer = false,
        string? worldName = null,
        string? gameMode = null)
    {
        var normalized = NormalizeStatus(rawStatus);
        var normalizedWorld = NormalizeWorldName(worldName);
        if (IsOffline(normalized))
            return "OFFLINE";
        if (IsWorldState(normalized, isHosting, isInWorld, isMultiplayer, normalizedWorld, gameMode))
            return "IN WORLD";
        if (IsMenuState(normalized))
            return "IN MENU";
        return "IN LAUNCHER";
    }

    private static string NormalizeStatus(string? rawStatus)
    {
        return (rawStatus ?? string.Empty).Replace('_', ' ').Trim().ToUpperInvariant();
    }

    private static string NormalizeWorldName(string? worldName)
    {
        var value = (worldName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        if (string.Equals(value, "WORLD", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return value;
    }

    private static string NormalizeGameMode(string? gameMode)
    {
        var value = (gameMode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.ToLowerInvariant() switch
        {
            "artificer" => "ARTIFICER",
            "veilwalker" => "VEILWALKER",
            "veilseer" => "VEILSEER",
            "creative" => "ARTIFICER",
            "survival" => "VEILWALKER",
            "spectator" => "VEILSEER",
            _ => value.ToUpperInvariant()
        };
    }

    private static bool IsOffline(string normalizedStatus)
    {
        return string.IsNullOrWhiteSpace(normalizedStatus)
            || normalizedStatus.Contains("OFFLINE", StringComparison.Ordinal);
    }

    private static bool IsWorldState(
        string normalizedStatus,
        bool isHosting,
        bool isInWorld,
        bool isMultiplayer,
        string normalizedWorld,
        string? gameMode)
    {
        return isHosting
            || isInWorld
            || isMultiplayer
            || !string.IsNullOrWhiteSpace(normalizedWorld)
            || !string.IsNullOrWhiteSpace(gameMode)
            || normalizedStatus.Contains("IN WORLD", StringComparison.Ordinal)
            || normalizedStatus.Contains("IN GAME", StringComparison.Ordinal)
            || normalizedStatus.Contains("INGAME", StringComparison.Ordinal);
    }

    private static bool IsMenuState(string normalizedStatus)
    {
        return normalizedStatus.Contains("IN MENU", StringComparison.Ordinal)
            || normalizedStatus.Contains("MENU", StringComparison.Ordinal);
    }
}
