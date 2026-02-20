using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    private const string DefaultGateUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
    private const string DefaultSupabaseAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";
    private const string DefaultVeilnetUrl = "https://latticeveil.github.io/veilnet";
    private static readonly TimeSpan DefaultTicketTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] TicketRetryDelays =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(3000)
    };

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

    public enum TicketCheckStatus
    {
        VerifiedAndTicketIssued,
        HashMismatch,
        MisconfiguredEndpoint,
        ServiceUnavailable,
        Unauthorized,
        BadResponse
    }

    public readonly struct TicketCheckResult
    {
        public bool Ok { get; init; }
        public TicketCheckStatus Status { get; init; }
        public string Message { get; init; }
    }

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

    public string? ComputeCurrentHash(Logger log, string? target)
    {
        return TryComputeExecutableHash(target, out _, out var hash) ? hash : null;
    }

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
        return EnsureTicketWithStatus(log, timeout, target).Ok;
    }

    public TicketCheckResult EnsureTicketWithStatus(Logger log, TimeSpan? timeout = null, string? target = null)
    {
        lock (TicketSync)
        {
            if (HasUsableTicketForCurrentIdentity())
            {
                return new TicketCheckResult
                {
                    Ok = true,
                    Status = TicketCheckStatus.VerifiedAndTicketIssued,
                    Message = "Ticket already valid."
                };
            }

            using var cts = new CancellationTokenSource(timeout ?? DefaultTicketTimeout);
            try
            {
                var result = Task.Run(() => EnsureTicketAsync(log, cts.Token, target)).GetAwaiter().GetResult();
                _denialReason = result.Ok ? string.Empty : result.Message;
                return result;
            }
            catch (OperationCanceledException ex)
            {
                _denialReason = ex.Message;
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.ServiceUnavailable,
                    Message = "Ticket request timed out."
                };
            }
            catch (Exception ex)
            {
                _denialReason = ex.Message;
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.ServiceUnavailable,
                    Message = $"Ticket request failed: {ex.Message}"
                };
            }
        }
    }

    public async Task<GatePresenceQueryResult> QueryPresenceAsync(IReadOnlyCollection<string> friendIds)
    {
        var endpoint = $"{_gateUrl}/presence/query";
        var (ok, res, err) = await PostAuthorizedAsync<GatePresenceQueryRequest, GatePresenceQueryResponse>(endpoint, new GatePresenceQueryRequest { ProductUserIds = friendIds.ToList() }, default).ConfigureAwait(false);
        return new GatePresenceQueryResult { Ok = ok && res?.Ok == true, Entries = res?.Entries ?? new List<GatePresenceEntry>() };
    }

    public async Task<bool> UpsertPresenceAsync(string? productUserId, string? displayName, bool isHosting, string? worldName, string? gameMode, string? joinTarget, string? status, bool cheats = false, int playerCount = 0, int maxPlayers = 0)
    {
        var endpoint = $"{_gateUrl}/presence/upsert";
        var request = new GatePresenceUpsertRequest { ProductUserId = productUserId, DisplayName = displayName, IsHosting = isHosting, WorldName = worldName, GameMode = gameMode, JoinTarget = joinTarget, Status = status, Cheats = cheats, PlayerCount = playerCount, MaxPlayers = maxPlayers };
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
        // Use specific endpoint based on build flavor
        var endpoint = $"{_gateUrl}/admin/allowlist/current-hash/{(target?.ToLowerInvariant() ?? _buildFlavor?.ToLowerInvariant() ?? "release")}";
        
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
            if (!TryComputeExecutableHash(target, out var exePath, out var currentHash))
            {
                log.Error("Failed to compute current EXE hash");
                return false;
            }

            log.Info($"Executable path used for hash: {exePath}");
            log.Info($"Computed EXE SHA256: {currentHash}");

            // Client validation must not call admin endpoints. Ask the gate for a ticket with our EXE hash.
            // If the gate approves, the hash is valid.
            var ticket = EnsureTicketWithStatus(log, null, target);
            if (ticket.Ok)
            {
                log.Info("Hash verification successful (ticket approved)");
                return true;
            }

            log.Warn($"Hash verification failed (ticket denied): {ticket.Message}");
            return false;
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
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendsResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/me";
        var (ok, res, err) = await GetAuthorizedAsync<GateFriendsResponse>(endpoint, default).ConfigureAwait(false);
        return new GateFriendsResult { Ok = ok, Message = res?.Message ?? err ?? "", Friends = res?.Friends ?? new List<GateIdentityUser>(), IncomingRequests = res?.IncomingRequests ?? new List<GateFriendRequest>(), OutgoingRequests = res?.OutgoingRequests ?? new List<GateFriendRequest>(), BlockedUsers = res?.BlockedUsers ?? new List<GateIdentityUser>() };
    }

    public async Task<GateFriendMutationResult> AddFriendAsync(string query)
    {
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendMutationResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/add";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendAddRequest, GateFriendMutationResponse>(endpoint, new GateFriendAddRequest { Query = query }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<(bool Ok, bool Exists, string Message)> VeilnetUserExistsAsync(string username, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            return (false, false, "Empty username");

        var veilnetBase = (Environment.GetEnvironmentVariable("LV_VEILNET_URL") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(veilnetBase))
            veilnetBase = DefaultVeilnetUrl;
        veilnetBase = veilnetBase.TrimEnd('/');

        var url = $"{veilnetBase}/api/users/exists?username={Uri.EscapeDataString(username)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.NotFound)
                return (true, false, "not_found");

            if (res.IsSuccessStatusCode)
                return (true, true, "ok");

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var msg = string.IsNullOrWhiteSpace(body) ? (res.ReasonPhrase ?? "HTTP error") : body;
            return (false, false, msg);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    public async Task<GateFriendMutationResult> RemoveFriendAsync(string targetProductUserId)
    {
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendMutationResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/remove";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendRemoveRequest, GateFriendMutationResponse>(endpoint, new GateFriendRemoveRequest { ProductUserId = targetProductUserId }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> RespondToFriendRequestAsync(string requesterProductUserId, bool accept, bool block = false)
    {
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendMutationResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/respond";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendRespondRequest, GateFriendMutationResponse>(endpoint, new GateFriendRespondRequest { RequesterProductUserId = requesterProductUserId, Accept = accept, Block = block }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> BlockUserAsync(string query)
    {
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendMutationResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/block";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendBlockRequest, GateFriendMutationResponse>(endpoint, new GateFriendBlockRequest { Query = query }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public async Task<GateFriendMutationResult> UnblockUserAsync(string targetProductUserId)
    {
        if (!HasUsableTicketForCurrentIdentity())
        {
            if (!EnsureTicket(new Logger("Gate")))
                return new GateFriendMutationResult { Ok = false, Message = string.IsNullOrWhiteSpace(_denialReason) ? "Unauthorized" : _denialReason };
        }
        var endpoint = $"{_gateUrl}/friends/unblock";
        var (ok, res, _) = await PostAuthorizedAsync<GateFriendUnblockRequest, GateFriendMutationResponse>(endpoint, new GateFriendUnblockRequest { ProductUserId = targetProductUserId }, default).ConfigureAwait(false);
        return new GateFriendMutationResult { Ok = ok && res?.Ok == true, Message = res?.Message ?? "" };
    }

    public bool IsFriendsTemporarilyUnavailable => false;

    public bool ValidatePeerTicket(string? ticket, Logger log, out string denialReason, TimeSpan? timeout = null)
    {
        denialReason = "";
        if (string.IsNullOrWhiteSpace(ticket))
        {
            denialReason = "No ticket";
            return false;
        }

        var endpoint = $"{_gateUrl.TrimEnd('/')}/online-ticket-validate";
        var requiredChannel = _buildFlavor == "dev" ? "dev" : "release";
        var anonKey = ResolveSupabaseAnonKey();
        if (string.IsNullOrWhiteSpace(anonKey))
        {
            denialReason = "Gate validation misconfigured: SUPABASE_ANON_KEY is missing.";
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout ?? DefaultTicketTimeout);
            var body = JsonSerializer.Serialize(
                new GateTicketValidateRequest { Ticket = ticket.Trim(), RequiredChannel = requiredChannel },
                JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            ApplyTicketFunctionHeaders(request, string.Empty, anonKey);

            using var response = Http.SendAsync(request, cts.Token).GetAwaiter().GetResult();
            var responseBody = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                denialReason = $"Ticket validation failed (HTTP {(int)response.StatusCode}).";
                return false;
            }

            GateTicketValidateResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GateTicketValidateResponse>(responseBody, JsonOptions);
            }
            catch
            {
                denialReason = "Invalid ticket validation response.";
                return false;
            }

            if (parsed?.Ok == true)
                return true;

            denialReason = string.IsNullOrWhiteSpace(parsed?.Reason) ? "Invalid ticket" : parsed!.Reason!;
            return false;
        }
        catch (Exception ex)
        {
            denialReason = ex.Message;
            return false;
        }
    }

    private async Task<TicketCheckResult> EnsureTicketAsync(Logger log, CancellationToken ct, string? target)
    {
        if (!TryComputeExecutableHash(target, out var executablePath, out var hash))
        {
            _denialReason = "Unable to compute executable hash.";
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.BadResponse,
                Message = _denialReason
            };
        }

        log.Info($"Executable path used for hash: {executablePath}");
        log.Info($"Computed EXE SHA256: {hash}");

        var payload = new
        {
            target = _buildFlavor == "dev" ? "dev" : "release",
            exe_hash_sha256 = hash,
            build_nonce = (Environment.GetEnvironmentVariable("LV_BUILD_NONCE") ?? string.Empty).Trim(),
            client_version = (Environment.GetEnvironmentVariable("LV_CLIENT_VERSION") ?? string.Empty).Trim()
        };

        var endpointCandidates = ResolveTicketEndpointCandidates(_gateUrl);
        if (endpointCandidates.Count == 0)
        {
            _denialReason = "Gate endpoint misconfigured: no ticket endpoint URL could be resolved.";
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.MisconfiguredEndpoint,
                Message = _denialReason
            };
        }

        var accessToken = ResolveVeilnetAccessToken();
        var anonKey = ResolveSupabaseAnonKey();
        var lastResult = new TicketCheckResult
        {
            Ok = false,
            Status = TicketCheckStatus.MisconfiguredEndpoint,
            Message = "Gate endpoint misconfigured."
        };

        for (var i = 0; i < endpointCandidates.Count; i++)
        {
            var endpointUrl = endpointCandidates[i];
            var isSupabaseFunctions = IsSupabaseFunctionsEndpoint(endpointUrl);
            if (isSupabaseFunctions && string.IsNullOrWhiteSpace(anonKey))
            {
                lastResult = new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.MisconfiguredEndpoint,
                    Message = "Gate endpoint misconfigured: SUPABASE_ANON_KEY is missing for function invocation."
                };
                _denialReason = lastResult.Message;
                log.Warn($"[gate] ticket config issue url={endpointUrl}: {lastResult.Message}");
                continue;
            }

            lastResult = await RequestTicketWithRetriesAsync(
                log,
                endpointUrl,
                payload,
                isSupabaseFunctions,
                accessToken,
                anonKey,
                ct).ConfigureAwait(false);

            if (lastResult.Ok)
                return lastResult;

            if (lastResult.Status != TicketCheckStatus.MisconfiguredEndpoint || i == endpointCandidates.Count - 1)
                return lastResult;

            log.Warn($"[gate] ticket endpoint fallback after misconfigured result at {endpointUrl}");
        }

        return lastResult;
    }

    private async Task<TicketCheckResult> RequestTicketWithRetriesAsync(
        Logger log,
        string endpointUrl,
        object payload,
        bool isSupabaseFunctions,
        string accessToken,
        string anonKey,
        CancellationToken ct)
    {
        var maxAttempts = TicketRetryDelays.Length;
        TicketCheckResult lastResult = new()
        {
            Ok = false,
            Status = TicketCheckStatus.ServiceUnavailable,
            Message = "Ticket request failed."
        };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            lastResult = await RequestTicketOnceAsync(
                log,
                endpointUrl,
                payload,
                isSupabaseFunctions,
                accessToken,
                anonKey,
                attempt + 1,
                maxAttempts,
                ct).ConfigureAwait(false);

            if (lastResult.Ok)
                return lastResult;

            if (lastResult.Status != TicketCheckStatus.ServiceUnavailable || attempt == maxAttempts - 1)
                return lastResult;

            var baseDelay = TicketRetryDelays[attempt];
            var jitterMs = Random.Shared.Next(50, 250);
            var retryDelay = baseDelay + TimeSpan.FromMilliseconds(jitterMs);
            log.Warn($"[gate] ticket retry in {retryDelay.TotalMilliseconds:0}ms after ServiceUnavailable (attempt {attempt + 1}/{maxAttempts}).");
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }

        return lastResult;
    }

    private async Task<TicketCheckResult> RequestTicketOnceAsync(
        Logger log,
        string endpointUrl,
        object payload,
        bool isSupabaseFunctions,
        string accessToken,
        string anonKey,
        int attempt,
        int maxAttempts,
        CancellationToken ct)
    {
        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        log.Info($"[gate] ticket request attempt {attempt}/{maxAttempts} url={endpointUrl}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
        {
            Content = new StringContent(serialized, Encoding.UTF8, "application/json")
        };

        if (isSupabaseFunctions)
        {
            ApplyTicketFunctionHeaders(request, accessToken, anonKey);
        }

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var snippet = TrimSnippet(responseBody, 200);
            var statusCode = (int)response.StatusCode;
            log.Info($"[gate] ticket response url={endpointUrl} status={statusCode} body=\"{snippet}\"");

            if (!response.IsSuccessStatusCode)
            {
                return BuildStatusFromHttpFailure(response.StatusCode, responseBody);
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.BadResponse,
                    Message = "Ticket endpoint returned an empty response."
                };
            }

            GateTicketResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GateTicketResponse>(responseBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                log.Warn($"[gate] ticket parse error ({ex.GetType().Name}): {ex.Message}");
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.BadResponse,
                    Message = "Ticket endpoint returned non-JSON or malformed JSON."
                };
            }

            if (parsed == null)
            {
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.BadResponse,
                    Message = "Ticket endpoint returned an empty JSON payload."
                };
            }

            if (parsed.Ok != true)
            {
                var reason = (parsed.Reason ?? parsed.Error ?? parsed.Message ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(reason)
                    && responseBody.Contains("Hello from Functions", StringComparison.OrdinalIgnoreCase))
                {
                    return new TicketCheckResult
                    {
                        Ok = false,
                        Status = TicketCheckStatus.MisconfiguredEndpoint,
                        Message = "Online ticket service not deployed/configured."
                    };
                }

                var mapped = BuildStatusFromReason(reason);
                return mapped;
            }

            var ticket = (parsed.Ticket ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ticket))
            {
                var appearsUnimplemented =
                    responseBody.Contains("Hello from Functions", StringComparison.OrdinalIgnoreCase)
                    || responseBody.Contains("not deployed", StringComparison.OrdinalIgnoreCase)
                    || responseBody.Contains("not configured", StringComparison.OrdinalIgnoreCase);

                if (appearsUnimplemented)
                {
                    return new TicketCheckResult
                    {
                        Ok = false,
                        Status = TicketCheckStatus.MisconfiguredEndpoint,
                        Message = "Online ticket service not deployed/configured."
                    };
                }

                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.BadResponse,
                    Message = "Ticket response is missing the ticket value."
                };
            }

            _ticket = ticket;
            if (DateTimeOffset.TryParse(parsed.ExpiresUtc ?? parsed.ExpiresAt, out var expires))
                _ticketExpiresUtc = expires.UtcDateTime;
            else
                _ticketExpiresUtc = DateTime.UtcNow.AddMinutes(25);

            _ticketProductUserId = EosClientProvider.Current?.LocalProductUserId ?? "";
            _status = "VERIFIED";
            _denialReason = string.Empty;
            ApplyEosConfigFromTicket(parsed.Eos);

            return new TicketCheckResult
            {
                Ok = true,
                Status = TicketCheckStatus.VerifiedAndTicketIssued,
                Message = "Ticket issued."
            };
        }
        catch (Exception ex)
        {
            var status = BuildStatusFromException(ex);
            log.Warn($"[gate] ticket request exception url={endpointUrl} type={ex.GetType().Name} message={ex.Message}");
            return status;
        }
    }

    private static TicketCheckResult BuildStatusFromHttpFailure(HttpStatusCode statusCode, string body)
    {
        var bodyText = (body ?? string.Empty).Trim();
        var message = $"Ticket request failed (HTTP {(int)statusCode}).";

        if (IsHashMismatchBody(bodyText))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.HashMismatch,
                Message = "Unofficial build - online disabled."
            };
        }

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.MisconfiguredEndpoint,
                Message = "Gate endpoint misconfigured (ticket route missing or wrong HTTP method)."
            };
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.Unauthorized,
                Message = "Online auth failed. Please re-login."
            };
        }

        if (statusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests)
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.ServiceUnavailable,
                Message = "Online services unavailable right now."
            };
        }

        if ((int)statusCode >= 500)
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.ServiceUnavailable,
                Message = "Online services unavailable right now."
            };
        }

        if (bodyText.Contains("function not found", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("not deployed", StringComparison.OrdinalIgnoreCase))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.MisconfiguredEndpoint,
                Message = "Online ticket service not deployed/configured."
            };
        }

        return new TicketCheckResult
        {
            Ok = false,
            Status = TicketCheckStatus.BadResponse,
            Message = message
        };
    }

    private static TicketCheckResult BuildStatusFromReason(string reason)
    {
        var normalized = (reason ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.BadResponse,
                Message = "Ticket endpoint denied the request."
            };
        }

        if (ContainsAny(normalized, "not found", "route", "endpoint", "method", "not deployed", "not configured"))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.MisconfiguredEndpoint,
                Message = "Gate endpoint misconfigured."
            };
        }

        if (ContainsAny(normalized, "unauthorized", "forbidden", "401", "403", "auth"))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.Unauthorized,
                Message = "Online auth failed. Please re-login."
            };
        }

        if (ContainsAny(normalized, "service unavailable", "timeout", "timed out", "network", "connection", "temporarily unavailable", "dns", "5"))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.ServiceUnavailable,
                Message = "Online services unavailable right now."
            };
        }

        if (ContainsAny(normalized, "hash_mismatch", "hash", "allowlist", "unofficial", "not allowed", "rejected", "denied"))
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.HashMismatch,
                Message = string.IsNullOrWhiteSpace(reason) ? "Build hash rejected." : reason
            };
        }

        return new TicketCheckResult
        {
            Ok = false,
            Status = TicketCheckStatus.BadResponse,
            Message = reason
        };
    }

    private static TicketCheckResult BuildStatusFromException(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException)
        {
            return new TicketCheckResult
            {
                Ok = false,
                Status = TicketCheckStatus.ServiceUnavailable,
                Message = "Online services timed out."
            };
        }

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
                return BuildStatusFromHttpFailure(httpEx.StatusCode.Value, string.Empty);

            if (httpEx.InnerException is SocketException)
            {
                return new TicketCheckResult
                {
                    Ok = false,
                    Status = TicketCheckStatus.ServiceUnavailable,
                    Message = "Online services are unreachable."
                };
            }
        }

        return new TicketCheckResult
        {
            Ok = false,
            Status = TicketCheckStatus.ServiceUnavailable,
            Message = "Online services unavailable right now."
        };
    }

    private static void ApplyTicketFunctionHeaders(HttpRequestMessage request, string accessToken, string anonKey)
    {
        if (!string.IsNullOrWhiteSpace(anonKey))
            request.Headers.TryAddWithoutValidation("apikey", anonKey);

        var bearer = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : anonKey;
        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }

    private static List<string> ResolveTicketEndpointCandidates(string baseUrl)
    {
        var explicitUrl = (Environment.GetEnvironmentVariable("LV_GATE_TICKET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return new List<string> { explicitUrl.TrimEnd('/') };

        var normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedBase))
            return new List<string>();

        if (normalizedBase.EndsWith("/online-ticket", StringComparison.OrdinalIgnoreCase))
            return new List<string> { normalizedBase };

        return new List<string> { $"{normalizedBase}/online-ticket" };
    }

    private static bool IsSupabaseFunctionsEndpoint(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(".supabase.co/functions/v1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSupabaseAnonKey()
    {
        var keys = new[]
        {
            "LV_SUPABASE_ANON_KEY",
            "SUPABASE_ANON_KEY",
            "VEILNET_SUPABASE_ANON_KEY"
        };

        for (var i = 0; i < keys.Length; i++)
        {
            var value = (Environment.GetEnvironmentVariable(keys[i]) ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return DefaultSupabaseAnonKey;
    }

    private static string ResolveVeilnetAccessToken()
    {
        return (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
    }

    private static string TrimSnippet(string value, int maxLen)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLen)
            return normalized;
        return normalized.Substring(0, maxLen);
    }

    private async Task<(bool, T?, string?)> PostAsync<TReq, T>(string url, TReq req, CancellationToken ct) where T : class { try { var j = JsonSerializer.Serialize(req, JsonOptions); using var c = new StringContent(j, Encoding.UTF8, "application/json"); var r = await Http.PostAsync(url, c, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }
    private async Task<(bool, T?, string?)> PostAuthorizedAsync<TReq, T>(string url, TReq req, CancellationToken ct) where T : class { if (string.IsNullOrWhiteSpace(_ticket)) return (false, null, "No ticket"); try { var j = JsonSerializer.Serialize(req, JsonOptions); using var m = new HttpRequestMessage(HttpMethod.Post, url); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ticket); m.Content = new StringContent(j, Encoding.UTF8, "application/json"); var r = await Http.SendAsync(m, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }
    private async Task<(bool, T?, string?)> GetAuthorizedAsync<T>(string url, CancellationToken ct) where T : class { if (string.IsNullOrWhiteSpace(_ticket)) return (false, null, "No ticket"); try { using var m = new HttpRequestMessage(HttpMethod.Get, url); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ticket); var r = await Http.SendAsync(m, ct).ConfigureAwait(false); if (!r.IsSuccessStatusCode) return (false, null, r.ReasonPhrase); var b = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return (true, JsonSerializer.Deserialize<T>(b, JsonOptions), null); } catch (Exception e) { return (false, null, e.Message); } }

    private static HttpClient CreateHttpClient() { var h = new HttpClientHandler(); if (h.SupportsAutomaticDecompression) h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) }; }
    private static string ResolveGateUrl()
    {
        var veilnetFunctions = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(veilnetFunctions))
            return veilnetFunctions.TrimEnd('/');

        var legacyGate = (Environment.GetEnvironmentVariable("LV_GATE_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(legacyGate) && IsSupabaseFunctionsEndpoint(legacyGate))
            return legacyGate.TrimEnd('/');

        return DefaultGateUrl;
    }
    private static bool ParseBool(string? v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    private void InvalidateTicket() { _ticket = ""; _ticketExpiresUtc = DateTime.MinValue; }
    private bool HasUsableTicketForCurrentIdentity() => HasValidTicket && string.Equals(_ticketProductUserId, EosClientProvider.Current?.LocalProductUserId ?? "", StringComparison.Ordinal);

    private static bool ContainsAny(string source, params string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (source.Contains(tokens[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryComputeExecutableHash(string? target, out string executablePath, out string hash)
    {
        executablePath = (target ?? string.Empty).Trim();
        hash = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath))
            executablePath = Hashing.ResolveCurrentProcessExecutablePath() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return false;

        try
        {
            hash = Hashing.Sha256File(executablePath);
            return !string.IsNullOrWhiteSpace(hash);
        }
        catch
        {
            return false;
        }
    }
    private void TryRestorePreAuthorizedTicketFromEnvironment() { }

    private static bool IsHashMismatchBody(string body)
    {
        return ContainsAny(
            body ?? string.Empty,
            "hash_mismatch",
            "UNOFFICIAL_BUILD",
            "\"error\":\"hash_mismatch\"",
            "\"error\":\"UNOFFICIAL_BUILD\"");
    }

    private static void ApplyEosConfigFromTicket(GateTicketEosPayload? eos)
    {
        if (eos == null)
            return;

        static void SetIfPresent(string key, string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(v))
                Environment.SetEnvironmentVariable(key, v);
        }

        SetIfPresent("EOS_PRODUCT_ID", eos.ProductId);
        SetIfPresent("EOS_SANDBOX_ID", eos.SandboxId);
        SetIfPresent("EOS_DEPLOYMENT_ID", eos.DeploymentId);
        SetIfPresent("EOS_CLIENT_ID", eos.ClientId);
        SetIfPresent("EOS_PRODUCT_NAME", eos.ProductName);
        SetIfPresent("EOS_PRODUCT_VERSION", eos.ProductVersion);
    }

    private class GateTicketResponse
    {
        public bool Ok { get; set; }
        public string? Ticket { get; set; }
        public string? ExpiresUtc { get; set; }
        public string? ExpiresAt { get; set; }
        public string? Reason { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
        public GateTicketEosPayload? Eos { get; set; }
    }
    private class GateTicketEosPayload
    {
        public string? ProductId { get; set; }
        public string? SandboxId { get; set; }
        public string? DeploymentId { get; set; }
        public string? ClientId { get; set; }
        public string? ProductName { get; set; }
        public string? ProductVersion { get; set; }
    }
    private class GatePresenceQueryRequest { public List<string> ProductUserIds { get; set; } = new(); }
    private class GatePresenceQueryResponse { public bool Ok { get; set; } public List<GatePresenceEntry>? Entries { get; set; } }
    private class GatePresenceUpsertRequest { public string? ProductUserId { get; set; } public string? DisplayName { get; set; } public bool IsHosting { get; set; } public string? WorldName { get; set; } public string? GameMode { get; set; } public string? JoinTarget { get; set; } public string? Status { get; set; } public bool Cheats { get; set; } public int PlayerCount { get; set; } public int MaxPlayers { get; set; } }
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
    private class GateTicketValidateRequest
    {
        public string Ticket { get; set; } = "";
        public string RequiredChannel { get; set; } = "release";
    }
    private class GateTicketValidateResponse { public bool Ok { get; set; } public string? Reason { get; set; } }
}

public class GateIdentityUser { public string ProductUserId { get; set; } = ""; public string Username { get; set; } = ""; public string DisplayName { get; set; } = ""; public string FriendCode { get; set; } = ""; }
public class GatePresenceEntry { public string ProductUserId { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Status { get; set; } = ""; public bool IsHosting { get; set; } public string WorldName { get; set; } = ""; public string GameMode { get; set; } = ""; public string JoinTarget { get; set; } = ""; public bool Cheats { get; set; } public int PlayerCount { get; set; } public int MaxPlayers { get; set; } public string FriendCode { get; set; } = ""; }
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
