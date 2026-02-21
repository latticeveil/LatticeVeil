using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

internal sealed class OfficialBuildVerifier
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly Logger _log;
    private readonly string _endpoint;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(10);
    private readonly object _cacheSync = new();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private OfficialHashesSnapshot? _cachePayload;

    internal enum VerifyFailure
    {
        None,
        MissingFile,
        InvalidChannel,
        ServiceUnavailable,
        Unauthorized,
        BadResponse,
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

    private readonly struct OfficialHashesSnapshot
    {
        public string DevHash { get; init; }
        public string ReleaseHash { get; init; }
    }

    public OfficialBuildVerifier(Logger log, string endpoint)
    {
        _log = log;
        _endpoint = (endpoint ?? string.Empty).Trim();
    }

    private static string GetGameVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?.ToString() ?? "8.0.0";
        return version.StartsWith("v") ? version : $"v{version}";
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

        return await VerifyHashAsync(channel, localHash, ct).ConfigureAwait(false);
    }

    public async Task<VerifyResult> VerifyHashAsync(string channel, string localHash, CancellationToken ct = default)
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

        localHash = NormalizeHash(localHash);
        if (!IsSha256(localHash))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.ComputeFailed,
                Channel = channel,
                Message = "Computed local SHA256 is invalid."
            };
        }

        var fetch = await FetchOfficialHashesAsync(ct).ConfigureAwait(false);
        if (!fetch.Ok || fetch.Payload == null)
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = fetch.Failure,
                Channel = channel,
                ActualHash = localHash,
                Message = fetch.Message
            };
        }

        var targetHash = channel == "dev" ? fetch.Payload.Value.DevHash : fetch.Payload.Value.ReleaseHash;
        targetHash = NormalizeHash(targetHash);
        if (!IsSha256(targetHash))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.BadResponse,
                Channel = channel,
                ActualHash = localHash,
                Message = $"Official {channel} hash is missing or invalid."
            };
        }

        if (!string.Equals(localHash, targetHash, StringComparison.OrdinalIgnoreCase))
        {
            return new VerifyResult
            {
                Ok = false,
                Failure = VerifyFailure.HashMismatch,
                Channel = channel,
                Message = "This build is not official for online play. Please download the official release.",
                ExpectedHash = targetHash,
                ActualHash = localHash
            };
        }

        return new VerifyResult
        {
            Ok = true,
            Failure = VerifyFailure.None,
            Channel = channel,
            Message = "Official hash verified.",
            ExpectedHash = targetHash,
            ActualHash = localHash
        };
    }

    private async Task<(bool Ok, VerifyFailure Failure, string Message, OfficialHashesSnapshot? Payload)> FetchOfficialHashesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            return (false, VerifyFailure.BadResponse, "Official hash endpoint is not configured.", null);

        var now = DateTime.UtcNow;
        lock (_cacheSync)
        {
            if (_cachePayload.HasValue && now < _cacheExpiresUtc)
                return (true, VerifyFailure.None, "cache", _cachePayload);
        }

        try
        {
            // Determine target based on current build configuration
            var target = Paths.IsDevBuild ? "dev" : "release";
            var version = GetGameVersion();
            var endpointWithParams = $"{_endpoint}?target={target}&version={version}";
            _log?.Info($"Hash lookup: target={target}, version={version}");
            
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointWithParams);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => VerifyFailure.Unauthorized,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => VerifyFailure.ServiceUnavailable,
                    var code when (int)code >= 500 => VerifyFailure.ServiceUnavailable,
                    _ => VerifyFailure.BadResponse
                };
                var message = $"Official hash fetch failed (HTTP {(int)response.StatusCode}).";
                return (false, failure, message, null);
            }

            using var doc = JsonDocument.Parse(body);
            _log?.Info($"Supabase response: {body}");
            var snapshot = ExtractSnapshot(doc.RootElement);
            var targetHash = target == "dev" ? snapshot.DevHash : snapshot.ReleaseHash;
            _log?.Info($"Fetched {target} hash: {targetHash}");
            if (!IsSha256(targetHash))
            {
                return (false, VerifyFailure.BadResponse, "Official hash payload is missing dev/release hashes.", null);
            }

            lock (_cacheSync)
            {
                _cachePayload = snapshot;
                _cacheExpiresUtc = DateTime.UtcNow.Add(_cacheTtl);
            }

            return (true, VerifyFailure.None, "ok", snapshot);
        }
        catch (OperationCanceledException ex)
        {
            _log.Warn($"Official hash fetch canceled: {ex.Message}");
            return (false, VerifyFailure.ServiceUnavailable, "Official hash fetch timed out.", null);
        }
        catch (Exception ex)
        {
            _log.Warn($"Official hash fetch error: {ex.Message}");
            return (false, VerifyFailure.ServiceUnavailable, $"Official hash fetch error: {ex.Message}", null);
        }
    }

    private static OfficialHashesSnapshot ExtractSnapshot(JsonElement root)
    {
        var devHash = string.Empty;
        var releaseHash = string.Empty;

        if (root.ValueKind == JsonValueKind.Object)
        {
            devHash = ResolveHashFromNamedObject(root, "dev");
            releaseHash = ResolveHashFromNamedObject(root, "release");

            if (!IsSha256(devHash))
                devHash = ResolveHashFromValue(root, "devHash", "dev_hash", "devSha256", "dev_sha256");
            if (!IsSha256(releaseHash))
                releaseHash = ResolveHashFromValue(root, "releaseHash", "release_hash", "releaseSha256", "release_sha256");

            if ((!IsSha256(devHash) || !IsSha256(releaseHash))
                && TryGetRowCollection(root, out var rows))
            {
                (devHash, releaseHash) = ResolveHashesFromRows(rows, devHash, releaseHash);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            (devHash, releaseHash) = ResolveHashesFromRows(root, devHash, releaseHash);
        }

        return new OfficialHashesSnapshot
        {
            DevHash = NormalizeHash(devHash),
            ReleaseHash = NormalizeHash(releaseHash)
        };
    }

    private static bool TryGetRowCollection(JsonElement root, out JsonElement rows)
    {
        if (root.TryGetProperty("rows", out rows) && rows.ValueKind == JsonValueKind.Array)
            return true;
        if (root.TryGetProperty("data", out rows) && rows.ValueKind == JsonValueKind.Array)
            return true;
        if (root.TryGetProperty("hashes", out rows) && rows.ValueKind == JsonValueKind.Array)
            return true;

        rows = default;
        return false;
    }

    private static (string DevHash, string ReleaseHash) ResolveHashesFromRows(JsonElement rows, string existingDev, string existingRelease)
    {
        var devHash = existingDev;
        var releaseHash = existingRelease;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var target = ResolveString(row, "target", "channel", "name").ToLowerInvariant();
            if (target is not ("dev" or "release"))
                continue;

            var rowHash = ResolveHashFromValue(row, "hash_sha256", "sha256", "hash");
            if (!IsSha256(rowHash))
                continue;

            if (target == "dev")
                devHash = rowHash;
            else
                releaseHash = rowHash;
        }

        return (devHash, releaseHash);
    }

    private static string ResolveHashFromNamedObject(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return string.Empty;

        if (value.ValueKind == JsonValueKind.String)
            return NormalizeHash(value.GetString());

        if (value.ValueKind == JsonValueKind.Object)
            return ResolveHashFromValue(value, "hash_sha256", "sha256", "hash");

        return string.Empty;
    }

    private static string ResolveHashFromValue(JsonElement value, params string[] propertyNames)
    {
        for (var i = 0; i < propertyNames.Length; i++)
        {
            if (!value.TryGetProperty(propertyNames[i], out var prop))
                continue;

            if (prop.ValueKind != JsonValueKind.String)
                continue;

            var hash = NormalizeHash(prop.GetString());
            if (IsSha256(hash))
                return hash;
        }

        return string.Empty;
    }

    private static string ResolveString(JsonElement value, params string[] propertyNames)
    {
        for (var i = 0; i < propertyNames.Length; i++)
        {
            if (!value.TryGetProperty(propertyNames[i], out var prop))
                continue;
            if (prop.ValueKind != JsonValueKind.String)
                continue;

            return (prop.GetString() ?? string.Empty).Trim();
        }

        return string.Empty;
    }

    private static string NormalizeChannel(string channel)
    {
        var value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return value is "dev" or "release" ? value : string.Empty;
    }

    private static string NormalizeHash(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
                return false;
        }

        return true;
    }
}
