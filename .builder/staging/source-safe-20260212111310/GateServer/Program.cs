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
    var runtime = allowlistProvider.GetRuntimeView();
    return Results.Ok(new
    {
        ok = true,
        source = allowlist.Source,
        refreshedUtc = allowlist.RefreshedUtc,
        allowlistAvailable = allowlist.IsAvailable,
        failureReason = allowlist.FailureReason,
        proofCount = allowlist.ProofTokens.Count,
        hashCount = allowlist.ExeSha256.Count,
        runtimeOverrideEnabled = runtime.Enabled,
        runtimeApplyMode = runtime.ApplyMode,
        runtimeHashCount = runtime.HashCount,
        runtimeUpdatedUtc = runtime.UpdatedUtc
    });
});

app.MapGet("/eos/config/gate", (HttpRequest request, HttpResponse response) =>
{
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(signingKey))
        return Results.Problem("Gate is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

    var ticket = ExtractBearerToken(request);
    if (string.IsNullOrWhiteSpace(ticket))
        return Results.Unauthorized();

    var issuer = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_ISSUER"), "latticeveil-gate");
    var audience = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_AUDIENCE"), "latticeveil-client");
    if (!TryValidateHs256Jwt(ticket, signingKey, issuer, audience, out _, out _))
        return Results.Unauthorized();

    if (!TryLoadEosConfigPayloadFromEnvironment(out var payload, out var error))
        return Results.Problem(error ?? "EOS config is missing required values.", statusCode: StatusCodes.Status503ServiceUnavailable);

    response.Headers.CacheControl = "no-store";
    response.Headers.Pragma = "no-cache";
    return Results.Json(payload);
});

app.MapPost("/ticket", async (TicketRequest request, AllowlistProvider allowlistProvider, ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("GateTicket");
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(signingKey))
        return Results.Ok(TicketResponse.Denied("Gate is not configured."));

    var allowlist = await allowlistProvider.GetAsync(ct);
    if (!allowlist.IsAvailable)
        return Results.Ok(TicketResponse.Denied($"Allowlist unavailable: {allowlist.FailureReason}"));

    var proofToken = DecodeProofToken(request.Proof);
    var exeHash = NormalizeHash(request.ExeHash);
    var devKey = (request.DevKey ?? string.Empty).Trim();
    var expectedDevKey = (Environment.GetEnvironmentVariable("GATE_DEV_KEY") ?? string.Empty).Trim();
    var isDevFlavor = string.Equals(request.BuildFlavor, "dev", StringComparison.OrdinalIgnoreCase);
    var devKeyOk = isDevFlavor
        && !string.IsNullOrWhiteSpace(expectedDevKey)
        && FixedTimeEquals(devKey, expectedDevKey);

    var proofOk = !string.IsNullOrWhiteSpace(proofToken) && allowlist.ContainsProof(proofToken);
    var hashOk = !string.IsNullOrWhiteSpace(exeHash) && allowlist.ContainsHash(exeHash);
    var verificationMode = (Environment.GetEnvironmentVariable("GATE_VERIFICATION_MODE") ?? "hash_or_proof").Trim();
    var allowlistedByPolicy = IsAllowlistedByPolicy(verificationMode, hashOk, proofOk);
    if (!allowlistedByPolicy && !devKeyOk)
        return Results.Ok(TicketResponse.Denied("Build is not allowlisted."));

    var expectedSandboxId = (Environment.GetEnvironmentVariable("GATE_EXPECTED_SANDBOX_ID") ?? string.Empty).Trim();
    var expectedDeploymentId = (Environment.GetEnvironmentVariable("GATE_EXPECTED_DEPLOYMENT_ID") ?? string.Empty).Trim();
    var publicIdPolicy = ResolvePublicIdPolicy(Environment.GetEnvironmentVariable("GATE_PUBLIC_ID_POLICY"));
    if (!devKeyOk && !MatchesExpectedPublicIds(request.PublicConfigIds, expectedSandboxId, expectedDeploymentId, publicIdPolicy))
        return Results.Ok(TicketResponse.Denied("EOS public IDs are not authorized for this gate."));

    if (!allowlist.IsVersionAllowed(request.GameVersion) && !devKeyOk)
        return Results.Ok(TicketResponse.Denied($"Client version too old. Minimum: {allowlist.MinVersion}"));

    var now = DateTimeOffset.UtcNow;
    var minutes = ParsePositiveInt(Environment.GetEnvironmentVariable("GATE_TICKET_MINUTES"), 30);
    var expires = now.AddMinutes(minutes);

    var issuer = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_ISSUER"), "latticeveil-gate");
    var audience = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_AUDIENCE"), "latticeveil-client");
    var ticketChannel = ResolveTicketChannel(request.BuildFlavor, devKeyOk);
    var claims = new Dictionary<string, object?>
    {
        ["iss"] = issuer,
        ["aud"] = audience,
        ["iat"] = now.ToUnixTimeSeconds(),
        ["exp"] = expires.ToUnixTimeSeconds(),
        ["build"] = devKeyOk ? "dev" : "official",
        ["channel"] = ticketChannel,
        ["version"] = request.GameVersion,
        ["platform"] = request.Platform,
        ["flavor"] = request.BuildFlavor
    };

    var jwt = CreateHs256Jwt(claims, signingKey);
    log.LogInformation("Issued online ticket for version {Version} on {Platform}.", request.GameVersion, request.Platform);
    return Results.Ok(TicketResponse.Approved(jwt, expires.UtcDateTime));
});

app.MapPost("/ticket/validate", (TicketValidateRequest request, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("GateTicketValidate");
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(signingKey))
        return Results.Ok(TicketValidateResponse.Denied("Gate is not configured."));

    var ticket = (request.Ticket ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(ticket))
        return Results.Ok(TicketValidateResponse.Denied("Ticket missing."));

    var issuer = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_ISSUER"), "latticeveil-gate");
    var audience = NormalizeOrDefault(Environment.GetEnvironmentVariable("GATE_AUDIENCE"), "latticeveil-client");

    if (!TryValidateHs256Jwt(ticket, signingKey, issuer, audience, out var claims, out var validationError))
    {
        log.LogWarning("Ticket validation failed: {Reason}", validationError);
        return Results.Ok(TicketValidateResponse.Denied(validationError));
    }

    var expiresUtc = TryGetUnixClaimUtc(claims, "exp");
    var channel = ResolveTicketChannelFromClaims(claims);
    var requiredChannel = NormalizeTicketChannel(request.RequiredChannel);
    if (!string.IsNullOrWhiteSpace(requiredChannel)
        && !string.Equals(channel, requiredChannel, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(TicketValidateResponse.Denied(
            $"Ticket channel '{channel}' does not match required '{requiredChannel}'.",
            channel,
            expiresUtc));
    }

    return Results.Ok(TicketValidateResponse.Approved(channel, expiresUtc));
});

app.MapGet("/admin/allowlist/runtime", (HttpContext httpContext, AllowlistProvider allowlistProvider) =>
{
    if (!TryAuthorizeAdmin(httpContext, out var failure))
        return failure!;

    var view = allowlistProvider.GetRuntimeView();
    return Results.Ok(new
    {
        ok = true,
        runtime = view
    });
});

app.MapPost("/admin/allowlist/runtime", (HttpContext httpContext, RuntimeAllowlistUpdateRequest request, AllowlistProvider allowlistProvider) =>
{
    if (!TryAuthorizeAdmin(httpContext, out var failure))
        return failure!;

    var normalizedOperation = (request.Operation ?? "replace").Trim();
    var model = new AllowlistModel
    {
        ProofTokens = request.ProofTokens ?? new List<string>(),
        AllowedClientExeSha256 = request.AllowedClientExeSha256 ?? new List<string>(),
        AllowedDevExeSha256 = request.AllowedDevExeSha256 ?? new List<string>(),
        AllowedReleaseExeSha256 = request.AllowedReleaseExeSha256 ?? new List<string>(),
        ExeSha256 = request.ExeSha256 ?? new List<string>(),
        MinVersion = request.MinVersion ?? "0.0.0"
    };

    var result = allowlistProvider.UpdateRuntimeAllowlist(normalizedOperation, request.ApplyMode, model);
    if (!result.Ok)
    {
        return Results.BadRequest(new
        {
            ok = false,
            reason = result.Message,
            runtime = result.View
        });
    }

    return Results.Ok(new
    {
        ok = true,
        message = result.Message,
        runtime = result.View
    });
});

app.MapPost("/admin/allowlist/runtime/current-hash", (HttpContext httpContext, RuntimeCurrentHashRequest request, AllowlistProvider allowlistProvider) =>
{
    if (!TryAuthorizeAdmin(httpContext, out var failure))
        return failure!;

    var result = allowlistProvider.SetRuntimeCurrentHash(
        request.Hash,
        request.Target,
        request.ReplaceTargetList,
        request.ClearOtherHashes,
        request.ApplyMode);
    if (!result.Ok)
    {
        return Results.BadRequest(new
        {
            ok = false,
            reason = result.Message,
            runtime = result.View
        });
    }

    return Results.Ok(new
    {
        ok = true,
        message = result.Message,
        runtime = result.View
    });
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

static bool TryLoadEosConfigPayloadFromEnvironment(out EosConfigPayload payload, out string? error)
{
    payload = new EosConfigPayload(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        "LatticeVeil",
        "1.0.0",
        "deviceid");
    error = null;

    var productId = (Environment.GetEnvironmentVariable("EOS_PRODUCT_ID") ?? string.Empty).Trim();
    var sandboxId = (Environment.GetEnvironmentVariable("EOS_SANDBOX_ID") ?? string.Empty).Trim();
    var deploymentId = (Environment.GetEnvironmentVariable("EOS_DEPLOYMENT_ID") ?? string.Empty).Trim();
    var clientId = (Environment.GetEnvironmentVariable("EOS_CLIENT_ID") ?? string.Empty).Trim();
    var clientSecret = (Environment.GetEnvironmentVariable("EOS_CLIENT_SECRET") ?? string.Empty).Trim();
    var productName = (Environment.GetEnvironmentVariable("EOS_PRODUCT_NAME") ?? "LatticeVeil").Trim();
    var productVersion = (Environment.GetEnvironmentVariable("EOS_PRODUCT_VERSION") ?? "1.0.0").Trim();
    var loginMode = (Environment.GetEnvironmentVariable("EOS_LOGIN_MODE") ?? "deviceid").Trim();

    if (string.IsNullOrWhiteSpace(productId))
    {
        error = "EOS_PRODUCT_ID is required.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(sandboxId))
    {
        error = "EOS_SANDBOX_ID is required.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(deploymentId))
    {
        error = "EOS_DEPLOYMENT_ID is required.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(clientId))
    {
        error = "EOS_CLIENT_ID is required.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(clientSecret))
    {
        error = "EOS_CLIENT_SECRET is required.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(productName))
        productName = "LatticeVeil";
    if (string.IsNullOrWhiteSpace(productVersion))
        productVersion = "1.0.0";
    if (string.IsNullOrWhiteSpace(loginMode))
        loginMode = "deviceid";

    payload = new EosConfigPayload(
        productId,
        sandboxId,
        deploymentId,
        clientId,
        clientSecret,
        productName,
        productVersion,
        loginMode);
    return true;
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

static bool FixedTimeEquals(string left, string right)
{
    if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        return false;

    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static bool IsAllowlistedByPolicy(string mode, bool hashOk, bool proofOk)
{
    var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "hash_only" => hashOk,
        "hash_and_proof" => hashOk && proofOk,
        "proof_only" => proofOk,
        _ => hashOk || proofOk
    };
}

static string ResolveTicketChannel(string? buildFlavor, bool devKeyOk)
{
    if (devKeyOk)
        return "dev";

    var normalizedFlavor = NormalizeTicketChannel(buildFlavor);
    return normalizedFlavor == "dev" ? "dev" : "release";
}

static string ResolveTicketChannelFromClaims(Dictionary<string, JsonElement> claims)
{
    if (TryGetStringClaim(claims, "channel", out var explicitChannel))
    {
        var normalized = NormalizeTicketChannel(explicitChannel);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;
    }

    if (TryGetStringClaim(claims, "build", out var build) && build.Equals("dev", StringComparison.OrdinalIgnoreCase))
        return "dev";

    if (TryGetStringClaim(claims, "flavor", out var flavor))
    {
        var normalized = NormalizeTicketChannel(flavor);
        if (normalized == "dev")
            return "dev";
    }

    return "release";
}

static string NormalizeTicketChannel(string? value)
{
    var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "dev" => "dev",
        "debug" => "dev",
        "release" => "release",
        "official" => "release",
        "community" => "release",
        _ => normalized
    };
}

static bool TryValidateHs256Jwt(
    string jwt,
    string signingKey,
    string expectedIssuer,
    string expectedAudience,
    out Dictionary<string, JsonElement> claims,
    out string reason)
{
    claims = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    reason = string.Empty;

    var parts = jwt.Split('.');
    if (parts.Length != 3)
    {
        reason = "Malformed JWT.";
        return false;
    }

    var encodedHeader = parts[0];
    var encodedPayload = parts[1];
    var encodedSignature = parts[2];
    var signingInput = $"{encodedHeader}.{encodedPayload}";

    var headerBytes = TryBase64UrlDecode(encodedHeader);
    var payloadBytes = TryBase64UrlDecode(encodedPayload);
    var signatureBytes = TryBase64UrlDecode(encodedSignature);
    if (headerBytes == null || payloadBytes == null || signatureBytes == null)
    {
        reason = "JWT decode failed.";
        return false;
    }

    Dictionary<string, JsonElement>? header;
    Dictionary<string, JsonElement>? payload;
    try
    {
        header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerBytes);
        payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadBytes);
    }
    catch
    {
        reason = "JWT JSON parse failed.";
        return false;
    }

    if (header == null || payload == null)
    {
        reason = "JWT payload missing.";
        return false;
    }

    if (!TryGetStringClaim(header, "alg", out var alg) || !alg.Equals("HS256", StringComparison.OrdinalIgnoreCase))
    {
        reason = "JWT algorithm not supported.";
        return false;
    }

    using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey)))
    {
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            reason = "JWT signature invalid.";
            return false;
        }
    }

    if (!TryGetStringClaim(payload, "iss", out var issuer)
        || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
    {
        reason = "JWT issuer invalid.";
        return false;
    }

    if (!ClaimHasAudience(payload, expectedAudience))
    {
        reason = "JWT audience invalid.";
        return false;
    }

    var exp = TryGetUnixClaimUtc(payload, "exp");
    if (exp == null)
    {
        reason = "JWT expiration missing.";
        return false;
    }

    if (exp.Value <= DateTime.UtcNow)
    {
        reason = "JWT expired.";
        return false;
    }

    claims = payload;
    return true;
}

static bool ClaimHasAudience(Dictionary<string, JsonElement> payload, string expectedAudience)
{
    if (!payload.TryGetValue("aud", out var audElement))
        return false;

    if (audElement.ValueKind == JsonValueKind.String)
    {
        var value = audElement.GetString() ?? string.Empty;
        return string.Equals(value.Trim(), expectedAudience, StringComparison.Ordinal);
    }

    if (audElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in audElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString() ?? string.Empty;
            if (string.Equals(value.Trim(), expectedAudience, StringComparison.Ordinal))
                return true;
        }
    }

    return false;
}

static DateTime? TryGetUnixClaimUtc(Dictionary<string, JsonElement> claims, string key)
{
    if (!claims.TryGetValue(key, out var claim))
        return null;

    if (claim.ValueKind == JsonValueKind.Number && claim.TryGetInt64(out var numberValue))
        return DateTimeOffset.FromUnixTimeSeconds(numberValue).UtcDateTime;

    if (claim.ValueKind == JsonValueKind.String
        && long.TryParse(claim.GetString(), out var stringValue))
    {
        return DateTimeOffset.FromUnixTimeSeconds(stringValue).UtcDateTime;
    }

    return null;
}

static bool TryGetStringClaim(Dictionary<string, JsonElement> claims, string key, out string value)
{
    value = string.Empty;
    if (!claims.TryGetValue(key, out var claim))
        return false;

    if (claim.ValueKind != JsonValueKind.String)
        return false;

    value = (claim.GetString() ?? string.Empty).Trim();
    return !string.IsNullOrWhiteSpace(value);
}

static byte[]? TryBase64UrlDecode(string value)
{
    var normalized = value.Replace('-', '+').Replace('_', '/');
    var pad = normalized.Length % 4;
    if (pad == 2)
        normalized += "==";
    else if (pad == 3)
        normalized += "=";
    else if (pad == 1)
        return null;

    try
    {
        return Convert.FromBase64String(normalized);
    }
    catch
    {
        return null;
    }
}

static bool MatchesExpectedPublicIds(
    Dictionary<string, string>? publicConfigIds,
    string expectedSandboxId,
    string expectedDeploymentId,
    PublicIdPolicy policy)
{
    if (policy == PublicIdPolicy.Off)
        return true;

    if (string.IsNullOrWhiteSpace(expectedSandboxId) && string.IsNullOrWhiteSpace(expectedDeploymentId))
        return true;

    if (publicConfigIds == null || publicConfigIds.Count == 0)
        return policy == PublicIdPolicy.IfPresent;

    var actualSandboxId = GetCaseInsensitiveValue(publicConfigIds, "SandboxId");
    var actualDeploymentId = GetCaseInsensitiveValue(publicConfigIds, "DeploymentId");
    var hasProvidedIds = !string.IsNullOrWhiteSpace(actualSandboxId) || !string.IsNullOrWhiteSpace(actualDeploymentId);
    if (!hasProvidedIds && policy == PublicIdPolicy.IfPresent)
        return true;

    if (!string.IsNullOrWhiteSpace(expectedSandboxId)
        && !string.Equals(actualSandboxId?.Trim(), expectedSandboxId, StringComparison.Ordinal))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(expectedDeploymentId)
        && !string.Equals(actualDeploymentId?.Trim(), expectedDeploymentId, StringComparison.Ordinal))
    {
        return false;
    }

    return true;
}

static PublicIdPolicy ResolvePublicIdPolicy(string? value)
{
    var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "required" or "strict" => PublicIdPolicy.Required,
        "off" or "disabled" or "false" or "0" => PublicIdPolicy.Off,
        _ => PublicIdPolicy.IfPresent
    };
}

static string? GetCaseInsensitiveValue(Dictionary<string, string> values, string key)
{
    if (values.TryGetValue(key, out var exact))
        return exact;

    foreach (var entry in values)
    {
        if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            return entry.Value;
    }

    return null;
}

static bool TryAuthorizeAdmin(HttpContext httpContext, out IResult? failure)
{
    failure = null;
    var expected = (Environment.GetEnvironmentVariable("GATE_ADMIN_TOKEN") ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(expected))
    {
        failure = Results.Problem(
            title: "Admin API disabled",
            detail: "Set GATE_ADMIN_TOKEN to enable admin endpoints.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
        return false;
    }

    var provided = ExtractAdminToken(httpContext);
    if (string.IsNullOrWhiteSpace(provided) || !FixedTimeEquals(provided.Trim(), expected))
    {
        failure = Results.Unauthorized();
        return false;
    }

    return true;
}

static string ExtractAdminToken(HttpContext httpContext)
{
    var authHeader = (httpContext.Request.Headers["Authorization"].ToString() ?? string.Empty).Trim();
    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return authHeader["Bearer ".Length..].Trim();

    var headerToken = (httpContext.Request.Headers["X-Gate-Admin-Token"].ToString() ?? string.Empty).Trim();
    return headerToken;
}

static string ExtractBearerToken(HttpRequest request)
{
    var authHeader = (request.Headers["Authorization"].ToString() ?? string.Empty).Trim();
    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return authHeader["Bearer ".Length..].Trim();

    return string.Empty;
}

enum PublicIdPolicy
{
    IfPresent,
    Required,
    Off
}

sealed class AllowlistProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _refreshInterval;
    private AllowlistSnapshot _snapshot = AllowlistSnapshot.Empty("startup");

    private readonly string _githubRepo;
    private readonly string _githubPath;
    private readonly string _githubBranch;
    private readonly string _githubToken;
    private readonly string _allowlistSource;
    private readonly string _allowlistJsonPath;
    private readonly string _allowlistJsonBase64;
    private readonly string _allowlistInlineJson;
    private readonly string _envProofTokens;
    private readonly string _envAllowedClientHashes;
    private readonly string _envAllowedDevHashes;
    private readonly string _envAllowedReleaseHashes;
    private readonly string _envLegacyHashes;
    private readonly string _envMinVersion;
    private readonly object _runtimeSync = new();
    private AllowlistModel? _runtimeModel;
    private RuntimeApplyMode _runtimeApplyMode = RuntimeApplyMode.ReplaceSource;
    private DateTime _runtimeUpdatedUtc = DateTime.MinValue;

    public AllowlistProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _githubRepo = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_REPO") ?? string.Empty).Trim();
        _githubPath = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_PATH") ?? "allowlist.json").Trim();
        _githubBranch = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_BRANCH") ?? "main").Trim();
        _githubToken = (Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty).Trim();
        _allowlistSource = (Environment.GetEnvironmentVariable("ALLOWLIST_SOURCE") ?? "env").Trim();
        _allowlistJsonPath = (Environment.GetEnvironmentVariable("ALLOWLIST_JSON_PATH") ?? string.Empty).Trim();
        _allowlistJsonBase64 = (Environment.GetEnvironmentVariable("ALLOWLIST_JSON_BASE64") ?? string.Empty).Trim();
        _allowlistInlineJson = (Environment.GetEnvironmentVariable("ALLOWLIST_INLINE_JSON") ?? string.Empty).Trim();
        _envProofTokens = (Environment.GetEnvironmentVariable("ALLOWLIST_PROOF_TOKENS") ?? string.Empty).Trim();
        _envAllowedClientHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_CLIENT_EXE_SHA256") ?? string.Empty).Trim();
        _envAllowedDevHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_DEV_EXE_SHA256") ?? string.Empty).Trim();
        _envAllowedReleaseHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_RELEASE_EXE_SHA256") ?? string.Empty).Trim();
        _envLegacyHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_EXE_SHA256") ?? string.Empty).Trim();
        _envMinVersion = (Environment.GetEnvironmentVariable("ALLOWLIST_MIN_VERSION") ?? string.Empty).Trim();
        var refreshSeconds = ParseRefreshSeconds(Environment.GetEnvironmentVariable("ALLOWLIST_REFRESH_SECONDS"));
        _refreshInterval = TimeSpan.FromSeconds(refreshSeconds);
    }

    private static int ParseRefreshSeconds(string? value)
    {
        if (!int.TryParse(value, out var parsed))
            return 30;

        if (parsed < 5)
            return 5;

        return parsed;
    }

    public RuntimeAllowlistView GetRuntimeView()
    {
        lock (_runtimeSync)
        {
            return BuildRuntimeViewUnsafe();
        }
    }

    public RuntimeAllowlistAdminResult UpdateRuntimeAllowlist(string operation, string? applyMode, AllowlistModel model)
    {
        var normalizedOperation = (operation ?? "replace").Trim().ToLowerInvariant();
        var parsedMode = ParseRuntimeApplyMode(applyMode);

        lock (_runtimeSync)
        {
            if (normalizedOperation == "clear")
            {
                _runtimeModel = null;
                _runtimeApplyMode = parsedMode;
                _runtimeUpdatedUtc = DateTime.MinValue;
                return new RuntimeAllowlistAdminResult(true, "Runtime allowlist cleared.", BuildRuntimeViewUnsafe());
            }

            if (!TryNormalizeAllowlistModel(model, out var normalizedModel, out var error))
            {
                return new RuntimeAllowlistAdminResult(false, error, BuildRuntimeViewUnsafe());
            }

            if (normalizedOperation == "replace")
            {
                _runtimeModel = normalizedModel;
                _runtimeApplyMode = parsedMode;
                _runtimeUpdatedUtc = DateTime.UtcNow;
                return new RuntimeAllowlistAdminResult(true, "Runtime allowlist replaced.", BuildRuntimeViewUnsafe());
            }

            if (normalizedOperation == "merge")
            {
                _runtimeModel ??= new AllowlistModel();
                MergeModelInto(_runtimeModel, normalizedModel);
                _runtimeApplyMode = parsedMode;
                _runtimeUpdatedUtc = DateTime.UtcNow;
                return new RuntimeAllowlistAdminResult(true, "Runtime allowlist merged.", BuildRuntimeViewUnsafe());
            }

            return new RuntimeAllowlistAdminResult(
                false,
                $"Unsupported operation '{operation}'. Use replace, merge, or clear.",
                BuildRuntimeViewUnsafe());
        }
    }

    public RuntimeAllowlistAdminResult SetRuntimeCurrentHash(
        string? hash,
        string? target,
        bool replaceTargetList,
        bool clearOtherHashes,
        string? applyMode)
    {
        var normalizedHash = NormalizeHashValue(hash);
        if (string.IsNullOrWhiteSpace(normalizedHash) || !IsSha256(normalizedHash))
        {
            return new RuntimeAllowlistAdminResult(
                false,
                "Hash must be a valid 64-character SHA256 hex string.",
                GetRuntimeView());
        }

        var parsedMode = ParseRuntimeApplyMode(applyMode);

        lock (_runtimeSync)
        {
            _runtimeModel ??= new AllowlistModel();

            if (clearOtherHashes)
            {
                _runtimeModel.AllowedClientExeSha256.Clear();
                _runtimeModel.AllowedDevExeSha256.Clear();
                _runtimeModel.AllowedReleaseExeSha256.Clear();
                _runtimeModel.ExeSha256.Clear();
            }

            var targetList = ResolveHashTargetList(_runtimeModel, target, out var targetLabel);
            if (targetList == null)
            {
                return new RuntimeAllowlistAdminResult(
                    false,
                    "Target must be one of: client, dev, release.",
                    BuildRuntimeViewUnsafe());
            }

            if (replaceTargetList)
                targetList.Clear();

            if (!targetList.Contains(normalizedHash, StringComparer.OrdinalIgnoreCase))
                targetList.Add(normalizedHash);

            _runtimeApplyMode = parsedMode;
            _runtimeUpdatedUtc = DateTime.UtcNow;
            var updatedView = BuildRuntimeViewUnsafe();
            return new RuntimeAllowlistAdminResult(
                true,
                $"Runtime hash updated for {targetLabel}.",
                updatedView);
        }
    }

    public async Task<AllowlistSnapshot> GetAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _snapshot.RefreshedUtc < _refreshInterval)
            return ApplyRuntimeOverride(_snapshot);

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (now - _snapshot.RefreshedUtc < _refreshInterval)
                return ApplyRuntimeOverride(_snapshot);

            var loaded = await LoadAllowlistWithPolicyAsync(ct);
            _snapshot = loaded;

            _snapshot = _snapshot with { RefreshedUtc = DateTime.UtcNow };
            return ApplyRuntimeOverride(_snapshot);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AllowlistSnapshot> LoadAllowlistWithPolicyAsync(CancellationToken ct)
    {
        var sourceMode = ParseAllowlistSource(_allowlistSource);
        var envSnapshot = TryLoadFromEnvironment();
        AllowlistSnapshot? githubSnapshot = null;
        if (sourceMode != AllowlistSourceMode.EnvironmentOnly)
            githubSnapshot = await TryLoadFromGithubAsync(ct);

        if (sourceMode == AllowlistSourceMode.EnvironmentOnly)
        {
            if (envSnapshot != null && envSnapshot.IsAvailable)
                return envSnapshot;

            var envReason = envSnapshot?.FailureReason ?? "Environment allowlist is not configured.";
            return AllowlistSnapshot.Unavailable("env", envReason);
        }

        if (sourceMode == AllowlistSourceMode.Hybrid)
        {
            if (githubSnapshot != null && githubSnapshot.IsAvailable)
                return MergeEnvironmentOverrides(githubSnapshot, envSnapshot);

            if (envSnapshot != null && envSnapshot.IsAvailable)
                return envSnapshot;

            var combinedReason = BuildCombinedFailureReason(githubSnapshot, envSnapshot);
            return AllowlistSnapshot.Unavailable("hybrid", combinedReason);
        }

        // Default: env
        if (githubSnapshot != null && githubSnapshot.IsAvailable)
            return MergeEnvironmentOverrides(githubSnapshot, envSnapshot);

        if (envSnapshot != null && envSnapshot.IsAvailable)
            return envSnapshot with { Source = $"{envSnapshot.Source} (github fallback)" };

        var reason = BuildCombinedFailureReason(githubSnapshot, envSnapshot);
        return AllowlistSnapshot.Unavailable("env", reason);
    }

    private static string BuildCombinedFailureReason(AllowlistSnapshot? githubSnapshot, AllowlistSnapshot? envSnapshot)
    {
        var githubReason = githubSnapshot?.FailureReason;
        var envReason = envSnapshot?.FailureReason;
        if (!string.IsNullOrWhiteSpace(githubReason) && !string.IsNullOrWhiteSpace(envReason))
            return $"GitHub: {githubReason} | Env: {envReason}";
        if (!string.IsNullOrWhiteSpace(githubReason))
            return githubReason;
        if (!string.IsNullOrWhiteSpace(envReason))
            return envReason;
        return "No allowlist sources are configured.";
    }

    private static AllowlistSourceMode ParseAllowlistSource(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "env" or "environment" or "inline" => AllowlistSourceMode.EnvironmentOnly,
            "hybrid" => AllowlistSourceMode.Hybrid,
            _ => AllowlistSourceMode.EnvironmentOnly
        };
    }

    private AllowlistSnapshot MergeEnvironmentOverrides(AllowlistSnapshot baseSnapshot, AllowlistSnapshot? envSnapshot)
    {
        if (envSnapshot == null || !envSnapshot.IsAvailable)
            return baseSnapshot;

        return MergeSnapshots(baseSnapshot, envSnapshot, "env");
    }

    private static AllowlistSnapshot MergeSnapshots(AllowlistSnapshot baseSnapshot, AllowlistSnapshot overrideSnapshot, string label)
    {
        var mergedProofs = new HashSet<string>(baseSnapshot.ProofTokens, StringComparer.Ordinal);
        mergedProofs.UnionWith(overrideSnapshot.ProofTokens);

        var mergedHashes = new HashSet<string>(baseSnapshot.ExeSha256, StringComparer.OrdinalIgnoreCase);
        mergedHashes.UnionWith(overrideSnapshot.ExeSha256);

        var minVersion = string.IsNullOrWhiteSpace(overrideSnapshot.MinVersion)
            || string.Equals(overrideSnapshot.MinVersion, "0.0.0", StringComparison.Ordinal)
            ? baseSnapshot.MinVersion
            : overrideSnapshot.MinVersion;

        return new AllowlistSnapshot(
            mergedProofs,
            mergedHashes,
            minVersion,
            $"{baseSnapshot.Source}+{label}",
            DateTime.UtcNow,
            true,
            string.Empty);
    }

    private AllowlistSnapshot ApplyRuntimeOverride(AllowlistSnapshot baseSnapshot)
    {
        RuntimeApplyMode applyMode;
        AllowlistSnapshot? runtimeSnapshot;
        lock (_runtimeSync)
        {
            applyMode = _runtimeApplyMode;
            runtimeSnapshot = BuildRuntimeSnapshotUnsafe();
        }

        if (runtimeSnapshot == null || !runtimeSnapshot.IsAvailable)
            return baseSnapshot;

        if (applyMode == RuntimeApplyMode.ReplaceSource || !baseSnapshot.IsAvailable)
        {
            return runtimeSnapshot with
            {
                Source = $"{runtimeSnapshot.Source}+replace_source",
                RefreshedUtc = DateTime.UtcNow,
                IsAvailable = true,
                FailureReason = string.Empty
            };
        }

        return MergeSnapshots(baseSnapshot, runtimeSnapshot, "runtime");
    }

    private RuntimeAllowlistView BuildRuntimeViewUnsafe()
    {
        var enabled = _runtimeModel != null;
        var applyMode = _runtimeApplyMode == RuntimeApplyMode.ReplaceSource ? "replace_source" : "merge";

        if (!enabled)
        {
            return new RuntimeAllowlistView(
                false,
                applyMode,
                DateTime.MinValue,
                new List<string>(),
                new List<string>(),
                new List<string>(),
                new List<string>(),
                new List<string>(),
                "0.0.0",
                0,
                0);
        }

        var snapshot = BuildRuntimeSnapshotUnsafe();
        var model = _runtimeModel!;
        var hashCount = snapshot?.ExeSha256.Count ?? 0;
        var proofCount = snapshot?.ProofTokens.Count ?? 0;
        return new RuntimeAllowlistView(
            true,
            applyMode,
            _runtimeUpdatedUtc,
            new List<string>(model.ProofTokens),
            new List<string>(model.AllowedClientExeSha256),
            new List<string>(model.AllowedDevExeSha256),
            new List<string>(model.AllowedReleaseExeSha256),
            new List<string>(model.ExeSha256),
            string.IsNullOrWhiteSpace(model.MinVersion) ? "0.0.0" : model.MinVersion.Trim(),
            hashCount,
            proofCount);
    }

    private AllowlistSnapshot? BuildRuntimeSnapshotUnsafe()
    {
        if (_runtimeModel == null)
            return null;

        var model = CloneModel(_runtimeModel);
        var snapshot = AllowlistSnapshot.FromModel(model, "runtime:admin");
        return snapshot with
        {
            RefreshedUtc = _runtimeUpdatedUtc == DateTime.MinValue ? DateTime.UtcNow : _runtimeUpdatedUtc,
            IsAvailable = true,
            FailureReason = string.Empty
        };
    }

    private static AllowlistModel CloneModel(AllowlistModel source)
    {
        return new AllowlistModel
        {
            ProofTokens = new List<string>(source.ProofTokens ?? new List<string>()),
            AllowedClientExeSha256 = new List<string>(source.AllowedClientExeSha256 ?? new List<string>()),
            AllowedDevExeSha256 = new List<string>(source.AllowedDevExeSha256 ?? new List<string>()),
            AllowedReleaseExeSha256 = new List<string>(source.AllowedReleaseExeSha256 ?? new List<string>()),
            ExeSha256 = new List<string>(source.ExeSha256 ?? new List<string>()),
            MinVersion = source.MinVersion
        };
    }

    private static void MergeModelInto(AllowlistModel target, AllowlistModel incoming)
    {
        target.ProofTokens = MergeDistinct(target.ProofTokens, incoming.ProofTokens, StringComparer.Ordinal);
        target.AllowedClientExeSha256 = MergeDistinct(target.AllowedClientExeSha256, incoming.AllowedClientExeSha256, StringComparer.OrdinalIgnoreCase);
        target.AllowedDevExeSha256 = MergeDistinct(target.AllowedDevExeSha256, incoming.AllowedDevExeSha256, StringComparer.OrdinalIgnoreCase);
        target.AllowedReleaseExeSha256 = MergeDistinct(target.AllowedReleaseExeSha256, incoming.AllowedReleaseExeSha256, StringComparer.OrdinalIgnoreCase);
        target.ExeSha256 = MergeDistinct(target.ExeSha256, incoming.ExeSha256, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(incoming.MinVersion) && !string.Equals(incoming.MinVersion.Trim(), "0.0.0", StringComparison.Ordinal))
            target.MinVersion = incoming.MinVersion.Trim();
    }

    private static List<string> MergeDistinct(List<string>? first, List<string>? second, StringComparer comparer)
    {
        var set = new HashSet<string>(first ?? new List<string>(), comparer);
        if (second != null)
            set.UnionWith(second);
        return set.ToList();
    }

    private static RuntimeApplyMode ParseRuntimeApplyMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "merge" => RuntimeApplyMode.Merge,
            _ => RuntimeApplyMode.ReplaceSource
        };
    }

    private static bool TryNormalizeAllowlistModel(AllowlistModel model, out AllowlistModel normalizedModel, out string error)
    {
        normalizedModel = new AllowlistModel();
        error = string.Empty;

        normalizedModel.ProofTokens = NormalizeProofTokens(model.ProofTokens);
        if (!TryNormalizeHashList("allowedClientExeSha256", model.AllowedClientExeSha256, out var allowedClient, out error))
            return false;
        if (!TryNormalizeHashList("allowedDevExeSha256", model.AllowedDevExeSha256, out var allowedDev, out error))
            return false;
        if (!TryNormalizeHashList("allowedReleaseExeSha256", model.AllowedReleaseExeSha256, out var allowedRelease, out error))
            return false;
        if (!TryNormalizeHashList("exeSha256", model.ExeSha256, out var legacy, out error))
            return false;

        normalizedModel.AllowedClientExeSha256 = allowedClient;
        normalizedModel.AllowedDevExeSha256 = allowedDev;
        normalizedModel.AllowedReleaseExeSha256 = allowedRelease;
        normalizedModel.ExeSha256 = legacy;
        normalizedModel.MinVersion = string.IsNullOrWhiteSpace(model.MinVersion) ? "0.0.0" : model.MinVersion.Trim();
        return true;
    }

    private static List<string> NormalizeProofTokens(IEnumerable<string>? values)
    {
        if (values == null)
            return new List<string>();

        return values
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryNormalizeHashList(
        string fieldName,
        IEnumerable<string>? values,
        out List<string> normalized,
        out string error)
    {
        normalized = new List<string>();
        error = string.Empty;
        if (values == null)
            return true;

        foreach (var raw in values)
        {
            var hash = NormalizeHashValue(raw);
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            if (!IsSha256(hash))
            {
                error = $"Invalid SHA256 in {fieldName}: {raw}";
                return false;
            }

            normalized.Add(hash);
        }

        normalized = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return false;
        }

        return true;
    }

    private static string? NormalizeHashValue(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;
        return hash.Trim().ToLowerInvariant();
    }

    private static List<string>? ResolveHashTargetList(AllowlistModel model, string? target, out string label)
    {
        var normalized = (target ?? "release").Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "client":
            case "all":
            case "any":
                label = "client";
                return model.AllowedClientExeSha256;
            case "dev":
                label = "dev";
                return model.AllowedDevExeSha256;
            case "release":
            case "official":
            case "community":
                label = "release";
                return model.AllowedReleaseExeSha256;
            default:
                label = string.Empty;
                return null;
        }
    }

    private AllowlistSnapshot? TryLoadFromEnvironment()
    {
        if (TryLoadModelFromJsonSources(out var jsonModel, out var jsonSource, out var jsonError))
        {
            return AllowlistSnapshot.FromModel(jsonModel, jsonSource);
        }

        if (!string.IsNullOrWhiteSpace(jsonError))
        {
            return AllowlistSnapshot.Unavailable("env", jsonError);
        }

        if (TryBuildModelFromEnvironmentVars(out var envModel, out var envSource))
        {
            return AllowlistSnapshot.FromModel(envModel, envSource);
        }

        return null;
    }

    private bool TryLoadModelFromJsonSources(out AllowlistModel model, out string source, out string error)
    {
        model = new AllowlistModel();
        source = string.Empty;
        error = string.Empty;

        // Prefer a secret file path (Render secret file support).
        if (!string.IsNullOrWhiteSpace(_allowlistJsonPath))
        {
            if (!File.Exists(_allowlistJsonPath))
            {
                error = $"ALLOWLIST_JSON_PATH does not exist: {_allowlistJsonPath}";
                return false;
            }

            try
            {
                var json = File.ReadAllText(_allowlistJsonPath);
                var parsed = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
                if (parsed == null)
                {
                    error = $"ALLOWLIST_JSON_PATH could not be parsed: {_allowlistJsonPath}";
                    return false;
                }

                model = parsed;
                source = $"env:file:{_allowlistJsonPath}";
                return true;
            }
            catch (Exception ex)
            {
                error = $"ALLOWLIST_JSON_PATH read failed: {ex.Message}";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(_allowlistJsonBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(_allowlistJsonBase64);
                var json = Encoding.UTF8.GetString(raw);
                var parsed = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
                if (parsed == null)
                {
                    error = "ALLOWLIST_JSON_BASE64 could not be parsed.";
                    return false;
                }

                model = parsed;
                source = "env:ALLOWLIST_JSON_BASE64";
                return true;
            }
            catch (Exception ex)
            {
                error = $"ALLOWLIST_JSON_BASE64 decode failed: {ex.Message}";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(_allowlistInlineJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AllowlistModel>(_allowlistInlineJson, JsonOptions);
                if (parsed == null)
                {
                    error = "ALLOWLIST_INLINE_JSON could not be parsed.";
                    return false;
                }

                model = parsed;
                source = "env:ALLOWLIST_INLINE_JSON";
                return true;
            }
            catch (Exception ex)
            {
                error = $"ALLOWLIST_INLINE_JSON parse failed: {ex.Message}";
                return false;
            }
        }

        return false;
    }

    private bool TryBuildModelFromEnvironmentVars(out AllowlistModel model, out string source)
    {
        var proofTokens = ParseList(_envProofTokens);
        var allowedClient = ParseList(_envAllowedClientHashes);
        var allowedDev = ParseList(_envAllowedDevHashes);
        var allowedRelease = ParseList(_envAllowedReleaseHashes);
        var legacyHashes = ParseList(_envLegacyHashes);

        var hasAny = proofTokens.Count > 0
            || allowedClient.Count > 0
            || allowedDev.Count > 0
            || allowedRelease.Count > 0
            || legacyHashes.Count > 0
            || !string.IsNullOrWhiteSpace(_envMinVersion);

        if (!hasAny)
        {
            model = new AllowlistModel();
            source = string.Empty;
            return false;
        }

        model = new AllowlistModel
        {
            ProofTokens = proofTokens,
            AllowedClientExeSha256 = allowedClient,
            AllowedDevExeSha256 = allowedDev,
            AllowedReleaseExeSha256 = allowedRelease,
            ExeSha256 = legacyHashes,
            MinVersion = string.IsNullOrWhiteSpace(_envMinVersion) ? "0.0.0" : _envMinVersion.Trim()
        };

        source = "env:variables";
        return true;
    }

    private static List<string> ParseList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw
            .Replace('\r', '\n')
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<AllowlistSnapshot?> TryLoadFromGithubAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_githubRepo)
            || string.IsNullOrWhiteSpace(_githubPath)
            || string.IsNullOrWhiteSpace(_githubBranch)
            || string.IsNullOrWhiteSpace(_githubToken))
        {
            return AllowlistSnapshot.Unavailable(
                $"github:{_githubRepo}/{_githubPath}",
                "GitHub allowlist environment variables are incomplete.");
        }

        var encodedPath = string.Join(
            "/",
            _githubPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
        var endpoint = $"https://api.github.com/repos/{_githubRepo}/contents/{encodedPath}?ref={_githubBranch}";

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("User-Agent", "LatticeVeil-GateServer");
        request.Headers.Add("Authorization", $"Bearer {_githubToken}");

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return AllowlistSnapshot.Unavailable(
                $"github:{_githubRepo}/{_githubPath}",
                $"GitHub API returned {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(ct);
        var github = JsonSerializer.Deserialize<GithubContentResponse>(body, JsonOptions);
        if (github == null || string.IsNullOrWhiteSpace(github.Content))
        {
            return AllowlistSnapshot.Unavailable(
                $"github:{_githubRepo}/{_githubPath}",
                "GitHub payload missing content.");
        }

        var base64 = github.Content.Replace("\n", string.Empty).Replace("\r", string.Empty);
        try
        {
            var raw = Convert.FromBase64String(base64);
            var json = Encoding.UTF8.GetString(raw);
            var model = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
            if (model == null)
            {
                return AllowlistSnapshot.Unavailable(
                    $"github:{_githubRepo}/{_githubPath}",
                    "Allowlist JSON could not be parsed.");
            }

            return AllowlistSnapshot.FromModel(model, $"github:{_githubRepo}/{_githubPath}");
        }
        catch (Exception ex)
        {
            return AllowlistSnapshot.Unavailable(
                $"github:{_githubRepo}/{_githubPath}",
                $"Allowlist decode failed: {ex.Message}");
        }
    }

    private sealed class GithubContentResponse
    {
        public string Content { get; set; } = string.Empty;
    }

    private enum AllowlistSourceMode
    {
        GitHub,
        EnvironmentOnly,
        Hybrid
    }

    private enum RuntimeApplyMode
    {
        Merge,
        ReplaceSource
    }
}

sealed class TicketRequest
{
    public string GameVersion { get; set; } = "dev";
    public string Platform { get; set; } = "windows";
    public string BuildFlavor { get; set; } = "community";
    public string? Proof { get; set; }
    public string? ExeHash { get; set; }
    public string? DevKey { get; set; }
    public Dictionary<string, string>? PublicConfigIds { get; set; }
}

sealed class TicketValidateRequest
{
    public string Ticket { get; set; } = string.Empty;
    public string? RequiredChannel { get; set; }
}

sealed record EosConfigPayload(
    string ProductId,
    string SandboxId,
    string DeploymentId,
    string ClientId,
    string ClientSecret,
    string ProductName,
    string ProductVersion,
    string LoginMode);

sealed class RuntimeAllowlistUpdateRequest
{
    public string Operation { get; set; } = "replace";
    public string ApplyMode { get; set; } = "replace_source";
    public List<string>? ProofTokens { get; set; }
    public List<string>? AllowedClientExeSha256 { get; set; }
    public List<string>? AllowedDevExeSha256 { get; set; }
    public List<string>? AllowedReleaseExeSha256 { get; set; }
    public List<string>? ExeSha256 { get; set; }
    public string? MinVersion { get; set; }
}

sealed class RuntimeCurrentHashRequest
{
    public string Hash { get; set; } = string.Empty;
    public string Target { get; set; } = "release";
    public bool ReplaceTargetList { get; set; } = true;
    public bool ClearOtherHashes { get; set; } = true;
    public string ApplyMode { get; set; } = "replace_source";
}

sealed record RuntimeAllowlistView(
    bool Enabled,
    string ApplyMode,
    DateTime UpdatedUtc,
    List<string> ProofTokens,
    List<string> AllowedClientExeSha256,
    List<string> AllowedDevExeSha256,
    List<string> AllowedReleaseExeSha256,
    List<string> ExeSha256,
    string MinVersion,
    int HashCount,
    int ProofCount);

sealed record RuntimeAllowlistAdminResult(
    bool Ok,
    string Message,
    RuntimeAllowlistView View);

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

sealed class TicketValidateResponse
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public string? Channel { get; set; }
    public string? ExpiresUtc { get; set; }

    public static TicketValidateResponse Approved(string channel, DateTime? expiresUtc) => new()
    {
        Ok = true,
        Channel = channel,
        ExpiresUtc = expiresUtc?.ToString("O"),
        Reason = "approved"
    };

    public static TicketValidateResponse Denied(string reason, string? channel = null, DateTime? expiresUtc = null) => new()
    {
        Ok = false,
        Reason = reason,
        Channel = channel,
        ExpiresUtc = expiresUtc?.ToString("O")
    };
}

sealed class AllowlistModel
{
    public List<string> ProofTokens { get; set; } = new();
    public List<string> AllowedClientExeSha256 { get; set; } = new();
    public List<string> AllowedDevExeSha256 { get; set; } = new();
    public List<string> AllowedReleaseExeSha256 { get; set; } = new();
    public List<string> ExeSha256 { get; set; } = new();
    public string MinVersion { get; set; } = "0.0.0";
}

sealed record AllowlistSnapshot(
    HashSet<string> ProofTokens,
    HashSet<string> ExeSha256,
    string MinVersion,
    string Source,
    DateTime RefreshedUtc,
    bool IsAvailable,
    string FailureReason)
{
    public static AllowlistSnapshot Empty(string source) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "0.0.0", source, DateTime.MinValue, false, "Allowlist not loaded.");

    public static AllowlistSnapshot Unavailable(string source, string reason) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), "0.0.0", source, DateTime.UtcNow, false, reason);

    public static AllowlistSnapshot FromModel(AllowlistModel model, string source)
    {
        var mergedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canonicalHashes = model.AllowedClientExeSha256 ?? new List<string>();
        var devHashes = model.AllowedDevExeSha256 ?? new List<string>();
        var releaseHashes = model.AllowedReleaseExeSha256 ?? new List<string>();
        var legacyHashes = model.ExeSha256 ?? new List<string>();

        for (var i = 0; i < canonicalHashes.Count; i++)
        {
            var value = (canonicalHashes[i] ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(value))
                mergedHashes.Add(value);
        }

        for (var i = 0; i < devHashes.Count; i++)
        {
            var value = (devHashes[i] ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(value))
                mergedHashes.Add(value);
        }

        for (var i = 0; i < releaseHashes.Count; i++)
        {
            var value = (releaseHashes[i] ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(value))
                mergedHashes.Add(value);
        }

        for (var i = 0; i < legacyHashes.Count; i++)
        {
            var value = (legacyHashes[i] ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(value))
                mergedHashes.Add(value);
        }

        return new AllowlistSnapshot(
            new HashSet<string>(model.ProofTokens?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()) ?? Enumerable.Empty<string>(), StringComparer.Ordinal),
            mergedHashes,
            string.IsNullOrWhiteSpace(model.MinVersion) ? "0.0.0" : model.MinVersion.Trim(),
            source,
            DateTime.UtcNow,
            true,
            string.Empty);
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
