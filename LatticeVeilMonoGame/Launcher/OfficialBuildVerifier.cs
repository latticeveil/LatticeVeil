using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

internal sealed class OfficialBuildVerifier
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Logger _log;
    private readonly string _endpoint;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(10);
    private readonly object _cacheSync = new();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private OfficialHashesPayload? _cachePayload;

    internal enum VerifyFailure
    {
        None,
        MissingFile,
        InvalidChannel,
        FetchFailed,
        InvalidRemotePayload,
        HashMismatch,
        ComputeFailed
    }

    internal readonly struct VerifyResult
    {
        public bool Ok { get; init; }
        public VerifyFailure Failure { get; init; }
        public string Message { get; init; }
        public string? Channel { get; init; }
        public string? ExpectedHash { get; init; }
        public string? ActualHash { get; init; }
    }

    private sealed class OfficialHashEntry
    {
        [JsonPropertyName("hash_sha256")]
        public string HashSha256 { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }

    private sealed class OfficialHashesPayload
    {
        [JsonPropertyName("dev")]
        public OfficialHashEntry? Dev { get; set; }

        [JsonPropertyName("release")]
        public OfficialHashEntry? Release { get; set; }
    }

    public OfficialBuildVerifier(Logger log, string endpoint)
    {
        _log = log;
        _endpoint = (endpoint ?? string.Empty).Trim();
    }

    public async Task<VerifyResult> VerifyAsync(string channel, string filePath, CancellationToken ct = default)
    {
        channel = NormalizeChannel(channel);
        if (string.IsNullOrWhiteSpace(channel))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.InvalidChannel,
                Message = "Invalid hash channel. Expected 'dev' or 'release'."
            };
        }

        filePath = (filePath ?? string.Empty).Trim();
        if (!File.Exists(filePath))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.MissingFile,
                Channel = channel,
                Message = $"Build verification file not found: {filePath}"
            };
        }

        var fetch = await FetchOfficialHashesAsync(ct).ConfigureAwait(false);
        if (!fetch.Ok || fetch.Payload == null)
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.FetchFailed,
                Channel = channel,
                Message = fetch.Message
            };
        }

        var remoteEntry = channel == "dev" ? fetch.Payload.Dev : fetch.Payload.Release;
        var expectedHash = ResolveRemoteHash(remoteEntry);
        if (!IsSha256(expectedHash))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.InvalidRemotePayload,
                Channel = channel,
                Message = $"Official {channel} hash is missing or invalid."
            };
        }

        string localHash;
        try
        {
            localHash = Hashing.Sha256File(filePath);
        }
        catch (Exception ex)
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.ComputeFailed,
                Channel = channel,
                Message = $"Failed to compute local SHA256: {ex.Message}"
            };
        }

        if (!string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.HashMismatch,
                Channel = channel,
                Message = "This build is not official for online play. Please download the official release.",
                ExpectedHash = expectedHash,
                ActualHash = localHash
            };
        }

        return new VerifyResult
        {
            Ok = true,
            Failure = VerifyFailure.None,
            Channel = channel,
            Message = "Official hash verified.",
            ExpectedHash = expectedHash,
            ActualHash = localHash
        };
    }

    private async Task<(bool Ok, string Message, OfficialHashesPayload? Payload)> FetchOfficialHashesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            return (false, "Official hash endpoint is not configured.", null);

        var now = DateTime.UtcNow;
        lock (_cacheSync)
        {
            if (_cachePayload != null && now < _cacheExpiresUtc)
                return (true, "cache", _cachePayload);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Official hash fetch failed (HTTP {(int)response.StatusCode}).", null);
            }

            var payload = JsonSerializer.Deserialize<OfficialHashesPayload>(body, JsonOptions);
            if (payload?.Dev == null || payload.Release == null)
            {
                return (false, "Official hash payload is missing dev/release rows.", null);
            }

            lock (_cacheSync)
            {
                _cachePayload = payload;
                _cacheExpiresUtc = DateTime.UtcNow.Add(_cacheTtl);
            }

            return (true, "ok", payload);
        }
        catch (Exception ex)
        {
            _log.Warn($"Official hash fetch error: {ex.Message}");
            return (false, $"Official hash fetch error: {ex.Message}", null);
        }
    }

    private static string NormalizeChannel(string channel)
    {
        var value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return value is "dev" or "release" ? value : string.Empty;
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex) return false;
        }

        return true;
    }

    private static string ResolveRemoteHash(OfficialHashEntry? entry)
    {
        var candidates = new[]
        {
            entry?.HashSha256,
            entry?.Sha256,
            entry?.Hash
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            var c = (candidates[i] ?? string.Empty).Trim().ToLowerInvariant();
            if (IsSha256(c))
                return c;
        }

        return string.Empty;
    }
}
