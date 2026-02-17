using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;

namespace LatticeVeilMonoGame.Online.Gate;

public class OnlineGateClient
{
    private static readonly object Sync = new();
    private static readonly object TicketSync = new();
    private static readonly HttpClient Http = CreateHttpClient();
    private static OnlineGateClient? _instance;
    private const string DefaultGateUrl = "https://eos-service.onrender.com";
    private static readonly TimeSpan DefaultTicketTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Admin-only: submits the current build hash for runtime allowlist approval.
    ///
    /// Newer EOS-Service versions use POST /admin/allowlist/runtime/current-hash (requires admin bearer token).
    /// Older builds used POST /admin/allowlist/runtime/submit. We attempt the new endpoint first and fall back on 404.
    /// </summary>
    public async Task<dynamic> SubmitHashForApprovalAsync(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return new { Ok = false, Message = "Empty hash" };

        var adminToken = (Environment.GetEnvironmentVariable("GATE_ADMIN_TOKEN")
                          ?? Environment.GetEnvironmentVariable("LV_GATE_ADMIN_TOKEN")
                          ?? "").Trim();

        var target = _buildFlavor;
        var v2 = $"{_gateUrl}/admin/allowlist/runtime/current-hash";
        var legacy = $"{_gateUrl}/admin/allowlist/runtime/submit";

        try
        {
            // Try the new endpoint first.
            var v2Payload = new
            {
                hash = hash,
                target = target,
                replaceTargetList = true,
                clearOtherHashes = false,
                applyMode = "replace_source"
            };

            var (ok, msg, status) = await PostJsonWithOptionalBearerAsync(v2, v2Payload, adminToken, default).ConfigureAwait(false);
            if (!ok && status == HttpStatusCode.NotFound)
            {
                // Fall back to the older endpoint (some legacy services accepted { hash } and responded with Ok/Message).
                var legacyPayload = new { hash = hash };
                (ok, msg, _) = await PostJsonWithOptionalBearerAsync(legacy, legacyPayload, adminToken, default).ConfigureAwait(false);
            }

            return new { Ok = ok, Message = msg };
        }
        catch (Exception ex)
        {
            return new { Ok = false, Message = ex.Message };
        }
    }

    private static async Task<(bool Ok, string Message, HttpStatusCode StatusCode)> PostJsonWithOptionalBearerAsync(
        string url,
        object payload,
        string bearerToken,
        CancellationToken ct)
    {
        try
        {
            var j = JsonSerializer.Serialize(payload, JsonOptions);
            using var m = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            m.Content = new StringContent(j, Encoding.UTF8, "application/json");

            var r = await Http.SendAsync(m, ct).ConfigureAwait(false);
            var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!r.IsSuccessStatusCode)
            {
                var msg = string.IsNullOrWhiteSpace(body) ? (r.ReasonPhrase ?? "HTTP error") : body;
                return (false, msg, r.StatusCode);
            }

            // Accept plain-text or JSON responses.
            if (string.IsNullOrWhiteSpace(body)) return (true, "", r.StatusCode);

            // If it isn't JSON, treat success as OK.
            if (!body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                return (true, body, r.StatusCode);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var ok = TryGetBool(doc.RootElement, "ok", out var ok1) ? ok1 :
                             TryGetBool(doc.RootElement, "Ok", out ok1) ? ok1 : true;

                    var msg = TryGetString(doc.RootElement, "message")
                              ?? TryGetString(doc.RootElement, "Message")
                              ?? "";

                    return (ok, msg, r.StatusCode);
                }
            }
            catch
            {
                // Ignore parse errors and return body.
            }

            return (true, body, r.StatusCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, (HttpStatusCode)0);
        }
    }

    private static bool TryGetBool(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(name, out var p)) return false;

        if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) { value = b; return true; }
        return false;
    }

    private static string? TryGetString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private readonly string _gateUrl;
    private readonly bool _gateRequired;
    private readonly string _proofPath;
    private readonly string _buildFlavor;

    private DateTime _ticketExpiresUtc = DateTime.MinValue;
    private string _ticket = string.Empty;
    private string _ticketProductUserId = string.Empty;
    private string _status = "UNVERIFIED";
    private string _denialReason = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private OnlineGateClient()
    {
        _gateUrl = ResolveGateUrl();
        var requiredValue = Environment.GetEnvironmentVariable("LV_GATE_REQUIRED");
        _gateRequired = string.IsNullOrWhiteSpace(requiredValue) || ParseBool(requiredValue);
        _proofPath = Path.Combine(AppContext.BaseDirectory, "official_build.sig");
        _buildFlavor = File.Exists(_proofPath) ? "release" : "dev";
        TryRestorePreAuthorizedTicketFromEnvironment();
    }

    public static OnlineGateClient GetOrCreate() { lock (Sync) { _instance ??= new OnlineGateClient(); return _instance; } }

    public bool IsGateRequired => _gateRequired;
    public bool HasValidTicket => !string.IsNullOrWhiteSpace(_ticket) && DateTime.UtcNow < _ticketExpiresUtc;
    public string DenialReason => _denialReason;

    public bool TryGetValidTicketForChildProcess(out string ticket, out DateTime expiresUtc)
    {
        ticket = string.Empty; expiresUtc = DateTime.MinValue;
        if (!HasValidTicket) return false;
        ticket = _ticket; expiresUtc = _ticketExpiresUtc;
        return true;
    }

    public void SetTicket(string ticket, DateTime expiresUtc, string? productUserId = null)
    {
        _ticket = ticket;
        _ticketExpiresUtc = expiresUtc;
        _ticketProductUserId = productUserId ?? "";
        _status = "VERIFIED";
    }

    public string? ComputeCurrentHash(Logger log, string? target) => TryComputeExecutableHash(target, false);

    public bool CanUseOfficialOnline(Logger log, out string denialMessage)
    {
        denialMessage = string.Empty;
        if (!IsGateRequired || HasValidTicket) return true;
        if (EnsureTicket(log)) return true;
        denialMessage = string.IsNullOrWhiteSpace(_denialReason) ? "Online features unavailable." : _denialReason;
        return false;
    }

    public bool EnsureTicket(Logger log, TimeSpan? timeout = null, string? target = null)
    {
        lock (TicketSync)
        {
            if (HasUsableTicketForCurrentIdentity()) return true;
            using var cts = new CancellationTokenSource(timeout ?? DefaultTicketTimeout);
            try { return Task.Run(() => EnsureTicketAsync(log, cts.Token, target)).GetAwaiter().GetResult(); }
            catch { return false; }
        }
    }

    public async Task<GatePresenceQueryResult> QueryPresenceAsync(IReadOnlyCollection<string> friendIds)
    {
        var endpoint = $"{_gateUrl}/presence/query";
        var (ok, res, err) = await PostAuthorizedAsync<GatePresenceQueryRequest, GatePresenceQueryResponse>(endpoint, new GatePresenceQueryRequest { ProductUserIds = friendIds.ToList() }, default).ConfigureAwait(false);
        return new GatePresenceQueryResult { Ok = ok && res?.Ok == true, Entries = res?.Entries ?? new List<GatePresenceEntry>() };
    }

    public async Task<bool> UpsertPresenceAsync(string? productUserId, string? displayName, bool isHosting, string? worldName, string? gameMode, string? joinTarget, string? status)
    {
        var endpoint = $"{_gateUrl}/presence/upsert";
        var request = new GatePresenceUpsertRequest { ProductUserId = productUserId, DisplayName = displayName, IsHosting = isHosting, WorldName = worldName, GameMode = gameMode, JoinTarget = joinTarget, Status = status };
        var (ok, res, _) = await PostAuthorizedAsync<GatePresenceUpsertRequest, GatePresenceUpsertResponse>(endpoint, request, default).ConfigureAwait(false);
        return ok;
    }

    public Task<bool> StopHostingAsync(string? productUserId) => UpsertPresenceAsync(productUserId, null, false, null, null, null, "online");

    public async Task<bool> SendWorldInviteAsync(string targetProductUserId, string worldName)
    {
        var endpoint = $"{_gateUrl}/presence/invite/send";
        var request = new GateWorldInviteSendRequest { TargetProductUserId = targetProductUserId, WorldName = worldName };
        var (ok, _, _) = await PostAuthorizedAsync<GateWorldInviteSendRequest, GateWorldInviteSendResponse>(endpoint, request, default).ConfigureAwait(false);
        return ok;
    }

    public async Task<GateWorldInvitesMeResult> GetMyWorldInvitesAsync()
    {
        var endpoint = $"{_gateUrl}/presence/invites/me";
        var (ok, res, err) = await GetAuthorizedAsync<GateWorldInvitesMeResponse>(endpoint, default).ConfigureAwait(false);
        return new GateWorldInvitesMeResult { Ok = ok, Incoming = res?.Incoming ?? new List<GateWorldInviteEntry>(), Outgoing = res?.Outgoing ?? new List<GateWorldInviteEntry>() };
    }

    public async Task<bool> RespondToWorldInviteAsync(string senderProductUserId, string response)
    {
        var endpoint = $"{_gateUrl}/presence/invite/respond";
        var request = new GateWorldInviteResponseRequest { SenderProductUserId = senderProductUserId, Response = response };
        var (ok, _, _) = await PostAuthorizedAsync<GateWorldInviteResponseRequest, GateWorldInviteResponse>(endpoint, request, default).ConfigureAwait(false);
        return ok;
    }

    public async Task<GateIdentityResolveResult> ResolveIdentityAsync(string query)
    {
        var endpoint = $"{_gateUrl}/identity/resolve";
        var (ok, res, _) = await PostAuthorizedAsync<GateIdentityResolveRequest, GateIdentityResolveResponse>(endpoint, new GateIdentityResolveRequest { Query = query }, default).ConfigureAwait(false);
        return new GateIdentityResolveResult { Found = ok && res?.Found == true, User = res?.User, Reason = res?.Reason ?? "" };
    }

    public async Task<GateIdentityMeResult> GetMyIdentityAsync()
    {
        var endpoint = $"{_gateUrl}/identity/me";
        var (ok, res, err) = await GetAuthorizedAsync<GateIdentityMeResponse>(endpoint, default).ConfigureAwait(false);
        return new GateIdentityMeResult { Ok = ok, Found = res?.Found == true, User = res?.User, SentRequests = res?.SentRequests ?? Array.Empty<string>(), ReceivedRequests = res?.ReceivedRequests ?? Array.Empty<string>(), Friends = res?.Friends ?? Array.Empty<string>(), Count = res?.Count ?? 0, Reason = res?.Reason ?? "", Message = res?.Reason ?? "" };
    }

    public async Task<GateIdentityByDeviceResult> GetIdentityByDeviceIdAsync(string deviceId)
    {
        var endpoint = $"{_gateUrl}/identity/by-device";
        var request = new { deviceId = deviceId };
        var (ok, res, err) = await PostAsync<object, GateIdentityByDeviceResponse>(endpoint, request, default).ConfigureAwait(false);
        return new GateIdentityByDeviceResult { Ok = ok && res?.Found == true, User = res?.User, Reason = res?.Reason ?? err ?? "" };
    }

    public async Task<GateIdentityClaimResult> ClaimUsernameAsync(string username, string? displayName, string? productUserId)
    {
        var endpoint = $"{_gateUrl}/identity/claim";
        var request = new GateIdentityClaimRequest { ProductUserId = productUserId, Username = username, DisplayName = displayName };
        var (ok, res, _) = await PostAuthorizedAsync<GateIdentityClaimRequest, GateIdentityClaimResponse>(endpoint, request, default).ConfigureAwait(false);
        return new GateIdentityClaimResult { Ok = ok && res?.Ok == true, Code = res?.Code ?? "fail", Message = res?.Message ?? "" };
    }

    public async Task<GateRecoveryCodeResult> RotateRecoveryCodeAsync()
    {
        var endpoint = $"{_gateUrl}/identity/recovery/rotate";
        var (ok, res, _) = await PostAuthorizedAsync<object, GateRecoveryCodeResponse>(endpoint, new { }, default).ConfigureAwait(false);
        return new GateRecoveryCodeResult { Ok = ok && res?.Ok == true, RecoveryCode = res?.RecoveryCode ?? "" };
    }

    public async Task<GateCurrentHashResult> GetCurrentHashAsync(string? target = null)
    {
        var endpoint = $"{_gateUrl}/admin/allowlist/runtime/current-hash";
        
        // Try GET request without authentication first
        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync(endpoint);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var res = JsonSerializer.Deserialize<GateCurrentHashResponse>(content, JsonOptions);
                if (res?.Ok == true)
                {
                    return new GateCurrentHashResult { Ok = true, Hash = res.Hash ?? "", Target = target ?? _buildFlavor, Message = res.Message ?? "" };
                }
                return new GateCurrentHashResult { Ok = false, Hash = "", Target = target ?? _buildFlavor, Message = "Response indicated failure" };
            }
            return new GateCurrentHashResult { Ok = false, Hash = "", Target = target ?? _buildFlavor, Message = $"HTTP request failed: {response.StatusCode}" };
        }
        catch (Exception ex)
        {
            return new GateCurrentHashResult { Ok = false, Hash = "", Target = target ?? _buildFlavor, Message = ex.Message };
        }
    }

    public async Task<bool> VerifyCurrentHashAsync(Logger log, string? target = null)
    {
        try
        {
            var currentHash = TryComputeExecutableHash(target, false);
            if (string.IsNullOrWhiteSpace(currentHash))
            {
                log.Error("Failed to compute current EXE hash");
                return false;
            }

            log.Info($"Local EXE hash: {currentHash}");

            // Try to get server hash (new public endpoint doesn't require authentication)
            var serverHashResult = await GetCurrentHashAsync(target);
            log.Info($"Server response: Ok={serverHashResult.Ok}, Hash={serverHashResult.Hash}, Message={serverHashResult.Message}");
            log.Info($"Hash comparison: Local={currentHash}, Server={serverHashResult.Hash}, Match={string.Equals(currentHash, serverHashResult.Hash, StringComparison.OrdinalIgnoreCase)}");
            
            if (!serverHashResult.Ok)
            {
                log.Error($"Failed to get server hash: {serverHashResult.Message}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(serverHashResult.Hash))
            {
                log.Warn("No hash configured on server - allowing verification");
                return true;
            }

            if (!string.Equals(currentHash, serverHashResult.Hash, StringComparison.OrdinalIgnoreCase))
            {
                log.Warn($"Hash mismatch: local={currentHash}, server={serverHashResult.Hash}");
                return false;
            }

            log.Info("Hash verification successful");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Hash verification failed");
            return false;
        }
    }

    // Back-compat: older launcher code calls this method name.
    // Semantics are identical to VerifyCurrentHashAsync.
    public Task<bool> VerifyExecutableHashAsync(Logger log, string? target = null)
    {
        return VerifyCurrentHashAsync(log, target);
    }

    public async Task<GateIdentityTransferResult> TransferIdentityAsync(string query, string recoveryCode, string productUserId)
    {
        var endpoint = $"{_gateUrl}/identity/transfer";
        var request = new GateIdentityTransferRequest { Query = query, RecoveryCode = recoveryCode, ProductUserId = productUserId };
        var (ok, res, _) = await PostAuthorizedAsync<GateIdentityTransferRequest, GateIdentityTransferResponse>(endpoint, request, default).ConfigureAwait(false);
        return new GateIdentityTransferResult { Ok = ok && res?.Ok == true, User = res?.User };
    }

    public async Task<GateFriendsResult> GetFriendsAsync()
    {
        var endpoint = $"{_gateUrl}/friends/me";
        var (ok, res, err) = await GetAuthorizedAsync<GateFriendsResponse>(endpoint, default).ConfigureAwait(false);
        return new GateFriendsResult { Ok = ok, Message = res?.Message ?? err ?? "", Friends = res?.Friends ?? new List<GateIdentityUser>(), IncomingRequests = res?.IncomingRequests ?? new List<GateFriendRequest>(), OutgoingRequests = res?.OutgoingRequests ?? new List<GateFriendRequest>(), BlockedUsers = res?.BlockedUsers ?? new List<GateIdentityUser>() };
    }

    public async Task<GateFriendMutationResult> AddFriendAsync(string query)
    {
        var endpoint = $"{_gateUrl}/friends/add";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendAddRequest, GateFriendMutationResponse>(endpoint, new GateFriendAddRequest { Query = query }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> RemoveFriendAsync(string targetProductUserId)
    {
        var endpoint = $"{_gateUrl}/friends/remove";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendRemoveRequest, GateFriendMutationResponse>(endpoint, new GateFriendRemoveRequest { ProductUserId = targetProductUserId }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> RespondToFriendRequestAsync(string requesterProductUserId, bool accept, bool block = false)
    {
        var endpoint = $"{_gateUrl}/friends/respond";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendRespondRequest, GateFriendMutationResponse>(endpoint, new GateFriendRespondRequest { RequesterProductUserId = requesterProductUserId, Accept = accept, Block = block }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> BlockUserAsync(string query)
    {
        var endpoint = $"{_gateUrl}/friends/block";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendBlockRequest, GateFriendMutationResponse>(endpoint, new GateFriendBlockRequest { Query = query }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> UnblockUserAsync(string targetProductUserId)
    {
        var endpoint = $"{_gateUrl}/friends/unblock";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendUnblockRequest, GateFriendMutationResponse>(endpoint, new GateFriendUnblockRequest { ProductUserId = targetProductUserId }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public bool IsFriendsTemporarilyUnavailable => false;

    public bool ValidatePeerTicket(string? ticket, Logger log, out string denialReason, TimeSpan? timeout = null)
    {
        denialReason = ""; if (string.IsNullOrWhiteSpace(ticket)) { denialReason = "No ticket"; return false; }
        var endpoint = $"{_gateUrl}/ticket/validate";
        var request = new GateTicketValidateRequest { Ticket = ticket, RequiredChannel = _buildFlavor == "dev" ? "dev" : "release" };
        try { using var cts = new CancellationTokenSource(timeout ?? DefaultTicketTimeout); var (ok, response, error) = PostAuthorizedAsync<GateTicketValidateRequest, GateTicketValidateResponse>(endpoint, request, cts.Token).GetAwaiter().GetResult(); if (!ok || response == null) { denialReason = error ?? "Network error"; return false; } if (!response.Ok) { denialReason = response.Reason ?? "Invalid ticket"; return false; } return true; }
        catch (Exception ex) { denialReason = ex.Message; return false; }
    }

    private async Task<bool> EnsureTicketAsync(Logger log, CancellationToken ct, string? target)
    {
        var hash = TryComputeExecutableHash(target, false);
        if (string.IsNullOrWhiteSpace(hash)) return false;
        var eos = EosClientProvider.Current;
        var request = new { ProductUserId = eos?.LocalProductUserId, DisplayName = "Player", BuildFlavor = _buildFlavor, ExeHash = hash };
        var (ok, res, err) = await PostAsync<object, GateTicketResponse>($"{_gateUrl}/ticket", request, ct).ConfigureAwait(false);
        if (!ok || res?.Ok != true) { _denialReason = res?.Reason ?? err ?? "Gate denied."; return false; }
        _ticket = res.Ticket ?? "";
        if (DateTimeOffset.TryParse(res.ExpiresUtc, out var exp)) _ticketExpiresUtc = exp.UtcDateTime;
        else _ticketExpiresUtc = DateTime.UtcNow.AddMinutes(25);
        _ticketProductUserId = eos?.LocalProductUserId ?? "";
        _status = "VERIFIED";
        return true;
    }

    private async Task<(bool, T?, string?)> PostAsync<TReq, T>(string url, TReq req, CancellationToken ct) where T : class { try { var j = JsonSerializer.Serialize(req, JsonOptions); using var c = new StringContent(j, Encoding.UTF8, "application/json"); var r = await Http.PostAsync(url, c, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }
    private async Task<(bool, T?, string?)> PostAuthorizedAsync<TReq, T>(string url, TReq req, CancellationToken ct) where T : class { if (string.IsNullOrWhiteSpace(_ticket)) return (false, null, "No ticket"); try { var j = JsonSerializer.Serialize(req, JsonOptions); using var m = new HttpRequestMessage(HttpMethod.Post, url); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ticket); m.Content = new StringContent(j, Encoding.UTF8, "application/json"); var r = await Http.SendAsync(m, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }
    private async Task<(bool, T?, string?)> GetAuthorizedAsync<T>(string url, CancellationToken ct) where T : class { if (string.IsNullOrWhiteSpace(_ticket)) return (false, null, "No ticket"); try { using var m = new HttpRequestMessage(HttpMethod.Get, url); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ticket); var r = await Http.SendAsync(m, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }

    private static HttpClient CreateHttpClient() { var h = new HttpClientHandler(); if (h.SupportsAutomaticDecompression) h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) }; }
    private static string ResolveGateUrl() => Environment.GetEnvironmentVariable("LV_GATE_URL")?.TrimEnd('/') ?? DefaultGateUrl;
    private static bool ParseBool(string? v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    private void InvalidateTicket() { _ticket = ""; _ticketExpiresUtc = DateTime.MinValue; }
    private bool HasUsableTicketForCurrentIdentity() => HasValidTicket && string.Equals(_ticketProductUserId, EosClientProvider.Current?.LocalProductUserId ?? "", StringComparison.Ordinal);
    private static string? TryComputeExecutableHash(string? target, bool strict) 
    { 
        var p = target ?? Environment.ProcessPath ?? "";
        
        // If we're in launcher, try to find the game executable
        if (string.IsNullOrEmpty(p) || p.Contains("Launcher"))
        {
            var baseDir = AppContext.BaseDirectory;
            p = Path.Combine(baseDir, "LatticeVeilMonoGame.exe");
        }
        
        if (!File.Exists(p)) return null; 
        try 
        { 
            using var s = File.OpenRead(p); 
            using var h = SHA256.Create(); 
            return Convert.ToHexString(h.ComputeHash(s)).ToLowerInvariant(); 
        } 
        catch 
        { 
            return null; 
        } 
    }
    private void TryRestorePreAuthorizedTicketFromEnvironment() { }

    private class GateTicketResponse { public bool Ok { get; set; } public string? Ticket { get; set; } public string? ExpiresUtc { get; set; } public string? Reason { get; set; } }
    private class GatePresenceQueryRequest { public List<string> ProductUserIds { get; set; } = new(); }
    private class GatePresenceQueryResponse { public bool Ok { get; set; } public List<GatePresenceEntry>? Entries { get; set; } }
    private class GatePresenceUpsertRequest { public string? ProductUserId { get; set; } public string? DisplayName { get; set; } public bool IsHosting { get; set; } public string? WorldName { get; set; } public string? GameMode { get; set; } public string? JoinTarget { get; set; } public string? Status { get; set; } }
    private class GatePresenceUpsertResponse { public bool Ok { get; set; } }
    private class GateWorldInviteSendRequest { public string? TargetProductUserId { get; set; } public string? WorldName { get; set; } }
    private class GateWorldInviteSendResponse { public bool Ok { get; set; } }
    private class GateWorldInvitesMeResponse { public bool Ok { get; set; } public List<GateWorldInviteEntry>? Incoming { get; set; } public List<GateWorldInviteEntry>? Outgoing { get; set; } }
    private class GateWorldInviteResponseRequest { public string? SenderProductUserId { get; set; } public string? Response { get; set; } }
    private class GateWorldInviteResponse { public bool Ok { get; set; } }
    private class GateIdentityResolveRequest { public string? Query { get; set; } }
    private class GateIdentityResolveResponse { public bool Ok { get; set; } public bool Found { get; set; } public string? Reason { get; set; } public GateIdentityUser? User { get; set; } }
    private class GateIdentityMeResponse { public bool Ok { get; set; } public bool Found { get; set; } public string? Reason { get; set; } public GateIdentityUser? User { get; set; } public string[]? SentRequests { get; set; } public string[]? ReceivedRequests { get; set; } public string[]? Friends { get; set; } public int Count { get; set; } }
    private class GateIdentityClaimRequest { public string? ProductUserId { get; set; } public string? Username { get; set; } public string? DisplayName { get; set; } }
    private class GateIdentityClaimResponse { public bool Ok { get; set; } public string? Code { get; set; } public string? Message { get; set; } public GateIdentityUser? User { get; set; } }
    private class GateRecoveryCodeResponse { public bool Ok { get; set; } public string? RecoveryCode { get; set; } }
    private class GateIdentityTransferRequest { public string? Query { get; set; } public string? RecoveryCode { get; set; } public string? ProductUserId { get; set; } }
    private class GateIdentityTransferResponse { public bool Ok { get; set; } public GateIdentityUser? User { get; set; } }
    private class GateFriendsResponse { public bool Ok { get; set; } public string? Message { get; set; } public List<GateIdentityUser>? Friends { get; set; } public List<GateFriendRequest>? IncomingRequests { get; set; } public List<GateFriendRequest>? OutgoingRequests { get; set; } public List<GateIdentityUser>? BlockedUsers { get; set; } public int Count { get; set; } }
    private class GateFriendMutationResponse { public bool Ok { get; set; } public string? Message { get; set; } }
    private class GateFriendAddRequest { public string? Query { get; set; } }
    private class GateFriendRemoveRequest { public string? ProductUserId { get; set; } }
    private class GateFriendRespondRequest { public string? RequesterProductUserId { get; set; } public bool Accept { get; set; } public bool Block { get; set; } }
    private class GateFriendBlockRequest { public string? Query { get; set; } }
    private class GateFriendUnblockRequest { public string? ProductUserId { get; set; } }
    private class GateTicketValidateRequest { public string Ticket { get; set; } = ""; public string RequiredChannel { get; set; } = "release"; }
    private class GateTicketValidateResponse { public bool Ok { get; set; } public string? Reason { get; set; } }
}

public class GateIdentityUser { public string ProductUserId { get; set; } = ""; public string Username { get; set; } = ""; public string DisplayName { get; set; } = ""; public string FriendCode { get; set; } = ""; }
public class GatePresenceEntry { public string ProductUserId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Status { get; set; } = ""; public bool IsHosting { get; set; } public string WorldName { get; set; } = ""; public string GameMode { get; set; } = ""; public string JoinTarget { get; set; } = ""; public string FriendCode { get; set; } = ""; }
public class GateFriendRequest { public string ProductUserId { get; set; } = ""; public DateTime RequestedUtc { get; set; } public GateIdentityUser User { get; set; } = new(); }
public class GatePresenceQueryResult { public bool Ok { get; set; } public List<GatePresenceEntry> Entries { get; set; } = new(); }
public class GateWorldInviteEntry { public string SenderProductUserId { get; set; } = ""; public string TargetProductUserId { get; set; } = ""; public string SenderDisplayName { get; set; } = ""; public string WorldName { get; set; } = ""; public string Status { get; set; } = ""; public DateTime CreatedUtc { get; set; } }
public class GateWorldInvitesMeResult { public bool Ok { get; set; } public List<GateWorldInviteEntry> Incoming { get; set; } = new(); public List<GateWorldInviteEntry> Outgoing { get; set; } = new(); }
public class GateIdentityResolveResult { public bool Found { get; set; } public GateIdentityUser? User { get; set; } public string Reason { get; set; } = ""; }
public class GateIdentityMeResult { public bool Ok { get; set; } public bool Found { get; set; } public GateIdentityUser? User { get; set; } public string[]? SentRequests { get; set; } public string[]? ReceivedRequests { get; set; } public string[]? Friends { get; set; } public int Count { get; set; } public string Reason { get; set; } = ""; public string Message { get; set; } = ""; }
public class GateIdentityClaimResult { public bool Ok { get; set; } public string Code { get; set; } = ""; public string Message { get; set; } = ""; }
public class GateRecoveryCodeResult { public bool Ok { get; set; } public string RecoveryCode { get; set; } = ""; }
public class GateIdentityTransferResult { public bool Ok { get; set; } public GateIdentityUser? User { get; set; } }
public class GateFriendsResult { public bool Ok { get; set; } public List<GateIdentityUser> Friends { get; set; } = new(); public List<GateFriendRequest> IncomingRequests { get; set; } = new(); public List<GateFriendRequest> OutgoingRequests { get; set; } = new(); public List<GateIdentityUser> BlockedUsers { get; set; } = new(); public string Message { get; set; } = ""; }
public class GateFriendMutationResult { public bool Ok { get; set; } public string Message { get; set; } = ""; }
public class GateCurrentHashResult { public bool Ok { get; set; } public string Hash { get; set; } = ""; public string Target { get; set; } = ""; public string Message { get; set; } = ""; }
public class GateIdentityByDeviceResult { public bool Ok { get; set; } public bool Found { get; set; } public GateIdentityUser? User { get; set; } public string Reason { get; set; } = ""; }
internal class GateCurrentHashResponse { public bool Ok { get; set; } public string? Hash { get; set; } public string? Target { get; set; } public string? Message { get; set; } }
internal class GateIdentityByDeviceResponse { public bool Ok { get; set; } public bool Found { get; set; } public GateIdentityUser? User { get; set; } public string? Reason { get; set; } }
