using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AllowlistProvider>();
builder.Services.AddSingleton<IdentityRegistryProvider>();
builder.Services.AddSingleton<PresenceRegistryProvider>();

var app = builder.Build();

app.Logger.LogInformation("GateServer starting. Build: 2026-02-15 05:30. Version: v8.0.6-nuclear");

// 1. HIGH PRIORITY TEST ENDPOINT (No Auth)
// If this 404s, the deployed branch/build is not serving current code.
app.MapGet("/test-friends", () => Results.Ok(new { status = "v8.0.5 is LIVE", routes = "friends mapped" }));

// 2. Features probe
app.MapGet("/features", () => Results.Ok(new { ok = true, identity = true, friends = true, presence = true, serverTimeUtc = DateTime.UtcNow.ToString("O") }));

app.MapGet("/admin/diag", (HttpContext httpContext) => {
    if (!TryAuthorizeAdmin(httpContext, out var failure)) return failure!;
    return Results.Ok(new {
        version = "v8.0.6",
        identitySource = Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_SOURCE"),
        identityRepo = Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_REPO"),
        identityBranch = Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_BRANCH"),
        identityPath = Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_PATH"),
        hasGithubToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_TOKEN"))
    });
});

// 3. Friends Routes (Standardized)
void MapFriends(RouteGroupBuilder group)
{
    group.MapGet("/me", GetFriendsMe);
    group.MapPost("/add", FriendsAdd);
    group.MapPost("/request", FriendsAdd);
    group.MapPost("/remove", FriendsRemove);
    group.MapPost("/respond", FriendsRespond);
}
MapFriends(app.MapGroup("/friends"));
MapFriends(app.MapGroup("/identity/friends"));

// Handlers
async Task<IResult> GetFriendsMe(HttpContext httpContext, IdentityRegistryProvider identityProvider, CancellationToken ct) {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.GetFriendsAsync(GetProductUserIdFromClaims(claims), ct);
    return Results.Ok(new { ok = result.Ok, friends = result.Friends, incomingRequests = result.IncomingRequests, outgoingRequests = result.OutgoingRequests });
}
async Task<IResult> FriendsAdd(HttpContext httpContext, FriendsAddRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.AddFriendByQueryAsync(GetProductUserIdFromClaims(claims), request.Query, ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message });
}
async Task<IResult> FriendsRespond(HttpContext httpContext, FriendsRespondRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.RespondToFriendRequestAsync(GetProductUserIdFromClaims(claims), request.RequesterProductUserId, request.Accept, request.Block, ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message });
}
async Task<IResult> FriendsRemove(HttpContext httpContext, FriendsRemoveRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.RemoveFriendAsync(GetProductUserIdFromClaims(claims), request.ProductUserId, ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message });
}

// 4. Identity & Config
app.MapGet("/eos/config/gate", (HttpRequest request) => {
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem("Gate not configured.", statusCode: 503);
    if (!TryValidateHs256Jwt(ExtractBearerToken(request), signingKey, "latticeveil-gate", "latticeveil-client", out _, out _)) return Results.Unauthorized();
    if (!TryLoadEosConfigPayloadFromEnvironment(out var p, out var err)) return Results.Problem(err ?? "Config error", statusCode: 503);
    return Results.Json(p);
});

app.MapPost("/ticket", async (TicketRequest request, AllowlistProvider allowlistProvider, ILoggerFactory loggerFactory, CancellationToken ct) => {
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem("Gate signing key not configured.", statusCode: 503);

    var allowlist = await allowlistProvider.GetAsync(ct);
    if (!allowlist.IsAvailable) return Results.Ok(TicketResponse.Denied("Allowlist unavailable"));

    var flavor = (request.BuildFlavor ?? "release").Trim().ToLowerInvariant();
    var hash = (request.ExeHash ?? "").Trim().ToLowerInvariant();
    var proof = (request.Proof ?? "").Trim();

    // 1. Validate against allowlist
    bool isVerified = false;
    if (flavor == "dev") {
        // Dev builds must have their hash in the allowlist to be "verified"
        if (allowlist.ContainsHash(hash)) isVerified = true;
    } else {
        // Release builds must have their hash in the allowlist
        if (allowlist.ContainsHash(hash)) isVerified = true;
    }

    if (!isVerified) {
        return Results.Ok(TicketResponse.Denied($"Unverified build hash: {hash}"));
    }

    var now = DateTimeOffset.UtcNow;
    var expires = now.AddMinutes(30);
    var channel = flavor == "dev" ? "dev" : "release";

    var claims = new Dictionary<string, object?> { 
        ["iss"] = "latticeveil-gate", 
        ["aud"] = "latticeveil-client", 
        ["iat"] = now.ToUnixTimeSeconds(), 
        ["exp"] = expires.ToUnixTimeSeconds(), 
        ["puid"] = request.ProductUserId?.Trim(), 
        ["name"] = request.DisplayName?.Trim(),
        ["chan"] = channel,
        ["hash"] = hash
    };
    return Results.Ok(TicketResponse.Approved(CreateHs256Jwt(claims, signingKey), expires.UtcDateTime));
});

app.MapPost("/ticket/validate", (GateTicketValidateRequest request) => {
    var signingKey = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(signingKey)) return Results.Ok(new { ok = false, reason = "Gate not configured" });
    
    if (TryValidateHs256Jwt(request.Ticket ?? "", signingKey, "latticeveil-gate", "latticeveil-client", out var claims, out var err)) {
        var ticketChannel = "release";
        if (claims.TryGetValue("chan", out var chanEl) && chanEl.ValueKind == JsonValueKind.String) {
            ticketChannel = chanEl.GetString() ?? "release";
        }

        return Results.Ok(new { 
            ok = true, 
            channel = ticketChannel, 
            expiresUtc = TryGetUnixClaimUtc(claims, "exp")?.ToString("O") 
        });
    }
    return Results.Ok(new { ok = false, reason = err });
});

app.MapGet("/identity/me", async (HttpContext httpContext, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var puid = GetProductUserIdFromClaims(claims);
    var user = await identityProvider.GetByProductUserIdAsync(puid, ct);
    var rules = identityProvider.GetUsernameRules();
    var friends = await identityProvider.GetFriendsAsync(puid, ct);
    
    return Results.Ok(new { 
        ok = true, 
        found = user != null, 
        user,
        rules,
        friends = friends.Friends.Select(f => f.ProductUserId).ToList(),
        sentRequests = friends.OutgoingRequests.Select(r => r.ProductUserId).ToList(),
        receivedRequests = friends.IncomingRequests.Select(r => r.ProductUserId).ToList(),
        blockedUsers = friends.BlockedUsers.Select(u => u.ProductUserId).ToList(),
        count = friends.Friends.Count
    });
});

app.MapPost("/identity/claim", async (HttpContext httpContext, GateIdentityClaimRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var puid = request.ProductUserId ?? GetProductUserIdFromClaims(claims);
    var result = await identityProvider.ClaimAsync(puid, request.Username, request.DisplayName, allowReassign: false, ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message, code = result.Code, user = result.User });
});

app.MapPost("/identity/recovery/rotate", async (HttpContext httpContext, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.RotateRecoveryCodeAsync(GetProductUserIdFromClaims(claims), ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message, recoveryCode = result.RecoveryCode });
});

app.MapPost("/identity/transfer", async (HttpContext httpContext, GateIdentityTransferRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
    var result = await identityProvider.TransferIdentityAsync(request.Query, GetProductUserIdFromClaims(claims), request.RecoveryCode, ct);
    return Results.Ok(new { ok = result.Ok, message = result.Message, user = result.User });
});

app.MapPost("/identity/resolve", async (HttpContext httpContext, IdentityResolveRequest request, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeGateTicket(httpContext, out _, out var failure)) return failure!;
    var result = await identityProvider.ResolveAsync(request.Query, ct);
    return Results.Ok(new { ok = true, found = result.Found, user = result.User });
});

// Presence Routes
void MapPresence(RouteGroupBuilder group)
{
    group.MapPost("/upsert", async (HttpContext httpContext, PresenceUpsertInput input, PresenceRegistryProvider presenceProvider) => {
        if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
        // Optional: verify that input.ProductUserId matches the token claim
        var result = presenceProvider.Upsert(input);
        return Results.Ok(new { ok = true, presence = result });
    });

    group.MapPost("/query", async (HttpContext httpContext, PresenceQueryRequest request, PresenceRegistryProvider presenceProvider) => {
        if (!TryAuthorizeGateTicket(httpContext, out _, out var failure)) return failure!;
        var results = presenceProvider.Query(request.ProductUserIds);
        return Results.Ok(new { ok = true, entries = results });
    });

    group.MapPost("/invite/send", async (HttpContext httpContext, WorldInviteSendInput input, PresenceRegistryProvider presenceProvider) => {
        if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
        var senderPuid = GetProductUserIdFromClaims(claims);
        var senderName = GetDisplayNameFromClaims(claims);
        var invite = presenceProvider.SendInvite(senderPuid, senderName, input.TargetProductUserId, input.WorldName);
        return Results.Ok(new { ok = true, invite });
    });

    group.MapGet("/invites/me", async (HttpContext httpContext, PresenceRegistryProvider presenceProvider) => {
        if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
        var puid = GetProductUserIdFromClaims(claims);
        var incoming = presenceProvider.GetInvitesFor(puid);
        var outgoing = presenceProvider.GetInvitesFrom(puid);
        return Results.Ok(new { ok = true, incoming, outgoing });
    }).WithName("PresenceInvitesMe");

    group.MapPost("/invite/respond", async (HttpContext httpContext, WorldInviteResponseInput input, PresenceRegistryProvider presenceProvider) => {
        if (!TryAuthorizeGateTicket(httpContext, out var claims, out var failure)) return failure!;
        var responderPuid = GetProductUserIdFromClaims(claims);
        var ok = presenceProvider.RespondToInvite(responderPuid, input.SenderProductUserId, input.Response);
        return Results.Ok(new { ok });
    });
}
MapPresence(app.MapGroup("/presence"));
// Check for potential duplicate - if this exists, it would cause the ambiguity
// MapPresence(app.MapGroup("/identity/presence")); // This would cause the duplicate

// Admin endpoints for Hash Tool
app.MapGet("/admin/allowlist/runtime", (HttpContext httpContext, AllowlistProvider allowlistProvider) => {
    if (!TryAuthorizeAdmin(httpContext, out var failure)) return failure!;
    return Results.Ok(new { ok = true, runtime = allowlistProvider.GetRuntimeView() });
});

app.MapPost("/admin/allowlist/runtime", (HttpContext httpContext, RuntimeAllowlistUpdateRequest request, AllowlistProvider allowlistProvider) => {
    if (!TryAuthorizeAdmin(httpContext, out var failure)) return failure!;
    var model = new AllowlistModel { ProofTokens = request.ProofTokens ?? new(), AllowedClientExeSha256 = request.AllowedClientExeSha256 ?? new(), AllowedDevExeSha256 = request.AllowedDevExeSha256 ?? new(), AllowedReleaseExeSha256 = request.AllowedReleaseExeSha256 ?? new(), ExeSha256 = request.ExeSha256 ?? new(), MinVersion = request.MinVersion ?? "0.0.0" };
    var result = allowlistProvider.UpdateRuntimeAllowlist(request.Operation ?? "replace", request.ApplyMode, model);
    return result.Ok ? Results.Ok(new { ok = true, message = result.Message, runtime = result.View }) : Results.BadRequest(new { ok = false, reason = result.Message, runtime = result.View });
});

app.MapPost("/admin/allowlist/runtime/current-hash", (HttpContext httpContext, RuntimeCurrentHashRequest request, AllowlistProvider allowlistProvider) => {
    if (!TryAuthorizeAdmin(httpContext, out var failure)) return failure!;
    var result = allowlistProvider.SetRuntimeCurrentHash(request.Hash, request.Target, request.ReplaceTargetList, request.ClearOtherHashes, request.ApplyMode);
    return result.Ok ? Results.Ok(new { ok = true, message = result.Message, runtime = result.View }) : Results.BadRequest(new { ok = false, reason = result.Message, runtime = result.View });
});

// 5. Identity Registry Management & Webhooks
app.MapPost("/admin/identity/refresh", async (HttpContext httpContext, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    if (!TryAuthorizeAdmin(httpContext, out var failure)) return failure!;
    var result = await identityProvider.ForceRefreshAsync(ct);
    return result.Ok ? Results.Ok(new { ok = true, message = "Identity registry refreshed." }) : Results.Problem(result.Error, statusCode: 503);
});

app.MapPost("/webhook/github/identity", async (HttpContext httpContext, IdentityRegistryProvider identityProvider, CancellationToken ct) => {
    // Public endpoint for GitHub webhooks. Optional: verify X-Hub-Signature-256.
    var result = await identityProvider.ForceRefreshAsync(ct);
    return Results.Ok(new { ok = result.Ok });
});

app.Run();

// Minimal Static Helpers
static string NormalizeOrDefault(string? v, string f) => string.IsNullOrWhiteSpace(v) ? f : v.Trim();
static int ParsePositiveInt(string? v, int f) => int.TryParse(v, out var p) && p > 0 ? p : f;
static bool TryLoadEosConfigPayloadFromEnvironment(out EosConfigPayload payload, out string? error) {
    error = null;
    var pId = (Environment.GetEnvironmentVariable("EOS_PRODUCT_ID") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(pId)) { payload = new EosConfigPayload("", "", "", "", "", "LatticeVeil", "1.0.0", "deviceid"); error = "Missing EOS vars"; return false; }
    payload = new EosConfigPayload(pId, Environment.GetEnvironmentVariable("EOS_SANDBOX_ID") ?? "", Environment.GetEnvironmentVariable("EOS_DEPLOYMENT_ID") ?? "", Environment.GetEnvironmentVariable("EOS_CLIENT_ID") ?? "", Environment.GetEnvironmentVariable("EOS_CLIENT_SECRET") ?? "", "LatticeVeil", "1.0.0", "deviceid");
    return true;
}
static string? DecodeProofToken(string? b64) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64 ?? "")).Trim(); } catch { return null; } }
static string? NormalizeHash(string? h) => h?.Trim().ToLowerInvariant();
static string CreateHs256Jwt(Dictionary<string, object?> p, string k) {
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p)));
    var input = $"{header}.{payload}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(k));
    return $"{input}.{Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(input)))}";
}
static string Base64UrlEncode(byte[] b) => Convert.ToBase64String(b).Replace("+", "-").Replace("/", "_").TrimEnd('=');
static bool FixedTimeEquals(string l, string r) => !string.IsNullOrEmpty(l) && !string.IsNullOrEmpty(r) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(l), Encoding.UTF8.GetBytes(r));
static bool IsAllowlistedByPolicy(string m, bool h, bool p) => m.ToLowerInvariant() switch { "hash_only" => h, "hash_and_proof" => h && p, "proof_only" => p, _ => h || p };
static string ResolveTicketChannel(string? f, bool d) => d ? "dev" : "release";
static string ResolveTicketChannelFromClaims(Dictionary<string, JsonElement> c) => "release";
static string NormalizeTicketChannel(string? v) => "release";
static bool TryValidateHs256Jwt(string jwt, string key, string iss, string aud, out Dictionary<string, JsonElement> claims, out string reason) {
    claims = new(); reason = "";
    var parts = jwt.Split('.'); if (parts.Length != 3) { reason = "Malformed"; return false; }
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    if (!Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"))).Equals(parts[2])) { reason = "Invalid Sig"; return false; }
    claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TryBase64UrlDecode(parts[1]) ?? Array.Empty<byte>()) ?? new();
    return true;
}
static DateTime? TryGetUnixClaimUtc(Dictionary<string, JsonElement> c, string k) => c.TryGetValue(k, out var e) && e.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).UtcDateTime : null;
static bool TryGetStringClaim(Dictionary<string, JsonElement> c, string k, out string v) { v = ""; if (c.TryGetValue(k, out var e) && e.ValueKind == JsonValueKind.String) { v = e.GetString() ?? ""; return true; } return false; }
static byte[]? TryBase64UrlDecode(string s) { var n = s.Replace('-', '+').Replace('_', '/'); var p = n.Length % 4; if (p == 2) n += "=="; else if (p == 3) n += "="; try { return Convert.FromBase64String(n); } catch { return null; } }
static bool MatchesExpectedPublicIds(Dictionary<string, string>? c, string s, string d, PublicIdPolicy p) => true;
static PublicIdPolicy ResolvePublicIdPolicy(string? v) => PublicIdPolicy.Off;
static string? GetCaseInsensitiveValue(Dictionary<string, string> v, string k) => v.TryGetValue(k, out var x) ? x : null;
static bool TryAuthorizeGateTicket(HttpContext h, out Dictionary<string, JsonElement> c, out IResult? f) {
    c = new(); f = null;
    var key = (Environment.GetEnvironmentVariable("GATE_JWT_SIGNING_KEY") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(key)) { f = Results.Problem("Gate config error", statusCode: 503); return false; }
    if (!TryValidateHs256Jwt(ExtractBearerToken(h.Request), key, "latticeveil-gate", "latticeveil-client", out c, out var err)) { f = Results.Ok(new { ok = false, reason = err }); return false; }
    return true;
}
static string GetProductUserIdFromClaims(Dictionary<string, JsonElement> c) => TryGetStringClaim(c, "puid", out var p) ? p : "";
static string GetDisplayNameFromClaims(Dictionary<string, JsonElement> c) => TryGetStringClaim(c, "name", out var v) ? v : "";
static string NormalizePresenceStatus(string? v, bool h, string w) => v ?? "online";
static bool TryAuthorizeAdmin(HttpContext h, out IResult? f) {
    f = null;
    var expected = (Environment.GetEnvironmentVariable("GATE_ADMIN_TOKEN") ?? "").Trim();
    if (string.IsNullOrWhiteSpace(expected)) { f = Results.Problem("Admin API disabled", statusCode: 503); return false; }
    var provided = ExtractAdminToken(h);
    if (string.IsNullOrWhiteSpace(provided) || !FixedTimeEquals(provided.Trim(), expected)) { f = Results.Unauthorized(); return false; }
    return true;
}
static string ExtractAdminToken(HttpContext h) {
    var a = h.Request.Headers["Authorization"].ToString().Trim();
    if (a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return a[7..].Trim();
    return h.Request.Headers["X-Gate-Admin-Token"].ToString().Trim();
}
static string ExtractBearerToken(HttpRequest r) { var a = r.Headers["Authorization"].ToString(); return a.StartsWith("Bearer ") ? a[7..].Trim() : ""; }

sealed record PresenceQueryRequest(List<string> ProductUserIds);
sealed record GateTicketValidateRequest(string Ticket, string RequiredChannel);
sealed record GateIdentityClaimRequest(string? ProductUserId, string Username, string? DisplayName);
sealed record GateIdentityTransferRequest(string Query, string RecoveryCode);

enum PublicIdPolicy { Off }
