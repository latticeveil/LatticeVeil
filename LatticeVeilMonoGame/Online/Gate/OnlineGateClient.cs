using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;

namespace LatticeVeilMonoGame.Online.Gate;

public sealed class OnlineGateClient
{
    private static readonly object Sync = new();
    private static readonly HttpClient Http = CreateHttpClient();
    private static OnlineGateClient? _instance;

    private readonly string _gateUrl;
    private readonly bool _gateRequired;
    private readonly string _proofPath;
    private readonly string _buildFlavor;

    private DateTime _ticketExpiresUtc = DateTime.MinValue;
    private string _ticket = string.Empty;
    private string _status = "UNVERIFIED";
    private string _denialReason = string.Empty;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private readonly TimeSpan _attemptCooldown = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private OnlineGateClient()
    {
        _gateUrl = (Environment.GetEnvironmentVariable("LV_GATE_URL") ?? string.Empty).Trim();
        _gateRequired = ParseBool(Environment.GetEnvironmentVariable("LV_GATE_REQUIRED"));
        _proofPath = ResolveProofPath();
        _buildFlavor = ResolveBuildFlavor(_proofPath);
    }

    public static OnlineGateClient GetOrCreate()
    {
        lock (Sync)
        {
            _instance ??= new OnlineGateClient();
            return _instance;
        }
    }

    public bool IsGateRequired => _gateRequired;
    public bool IsGateConfigured => !string.IsNullOrWhiteSpace(_gateUrl);
    public bool HasValidTicket => !string.IsNullOrWhiteSpace(_ticket) && DateTime.UtcNow < _ticketExpiresUtc;
    public string StatusText => _status;
    public string DenialReason => _denialReason;

    public bool CanUseOfficialOnline(Logger log, out string denialMessage)
    {
        denialMessage = string.Empty;

        if (HasValidTicket)
            return true;

        if (!IsGateConfigured)
        {
            if (IsGateRequired)
            {
                denialMessage = "Official online disabled (unverified build). LAN still available.";
                _status = "DENIED";
                _denialReason = "Gate URL missing.";
                return false;
            }

            _status = "BYPASS (NO GATE)";
            return true;
        }

        var ok = EnsureTicket(log);
        if (ok)
            return true;

        if (IsGateRequired)
        {
            denialMessage = "Official online disabled (unverified build). LAN still available.";
            return false;
        }

        // Optional gate mode in dev/community builds.
        return true;
    }

    public bool EnsureTicket(Logger log, TimeSpan? timeout = null)
    {
        if (HasValidTicket)
            return true;

        if (!IsGateConfigured)
            return !IsGateRequired;

        var now = DateTime.UtcNow;
        if (_lastAttemptUtc != DateTime.MinValue && now - _lastAttemptUtc < _attemptCooldown)
            return HasValidTicket || !IsGateRequired;

        _lastAttemptUtc = now;

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(6));
        try
        {
            return EnsureTicketAsync(log, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _status = "ERROR";
            _denialReason = ex.Message;
            log.Warn($"Online gate request failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> EnsureTicketAsync(Logger log, CancellationToken ct = default)
    {
        if (HasValidTicket)
            return true;

        if (!IsGateConfigured)
            return !IsGateRequired;

        var request = BuildTicketRequest();
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var endpoint = BuildTicketUrl(_gateUrl);
        using var response = await Http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _status = "DENIED";
            _denialReason = $"Gate HTTP {(int)response.StatusCode}";
            log.Warn($"Online gate denied with HTTP {(int)response.StatusCode}.");
            return false;
        }

        var parsed = JsonSerializer.Deserialize<GateTicketResponse>(body, JsonOptions);
        if (parsed == null)
        {
            _status = "DENIED";
            _denialReason = "Empty gate response.";
            return false;
        }

        if (!parsed.Ok)
        {
            _status = "DENIED";
            _denialReason = string.IsNullOrWhiteSpace(parsed.Reason) ? "Gate denied request." : parsed.Reason!;
            log.Warn($"Online gate denied: {_denialReason}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Ticket) || !DateTime.TryParse(parsed.ExpiresUtc, out var expiresUtc))
        {
            _status = "DENIED";
            _denialReason = "Gate response missing ticket metadata.";
            return false;
        }

        _ticket = parsed.Ticket.Trim();
        _ticketExpiresUtc = DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc);
        _status = "VERIFIED";
        _denialReason = string.Empty;
        log.Info($"Online gate ticket acquired; expires {_ticketExpiresUtc:u}.");
        return true;
    }

    private GateTicketRequest BuildTicketRequest()
    {
        var proofBytes = TryReadProof(_proofPath);
        var proofBase64 = proofBytes != null && proofBytes.Length > 0
            ? Convert.ToBase64String(proofBytes)
            : null;

        var exeHash = TryComputeExecutableHash();
        EosConfig.TryGetPublicIdentifiers(out var sandboxId, out var deploymentId);

        return new GateTicketRequest
        {
            GameVersion = GetGameVersion(),
            Platform = GetPlatform(),
            BuildFlavor = _buildFlavor,
            Proof = proofBase64,
            ExeHash = exeHash,
            PublicConfigIds = new PublicConfigIds
            {
                SandboxId = sandboxId,
                DeploymentId = deploymentId
            }
        };
    }

    private static string ResolveProofPath()
    {
        var path = (Environment.GetEnvironmentVariable("LV_OFFICIAL_PROOF_PATH") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(path))
            return path;
        return Path.Combine(AppContext.BaseDirectory, "official_build.sig");
    }

    private static string ResolveBuildFlavor(string proofPath)
    {
        var configured = (Environment.GetEnvironmentVariable("LV_BUILD_FLAVOR") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return File.Exists(proofPath) ? "official" : "community";
    }

    private static string GetGameVersion()
    {
        return typeof(OnlineGateClient).Assembly.GetName().Version?.ToString() ?? "dev";
    }

    private static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macos";
        return RuntimeInformation.OSDescription;
    }

    private static byte[]? TryReadProof(string proofPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(proofPath) || !File.Exists(proofPath))
                return null;
            return File.ReadAllBytes(proofPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryComputeExecutableHash()
    {
        try
        {
            var filePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTicketUrl(string gateUrl)
    {
        var baseUrl = gateUrl.TrimEnd('/');
        return $"{baseUrl}/ticket";
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        return trimmed == "1"
            || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private sealed class GateTicketRequest
    {
        public string GameVersion { get; set; } = "dev";
        public string Platform { get; set; } = "windows";
        public string BuildFlavor { get; set; } = "community";
        public string? Proof { get; set; }
        public string? ExeHash { get; set; }
        public PublicConfigIds PublicConfigIds { get; set; } = new();
    }

    private sealed class PublicConfigIds
    {
        public string SandboxId { get; set; } = string.Empty;
        public string DeploymentId { get; set; } = string.Empty;
    }

    private sealed class GateTicketResponse
    {
        public bool Ok { get; set; }
        public string? Ticket { get; set; }
        public string? ExpiresUtc { get; set; }
        public string? Reason { get; set; }
    }
}
