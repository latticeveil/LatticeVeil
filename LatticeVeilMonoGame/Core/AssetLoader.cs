using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class AssetLoader : IDisposable
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _fallbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Logger? _log;

    public AssetLoader(GraphicsDevice graphicsDevice, Logger? log = null)
    {
        _graphicsDevice = graphicsDevice;
        _log = log;
    }

    public GraphicsDevice GraphicsDevice => _graphicsDevice;

    public Texture2D LoadTexture(string relativePath)
    {
        // Use a normalized cache key (forward slashes)
        var key = relativePath.Replace('\\', '/');
        if (_textures.TryGetValue(key, out var cached))
            return cached;

        if (AssetResolver.TryResolve(relativePath, out var filePath))
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var tex = Texture2D.FromStream(_graphicsDevice, fs);
                _textures[key] = tex;
                return tex;
            }
            catch (Exception ex)
            {
                LogMissing(key, filePath, $"Failed to load texture: {ex.Message}");
                return GetFallback(key);
            }
        }

        var resolved = AssetResolver.Resolve(relativePath);
        LogMissing(key, resolved, "Texture file not found.");
        return GetFallback(key);
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values)
            t.Dispose();
        _textures.Clear();

        foreach (var t in _fallbacks.Values)
            t.Dispose();
        _fallbacks.Clear();
    }

    private Texture2D GetFallback(string key)
    {
        if (_fallbacks.TryGetValue(key, out var fallback))
            return fallback;

        fallback = CreateCheckerFallbackTexture();
        _fallbacks[key] = fallback;
        return fallback;
    }

    private Texture2D CreateCheckerFallbackTexture()
    {
        const int size = 16;
        var tex = new Texture2D(_graphicsDevice, size, size);
        var data = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var even = ((x / 4) + (y / 4)) % 2 == 0;
                data[y * size + x] = even ? new Color(200, 0, 200) : new Color(20, 20, 20);
            }
        }

        tex.SetData(data);
        return tex;
    }

    private void LogMissing(string key, string resolvedPath, string reason)
    {
        if (!_missingLogged.Add(key))
            return;

        _log?.Warn($"Missing texture '{key}'. {reason} Resolved: {resolvedPath}. Install assets to: {AssetResolver.DescribeInstallLocation()}");
    }
}
