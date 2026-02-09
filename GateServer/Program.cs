using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AllowlistProvider>();

var app = builder.Build();

app.MapGet("/health", async (AllowlistProvider allowlistProvider, CancellationToken ct) =>
{
    var allowlist = await allowlistProvider.GetAsync(ct);
    return Results.Ok(new
    {
        ok = true,
        source = allowlist.Source,
        refreshedUtc = allowlist.RefreshedUtc,
        proofCount = allowlist.ProofTokens.Count,
        hashCount = allowlist.ExeSha256.Count
    });
});

app.MapPost("/ticket", async (TicketRequest request, AllowlistProvider allowlistProvider, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("GateTicket");
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(signingKey))
        return Results.Ok(TicketResponse.Denied("Gate is not configured."));

    var allowlist = await allowlistProvider.GetAsync(ct);
    if (!allowlist.IsVersionAllowed(request.GameVersion))
        return Results.Ok(TicketResponse.Denied($"Client version too old. Minimum: {allowlist.MinVersion}"));

    var proofToken = DecodeProofToken(request.Proof);
    var exeHash = NormalizeHash(request.ExeHash);

    var proofOk = !string.IsNullOrWhiteSpace(proofToken) && allowlist.ContainsProof(proofToken);
    var hashOk = !string.IsNullOrWhiteSpace(exeHash) && allowlist.ContainsHash(exeHash);
    if (!proofOk && !hashOk)
        return Results.Ok(TicketResponse.Denied("Build is not allowlisted."));

    var now = DateTimeOffset.UtcNow;
    var minutes = ParsePositiveInt(Environment.GetEnvironmentVariable("GATE_TICKET_MINUTES"), 30);
    var expires = now.AddMinutes(minutes);

    var issuer = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_ISSUER"), "latticeveil-gate");
    var audience = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_AUDIENCE"), "latticeveil-client");
    var claims = new Dictionary<string, object?>
    {
        ["iss"] = issuer,
        ["aud"] = audience,
        ["iat"] = now.ToUnixTimeSeconds(),
        ["exp"] = expires.ToUnixTimeSeconds(),
        ["build"] = "official",
        ["version"] = request.GameVersion,
        ["platform"] = request.Platform,
        ["flavor"] = request.BuildFlavor
    };

    var jwt = CreateHs256Jwt(claims, signingKey);
    log.LogInformation("Issued online ticket for version {Version} on {Platform}.", request.GameVersion, request.Platform);
    return Results.Ok(TicketResponse.Approved(jwt, expires.UtcDateTime));
});

app.Run();

static string NormalizeOrDefault(string? value, string fallback)
{
    var trimmed = (value ?? string.Empty).Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
}

static int ParsePositiveInt(string? value, int fallback)
{
    if (!int.TryParse(value, out var parsed))
        return fallback;
    return parsed > 0 ? parsed : fallback;
}

static string? DecodeProofToken(string? proofBase64)
{
    if (string.IsNullOrWhiteSpace(proofBase64))
        return null;

    try
    {
        var bytes = Convert.FromBase64String(proofBase64.Trim());
        return Encoding.UTF8.GetString(bytes).Trim();
    }
    catch
    {
        return null;
    }
}

static string? NormalizeHash(string? hash)
{
    if (string.IsNullOrWhiteSpace(hash))
        return null;
    return hash.Trim().ToLowerInvariant();
}

static string CreateHs256Jwt(Dictionary<string, object?> payload, string key)
{
    var header = new Dictionary<string, object?>
    {
        ["alg"] = "HS256",
        ["typ"] = "JWT"
    };

    var headerJson = JsonSerializer.Serialize(header);
    var payloadJson = JsonSerializer.Serialize(payload);
    var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
    var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
    var signingInput = $"{encodedHeader}.{encodedPayload}";

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
    var signature = Base64UrlEncode(signatureBytes);

    return $"{signingInput}.{signature}";
}

static string Base64UrlEncode(byte[] bytes)
{
    return Convert.ToBase64String(bytes)
        .Replace("+", "-")
        .Replace("/", "_")
        .TrimEnd('=');
}

sealed class AllowlistProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private AllowlistSnapshot _snapshot = AllowlistSnapshot.Empty("startup");

    private readonly string _githubRepo;
    private readonly string _githubPath;
    private readonly string _githubBranch;
    private readonly string _githubToken;
    private readonly string _localFile;

    public AllowlistProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _githubRepo = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_REPO") ?? string.Empty).Trim();
        _githubPath = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_PATH") ?? "allowlist.json").Trim();
        _githubBranch = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_BRANCH") ?? "main").Trim();
        _githubToken = (Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty).Trim();
        _localFile = (Environment.GetEnvironmentVariable("ALLOWLIST_FILE") ?? string.Empty).Trim();
    }

    public async Task<AllowlistSnapshot> GetAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _snapshot.RefreshedUtc < _refreshInterval)
            return _snapshot;

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (now - _snapshot.RefreshedUtc < _refreshInterval)
                return _snapshot;

            var loaded = await TryLoadFromLocalFileAsync(ct)
                ?? await TryLoadFromGithubAsync(ct)
                ?? AllowlistSnapshot.Empty("none");
            _snapshot = loaded with { RefreshedUtc = DateTime.UtcNow };
            return _snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AllowlistSnapshot?> TryLoadFromLocalFileAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_localFile) || !File.Exists(_localFile))
            return null;

        var json = await File.ReadAllTextAsync(_localFile, ct);
        var model = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
        if (model == null)
            return null;
        return AllowlistSnapshot.FromModel(model, $"file:{_localFile}");
    }

    private async Task<AllowlistSnapshot?> TryLoadFromGithubAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_githubRepo) || string.IsNullOrWhiteSpace(_githubToken))
            return null;

        var encodedPath = Uri.EscapeDataString(_githubPath);
        var endpoint = $"https://api.github.com/repos/{_githubRepo}/contents/{encodedPath}?ref={_githubBranch}";

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("User-Agent", "LatticeVeil-GateServer");
        request.Headers.Add("Authorization", $"Bearer {_githubToken}");

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        var github = JsonSerializer.Deserialize<GithubContentResponse>(body, JsonOptions);
        if (github == null || string.IsNullOrWhiteSpace(github.Content))
            return null;

        var base64 = github.Content.Replace("\n", string.Empty).Replace("\r", string.Empty);
        var raw = Convert.FromBase64String(base64);
        var json = Encoding.UTF8.GetString(raw);
        var model = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
        if (model == null)
            return null;
        return AllowlistSnapshot.FromModel(model, $"github:{_githubRepo}/{_githubPath}");
    }

    private sealed class GithubContentResponse
    {
        public string Content { get; set; } = string.Empty;
    }
}

sealed class TicketRequest
{
    public string GameVersion { get; set; } = "dev";
    public string Platform { get; set; } = "windows";
    public string BuildFlavor { get; set; } = "community";
    public string? Proof { get; set; }
    public string? ExeHash { get; set; }
    public Dictionary<string, string>? PublicConfigIds { get; set; }
}

sealed class TicketResponse
{
    public bool Ok { get; set; }
    public string? Ticket { get; set; }
    public string? ExpiresUtc { get; set; }
    public string? Reason { get; set; }

    public static TicketResponse Approved(string ticket, DateTime expiresUtc) => new()
    {
        Ok = true,
        Ticket = ticket,
        ExpiresUtc = expiresUtc.ToString("O"),
        Reason = "approved"
    };

    public static TicketResponse Denied(string reason) => new()
    {
        Ok = false,
        Reason = reason
    };
}

sealed class AllowlistModel
{
    public List<string> ProofTokens { get; set; } = new();
    public List<string> ExeSha256 { get; set; } = new();
    public string MinVersion { get; set; } = "0.0.0";
}

sealed record AllowlistSnapshot(
    HashSet<string> ProofTokens,
    HashSet<string> ExeSha256,
    string MinVersion,
    string Source,
    DateTime RefreshedUtc)
{
    public static AllowlistSnapshot Empty(string source) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "0.0.0", source, DateTime.MinValue);

    public static AllowlistSnapshot FromModel(AllowlistModel model, string source)
    {
        return new AllowlistSnapshot(
            new HashSet<string>(model.ProofTokens?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal),
            new HashSet<string>(model.ExeSha256?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(model.MinVersion) ? "0.0.0" : model.MinVersion.Trim(),
            source,
            DateTime.UtcNow);
    }

    public bool ContainsProof(string token) => ProofTokens.Contains(token.Trim());
    public bool ContainsHash(string hash) => ExeSha256.Contains(hash.Trim().ToLowerInvariant());

    public bool IsVersionAllowed(string version)
    {
        if (!Version.TryParse(NormalizeVersion(version), out var client))
            return true;
        if (!Version.TryParse(NormalizeVersion(MinVersion), out var min))
            return true;
        return client >= min;
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "0.0.0";
        if (Version.TryParse(trimmed, out _))
            return trimmed;

        var parts = trimmed.Split('.');
        if (parts.Length == 1)
            return $"{parts[0]}.0.0";
        if (parts.Length == 2)
            return $"{parts[0]}.{parts[1]}.0";
        return $"{parts[0]}.{parts[1]}.{parts[2]}";
    }
}
