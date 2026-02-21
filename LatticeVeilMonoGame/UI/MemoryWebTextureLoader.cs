using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.UI;

internal sealed class MemoryWebTextureLoader : IDisposable
{
    private readonly HttpClient _http;

    public MemoryWebTextureLoader(HttpClient? http = null)
    {
        _http = http ?? CreateHttpClient();
    }

    public async Task<byte[]?> DownloadImageBytesAsync(string? url, CancellationToken ct = default)
    {
        var normalizedUrl = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Headers.Pragma.ParseAdd("no-cache");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public Texture2D? CreateTextureFromBytes(GraphicsDevice graphicsDevice, byte[]? imageBytes)
    {
        if (graphicsDevice == null || imageBytes == null || imageBytes.Length == 0)
            return null;

        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            return Texture2D.FromStream(graphicsDevice, ms);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (handler.SupportsAutomaticDecompression)
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}
