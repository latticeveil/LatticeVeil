using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

sealed class IdentityRegistryProvider
{
    private const string DefaultRegex = "^[A-Za-z0-9_]+$";
    private const int DefaultUsernameMin = 3;
    private const int DefaultUsernameMax = 16;
    private const int DefaultRefreshSeconds = 30;

    private const string FriendCodePrefix = "RC-";
    private const string FriendCodeSalt = "RC-FRIENDCODE-V1";
    private const string FriendCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int FriendCodeLength = 8;
    private const int MaxFriendsPerUser = 256;
    private const int MaxIncomingRequestsPerUser = 256;
    private const int MaxBlockedUsersPerUser = 256;
    private const int RecoveryCodeLength = 20;
    private const int RecoveryLockThreshold = 5;
    private static readonly TimeSpan RecoveryLockDuration = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private readonly string _source;
    private readonly string _githubRepo;
    private readonly string _githubPath;
    private readonly string _githubBranch;
    private readonly string _githubToken;
    private readonly TimeSpan _refreshInterval;

    private readonly int _usernameMin;
    private readonly int _usernameMax;
    private readonly string _usernameRegexPattern;
    private readonly Regex _usernameRegex;

    private IdentityRegistryDocument _document = IdentityRegistryDocument.Empty();
    private string _documentSha = string.Empty;
    private DateTime _documentLoadedUtc = DateTime.MinValue;

    public IdentityRegistryProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _source = (Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_SOURCE") ?? "github").Trim().ToLowerInvariant();
        _githubRepo = (Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_REPO") ?? string.Empty).Trim();
        _githubPath = (Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_PATH") ?? "identity-registry.json").Trim();
        _githubBranch = (Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_BRANCH") ?? "main").Trim();
        _githubToken = (Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_GITHUB_TOKEN") ?? string.Empty).Trim();
        _refreshInterval = TimeSpan.FromSeconds(ParsePositiveInt(
            Environment.GetEnvironmentVariable("IDENTITY_REGISTRY_REFRESH_SECONDS"),
            DefaultRefreshSeconds));

        _usernameMin = ParsePositiveInt(Environment.GetEnvironmentVariable("IDENTITY_USERNAME_MIN"), DefaultUsernameMin);
        _usernameMax = ParsePositiveInt(Environment.GetEnvironmentVariable("IDENTITY_USERNAME_MAX"), DefaultUsernameMax);
        if (_usernameMax < _usernameMin)
            _usernameMax = _usernameMin;

        _usernameRegexPattern = (Environment.GetEnvironmentVariable("IDENTITY_USERNAME_REGEX") ?? DefaultRegex).Trim();
        try
        {
            _usernameRegex = new Regex(
                string.IsNullOrWhiteSpace(_usernameRegexPattern) ? DefaultRegex : _usernameRegexPattern,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            _usernameRegex = new Regex(DefaultRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            _usernameRegexPattern = DefaultRegex;
        }
    }

    public IdentityUsernameRules GetUsernameRules()
    {
        return new IdentityUsernameRules(_usernameMin, _usernameMax, _usernameRegexPattern);
    }

    public bool TryNormalizeUsername(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var candidate = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Username is required.";
            return false;
        }

        if (candidate.Length < _usernameMin || candidate.Length > _usernameMax)
        {
            error = $"Username must be {_usernameMin}-{_usernameMax} characters.";
            return false;
        }

        if (!_usernameRegex.IsMatch(candidate))
        {
            error = $"Username must match regex: {_usernameRegexPattern}";
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    public async Task<IdentityResolveResult> ResolveAsync(string? query, CancellationToken ct)
    {
        var value = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new IdentityResolveResult(false, null, "Query is required.");

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityResolveResult(false, null, load.Error);

            var user = TryResolveUnsafe(value);
            if (user == null)
                return new IdentityResolveResult(false, null, "No identity found.");

            return new IdentityResolveResult(true, user, string.Empty);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityResolvedUser?> GetByProductUserIdAsync(string? productUserId, CancellationToken ct)
    {
        var normalizedPuid = NormalizeProductUserId(productUserId);
        if (string.IsNullOrWhiteSpace(normalizedPuid))
            return null;

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return null;

            var entry = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, normalizedPuid, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return null;

            return ToResolvedUser(entry);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendsResult> GetFriendsAsync(string? ownerProductUserId, CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
        {
            return new IdentityFriendsResult(
                false,
                "Owner ProductUserId is required.",
                Array.Empty<IdentityResolvedUser>(),
                Array.Empty<IdentityFriendRequestInfo>(),
                Array.Empty<IdentityFriendRequestInfo>(),
                Array.Empty<IdentityResolvedUser>());
        }

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
            {
                return new IdentityFriendsResult(
                    false,
                    load.Error,
                    Array.Empty<IdentityResolvedUser>(),
                    Array.Empty<IdentityFriendRequestInfo>(),
                    Array.Empty<IdentityFriendRequestInfo>(),
                    Array.Empty<IdentityResolvedUser>());
            }

            var ids = GetFriendIdsUnsafe(ownerPuid);
            var resolved = new List<IdentityResolvedUser>(ids.Count);
            for (var i = 0; i < ids.Count; i++)
            {
                var user = BuildResolvedUserUnsafe(ids[i]);
                if (user != null)
                    resolved.Add(user);
            }

            var incomingRequests = GetIncomingRequestsUnsafe(ownerPuid);
            var outgoingRequests = GetOutgoingRequestsUnsafe(ownerPuid);
            var blockedIds = GetBlockedIdsUnsafe(ownerPuid);
            var blockedUsers = new List<IdentityResolvedUser>(blockedIds.Count);
            for (var i = 0; i < blockedIds.Count; i++)
            {
                var blockedUser = BuildResolvedUserUnsafe(blockedIds[i]);
                if (blockedUser != null)
                    blockedUsers.Add(blockedUser);
            }

            return new IdentityFriendsResult(
                true,
                "ok",
                resolved,
                incomingRequests,
                outgoingRequests,
                blockedUsers);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendMutationResult> AddFriendByQueryAsync(
        string? ownerProductUserId,
        string? query,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityFriendMutationResult(false, "Owner ProductUserId is required.", null, 0);

        var value = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new IdentityFriendMutationResult(false, "Friend query is required.", null, 0);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityFriendMutationResult(false, load.Error, null, 0);

            var resolved = TryResolveUnsafe(value);
            var targetPuid = resolved?.ProductUserId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetPuid))
            {
                // Support direct ProductUserId adds without requiring a reserved username claim.
                // Keep this strict enough to avoid accidental username typos becoming unresolved IDs.
                if (value.StartsWith(FriendCodePrefix, StringComparison.OrdinalIgnoreCase))
                    return new IdentityFriendMutationResult(false, "Friend code was not found.", null, 0);
                if (value.Length <= _usernameMax)
                    return new IdentityFriendMutationResult(false, "Username was not found.", null, 0);
                targetPuid = NormalizeProductUserId(value);
                if (string.IsNullOrWhiteSpace(targetPuid))
                    return new IdentityFriendMutationResult(false, "Target ProductUserId is invalid.", null, 0);
            }

            if (string.Equals(ownerPuid, targetPuid, StringComparison.OrdinalIgnoreCase))
                return new IdentityFriendMutationResult(false, "You cannot add yourself as a friend.", null, GetFriendIdsUnsafe(ownerPuid).Count);

            if (IsBlockedUnsafe(ownerPuid, targetPuid))
                return new IdentityFriendMutationResult(false, "Unblock this user before sending a friend request.", BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);

            if (IsBlockedUnsafe(targetPuid, ownerPuid))
                return new IdentityFriendMutationResult(false, "This player has blocked you.", BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);

            var ownerFriends = GetFriendIdsUnsafe(ownerPuid);
            if (ownerFriends.Any(x => string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase)))
            {
                return new IdentityFriendMutationResult(true, "Friend is already in your list.", BuildResolvedUserUnsafe(targetPuid), ownerFriends.Count);
            }

            var changed = false;
            var message = "Friend request sent.";
            if (HasIncomingRequestUnsafe(ownerPuid, targetPuid))
            {
                RemoveIncomingRequestUnsafe(ownerPuid, targetPuid, out var removedIncoming);
                var addA = AddFriendUnsafe(ownerPuid, targetPuid, out _, out var changedA, out _);
                var addB = AddFriendUnsafe(targetPuid, ownerPuid, out _, out var changedB, out _);
                changed = removedIncoming || changedA || changedB;
                if (!addA || !addB)
                    return new IdentityFriendMutationResult(false, "Could not accept pending friend request.", BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);
                message = "Friend request accepted.";
            }
            else if (HasIncomingRequestUnsafe(targetPuid, ownerPuid))
            {
                message = "Friend request already sent.";
            }
            else
            {
                var addRequest = AddIncomingRequestUnsafe(targetPuid, ownerPuid, out changed, out _, out var requestMessage);
                if (!addRequest)
                    return new IdentityFriendMutationResult(false, requestMessage, BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);
            }

            if (changed)
            {
                var save = await SaveUnsafe($"friends: request {ShortId(ownerPuid)} -> {ShortId(targetPuid)}", ct);
                if (!save.Ok)
                    return new IdentityFriendMutationResult(false, save.Error, BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);
            }

            return new IdentityFriendMutationResult(true, message, BuildResolvedUserUnsafe(targetPuid), GetFriendIdsUnsafe(ownerPuid).Count);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendMutationResult> RemoveFriendAsync(
        string? ownerProductUserId,
        string? targetProductUserId,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        var targetPuid = NormalizeProductUserId(targetProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityFriendMutationResult(false, "Owner ProductUserId is required.", null, 0);
        if (string.IsNullOrWhiteSpace(targetPuid))
            return new IdentityFriendMutationResult(false, "Friend ProductUserId is required.", null, 0);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityFriendMutationResult(false, load.Error, null, 0);

            var list = GetOrCreateFriendListUnsafe(ownerPuid);
            var before = list.Friends.Count;
            list.Friends = list.Friends
                .Where(x => !string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var changed = list.Friends.Count != before;
            list.UpdatedUtc = DateTime.UtcNow;
            _document.FriendLists[ownerPuid] = list;

            if (changed)
            {
                var save = await SaveUnsafe($"friends: remove {ShortId(targetPuid)} from {ShortId(ownerPuid)}", ct);
                if (!save.Ok)
                    return new IdentityFriendMutationResult(false, save.Error, BuildResolvedUserUnsafe(targetPuid), list.Friends.Count);
            }

            return new IdentityFriendMutationResult(
                true,
                changed ? "Friend removed." : "Friend was not in list.",
                BuildResolvedUserUnsafe(targetPuid),
                list.Friends.Count);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendMutationResult> RespondToFriendRequestAsync(
        string? ownerProductUserId,
        string? requesterProductUserId,
        bool accept,
        bool block,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        var requesterPuid = NormalizeProductUserId(requesterProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityFriendMutationResult(false, "Owner ProductUserId is required.", null, 0);
        if (string.IsNullOrWhiteSpace(requesterPuid))
            return new IdentityFriendMutationResult(false, "Requester ProductUserId is required.", null, 0);
        if (string.Equals(ownerPuid, requesterPuid, StringComparison.OrdinalIgnoreCase))
            return new IdentityFriendMutationResult(false, "Cannot respond to a request from yourself.", null, GetFriendIdsUnsafe(ownerPuid).Count);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityFriendMutationResult(false, load.Error, null, 0);

            RemoveIncomingRequestUnsafe(ownerPuid, requesterPuid, out var requestRemoved);
            if (!requestRemoved)
                return new IdentityFriendMutationResult(false, "Friend request was not found.", BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);

            var changed = true;
            string message;
            if (block)
            {
                AddBlockedUserUnsafe(ownerPuid, requesterPuid, out var blockChanged, out _);
                changed |= blockChanged;
                changed |= RemoveFriendLinkUnsafe(ownerPuid, requesterPuid);
                changed |= RemoveFriendLinkUnsafe(requesterPuid, ownerPuid);
                RemoveIncomingRequestUnsafe(requesterPuid, ownerPuid, out var reverseRequestRemoved);
                changed |= reverseRequestRemoved;
                message = "Request denied and user blocked.";
            }
            else if (accept)
            {
                if (IsBlockedUnsafe(ownerPuid, requesterPuid))
                    return new IdentityFriendMutationResult(false, "Unblock this user before accepting.", BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);
                if (IsBlockedUnsafe(requesterPuid, ownerPuid))
                    return new IdentityFriendMutationResult(false, "Cannot accept. This player has blocked you.", BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);

                var addA = AddFriendUnsafe(ownerPuid, requesterPuid, out var addMessageA, out var changedA, out _);
                var addB = AddFriendUnsafe(requesterPuid, ownerPuid, out var addMessageB, out var changedB, out _);
                if (!addA || !addB)
                {
                    var failure = !string.IsNullOrWhiteSpace(addMessageA) ? addMessageA : addMessageB;
                    return new IdentityFriendMutationResult(false, failure, BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);
                }

                changed |= changedA || changedB;
                message = "Friend request accepted.";
            }
            else
            {
                message = "Friend request denied.";
            }

            if (changed)
            {
                var save = await SaveUnsafe($"friends: respond {ShortId(ownerPuid)} <- {ShortId(requesterPuid)}", ct);
                if (!save.Ok)
                    return new IdentityFriendMutationResult(false, save.Error, BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);
            }

            return new IdentityFriendMutationResult(true, message, BuildResolvedUserUnsafe(requesterPuid), GetFriendIdsUnsafe(ownerPuid).Count);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendMutationResult> BlockByQueryAsync(
        string? ownerProductUserId,
        string? query,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        var value = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityFriendMutationResult(false, "Owner ProductUserId is required.", null, 0);
        if (string.IsNullOrWhiteSpace(value))
            return new IdentityFriendMutationResult(false, "Block query is required.", null, GetBlockedIdsUnsafe(ownerPuid).Count);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityFriendMutationResult(false, load.Error, null, 0);

            var resolved = TryResolveUnsafe(value);
            var targetPuid = resolved?.ProductUserId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetPuid))
            {
                targetPuid = NormalizeProductUserId(value);
                if (string.IsNullOrWhiteSpace(targetPuid))
                    return new IdentityFriendMutationResult(false, "Target ProductUserId is invalid.", null, GetBlockedIdsUnsafe(ownerPuid).Count);
            }

            if (string.Equals(ownerPuid, targetPuid, StringComparison.OrdinalIgnoreCase))
                return new IdentityFriendMutationResult(false, "You cannot block yourself.", null, GetBlockedIdsUnsafe(ownerPuid).Count);

            AddBlockedUserUnsafe(ownerPuid, targetPuid, out var blockChanged, out var blockCount);
            var changed = blockChanged;
            changed |= RemoveFriendLinkUnsafe(ownerPuid, targetPuid);
            changed |= RemoveFriendLinkUnsafe(targetPuid, ownerPuid);
            RemoveIncomingRequestUnsafe(ownerPuid, targetPuid, out var removedIncoming);
            changed |= removedIncoming;
            RemoveIncomingRequestUnsafe(targetPuid, ownerPuid, out var removedOutgoing);
            changed |= removedOutgoing;

            if (changed)
            {
                var save = await SaveUnsafe($"friends: block {ShortId(ownerPuid)} -> {ShortId(targetPuid)}", ct);
                if (!save.Ok)
                    return new IdentityFriendMutationResult(false, save.Error, BuildResolvedUserUnsafe(targetPuid), blockCount);
            }

            var message = blockChanged ? "User blocked." : "User already blocked.";
            return new IdentityFriendMutationResult(true, message, BuildResolvedUserUnsafe(targetPuid), blockCount);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityFriendMutationResult> UnblockAsync(
        string? ownerProductUserId,
        string? targetProductUserId,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        var targetPuid = NormalizeProductUserId(targetProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityFriendMutationResult(false, "Owner ProductUserId is required.", null, 0);
        if (string.IsNullOrWhiteSpace(targetPuid))
            return new IdentityFriendMutationResult(false, "Target ProductUserId is required.", null, GetBlockedIdsUnsafe(ownerPuid).Count);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityFriendMutationResult(false, load.Error, null, 0);

            var changed = RemoveBlockedUserUnsafe(ownerPuid, targetPuid);
            var count = GetBlockedIdsUnsafe(ownerPuid).Count;
            if (changed)
            {
                var save = await SaveUnsafe($"friends: unblock {ShortId(ownerPuid)} -> {ShortId(targetPuid)}", ct);
                if (!save.Ok)
                    return new IdentityFriendMutationResult(false, save.Error, BuildResolvedUserUnsafe(targetPuid), count);
            }

            return new IdentityFriendMutationResult(
                true,
                changed ? "User unblocked." : "User was not blocked.",
                BuildResolvedUserUnsafe(targetPuid),
                count);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityRecoveryRotateResult> RotateRecoveryCodeAsync(
        string? ownerProductUserId,
        CancellationToken ct)
    {
        var ownerPuid = NormalizeProductUserId(ownerProductUserId);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new IdentityRecoveryRotateResult(false, "Owner ProductUserId is required.", string.Empty);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityRecoveryRotateResult(false, load.Error, string.Empty);

            var owner = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, ownerPuid, StringComparison.OrdinalIgnoreCase));
            if (owner == null)
                return new IdentityRecoveryRotateResult(false, "Claim a reserved username first, then rotate a recovery code.", string.Empty);

            var plaintext = GenerateRecoveryCode();
            var normalized = NormalizeRecoveryCode(plaintext);
            var salt = GenerateRecoverySalt();
            var hash = ComputeRecoveryCodeHashHex(salt, normalized);
            _document.Recovery[ownerPuid] = new IdentityRecoveryEntry
            {
                CodeHash = hash,
                CodeSalt = salt,
                RotatedUtc = DateTime.UtcNow,
                FailedAttempts = 0,
                LockUntilUtc = null
            };
            _document.UpdatedUtc = DateTime.UtcNow;

            var save = await SaveUnsafe($"identity: rotate recovery for {ShortId(ownerPuid)}", ct);
            if (!save.Ok)
                return new IdentityRecoveryRotateResult(false, save.Error, string.Empty);

            return new IdentityRecoveryRotateResult(true, "Recovery code rotated.", plaintext);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityTransferResult> TransferIdentityAsync(
        string? oldUsernameOrCode,
        string? recoveryCode,
        string? newProductUserId,
        CancellationToken ct)
    {
        var query = (oldUsernameOrCode ?? string.Empty).Trim();
        var newPuid = NormalizeProductUserId(newProductUserId);
        if (string.IsNullOrWhiteSpace(query))
            return new IdentityTransferResult(false, "Identity query is required.", null);
        if (string.IsNullOrWhiteSpace(newPuid))
            return new IdentityTransferResult(false, "Destination ProductUserId is required.", null);

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityTransferResult(false, load.Error, null);

            var source = TryResolveUnsafe(query);
            if (source == null)
                return new IdentityTransferResult(false, "Source identity was not found.", null);

            var oldPuid = NormalizeProductUserId(source.ProductUserId);
            if (string.IsNullOrWhiteSpace(oldPuid))
                return new IdentityTransferResult(false, "Source identity is invalid.", null);

            var oldEntry = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, oldPuid, StringComparison.OrdinalIgnoreCase));
            if (oldEntry == null)
                return new IdentityTransferResult(false, "Source reserved username is not available.", null);

            var codeCheck = VerifyRecoveryCodeUnsafe(oldPuid, recoveryCode);
            if (!codeCheck.Ok)
            {
                if (codeCheck.StateChanged)
                    await SaveUnsafe($"identity: failed recovery transfer for {ShortId(oldPuid)}", ct);
                return new IdentityTransferResult(false, codeCheck.Message, null);
            }

            var now = DateTime.UtcNow;
            var changed = codeCheck.StateChanged;
            IdentityRegistryEntry destination;

            if (string.Equals(oldPuid, newPuid, StringComparison.OrdinalIgnoreCase))
            {
                destination = oldEntry;
            }
            else
            {
                destination = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, newPuid, StringComparison.OrdinalIgnoreCase))
                    ?? new IdentityRegistryEntry { ProductUserId = newPuid };

                if (!_document.Users.Contains(destination))
                {
                    _document.Users.Add(destination);
                    changed = true;
                }

                destination.Username = oldEntry.Username;
                destination.DisplayName = oldEntry.DisplayName;
                destination.UpdatedUtc = now;
                changed = true;

                _document.Users.Remove(oldEntry);
                changed = true;

                MigrateFriendListsUnsafe(oldPuid, newPuid);
                changed = true;

                if (_document.Recovery.TryGetValue(oldPuid, out var recovery))
                {
                    recovery.FailedAttempts = 0;
                    recovery.LockUntilUtc = null;
                    _document.Recovery[newPuid] = recovery;
                    _document.Recovery.Remove(oldPuid);
                    changed = true;
                }
            }

            if (changed)
            {
                _document.UpdatedUtc = now;
                NormalizeDocumentUnsafe();
                var save = await SaveUnsafe($"identity: transfer {ShortId(oldPuid)} -> {ShortId(newPuid)}", ct);
                if (!save.Ok)
                    return new IdentityTransferResult(false, save.Error, BuildResolvedUserUnsafe(newPuid));
            }

            return new IdentityTransferResult(true, "Identity transfer complete.", BuildResolvedUserUnsafe(newPuid));
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityClaimResult> ClaimAsync(
        string? productUserId,
        string? username,
        string? displayName,
        bool allowReassign,
        CancellationToken ct)
    {
        var puid = NormalizeProductUserId(productUserId);
        if (string.IsNullOrWhiteSpace(puid))
            return new IdentityClaimResult(false, "ProductUserId is required.", "invalid_product_user_id", null);

        if (!TryNormalizeUsername(username, out var normalizedUsername, out var usernameError))
            return new IdentityClaimResult(false, usernameError, "invalid_username", null);

        var normalizedDisplay = NormalizeDisplayName(displayName, normalizedUsername);

        await _sync.WaitAsync(ct);
        try
        {
            var lastSaveError = string.Empty;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var load = await EnsureLoadedUnsafe(ct, forceRefresh: attempt > 0);
                if (!load.Ok)
                    return new IdentityClaimResult(false, load.Error, "load_failed", null);

                var conflict = _document.Users.FirstOrDefault(
                    x => string.Equals(x.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(x.ProductUserId, puid, StringComparison.OrdinalIgnoreCase));
                if (conflict != null && !allowReassign)
                    return new IdentityClaimResult(false, "Username already reserved.", "username_conflict", ToResolvedUser(conflict));

                if (conflict != null && allowReassign)
                    _document.Users.Remove(conflict);

                var existing = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, puid, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new IdentityRegistryEntry
                    {
                        ProductUserId = puid,
                        Username = normalizedUsername,
                        DisplayName = normalizedDisplay,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _document.Users.Add(existing);
                }
                else
                {
                    existing.Username = normalizedUsername;
                    existing.DisplayName = normalizedDisplay;
                    existing.UpdatedUtc = DateTime.UtcNow;
                }

                _document.UpdatedUtc = DateTime.UtcNow;
                NormalizeDocumentUnsafe();

                var save = await SaveUnsafe(
                    $"identity: reserve '{normalizedUsername}' for {ShortId(puid)}",
                    ct);
                if (save.Ok)
                    return new IdentityClaimResult(true, "Username reserved.", "ok", ToResolvedUser(existing));

                lastSaveError = save.Error;
                if (!save.Retryable)
                    return new IdentityClaimResult(false, save.Error, "save_failed", ToResolvedUser(existing));
            }

            var reason = string.IsNullOrWhiteSpace(lastSaveError)
                ? "Could not save identity registry after retries."
                : $"Could not save identity registry after retries. {lastSaveError}";
            return new IdentityClaimResult(false, reason, "save_retry_exhausted", null);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IdentityAdminResult> ReassignAsync(
        string? productUserId,
        string? username,
        string? displayName,
        CancellationToken ct)
    {
        var claim = await ClaimAsync(productUserId, username, displayName, allowReassign: true, ct);
        return new IdentityAdminResult(claim.Ok, claim.Message, claim.User);
    }

    public async Task<IdentityAdminResult> RemoveAsync(
        string? productUserId,
        string? username,
        CancellationToken ct)
    {
        var puid = NormalizeProductUserId(productUserId);
        var normalizedUsername = (username ?? string.Empty).Trim().ToLowerInvariant();

        await _sync.WaitAsync(ct);
        try
        {
            var load = await EnsureLoadedUnsafe(ct, forceRefresh: false);
            if (!load.Ok)
                return new IdentityAdminResult(false, load.Error, null);

            var removed = default(IdentityRegistryEntry);
            if (!string.IsNullOrWhiteSpace(puid))
            {
                removed = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, puid, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrWhiteSpace(normalizedUsername))
            {
                removed = _document.Users.FirstOrDefault(x => string.Equals(x.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
            }

            if (removed == null)
                return new IdentityAdminResult(false, "Identity entry not found.", null);

            _document.Users.Remove(removed);
            _document.FriendLists.Remove(removed.ProductUserId);
            _document.FriendRequests.Remove(removed.ProductUserId);
            _document.BlockLists.Remove(removed.ProductUserId);
            _document.Recovery.Remove(removed.ProductUserId);

            var socialKey = NormalizeProductUserId(removed.ProductUserId);
            foreach (var owner in _document.FriendLists.Keys.ToArray())
            {
                RemoveFriendLinkUnsafe(owner, socialKey);
            }

            foreach (var owner in _document.FriendRequests.Keys.ToArray())
            {
                RemoveIncomingRequestUnsafe(owner, socialKey, out _);
            }

            foreach (var owner in _document.BlockLists.Keys.ToArray())
            {
                RemoveBlockedUserUnsafe(owner, socialKey);
            }

            _document.UpdatedUtc = DateTime.UtcNow;

            var save = await SaveUnsafe($"identity: remove {ShortId(removed.ProductUserId)}", ct);
            if (!save.Ok)
                return new IdentityAdminResult(false, save.Error, ToResolvedUser(removed));

            return new IdentityAdminResult(true, "Identity removed.", ToResolvedUser(removed));
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<ProviderResult> ForceRefreshAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var result = await EnsureLoadedUnsafe(ct, forceRefresh: true);
            return result;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<ProviderResult> EnsureLoadedUnsafe(CancellationToken ct, bool forceRefresh)
    {
        if (!forceRefresh && DateTime.UtcNow - _documentLoadedUtc < _refreshInterval)
            return ProviderResult.Success();

        if (!string.Equals(_source, "github", StringComparison.OrdinalIgnoreCase))
        {
            _documentLoadedUtc = DateTime.UtcNow;
            NormalizeDocumentUnsafe();
            return ProviderResult.Success();
        }

        var load = await LoadFromGithubUnsafe(ct);
        if (load.Ok)
        {
            _document = load.Document ?? IdentityRegistryDocument.Empty();
            _documentSha = load.Sha ?? string.Empty;
            _documentLoadedUtc = DateTime.UtcNow;
            NormalizeDocumentUnsafe();
            return ProviderResult.Success();
        }

        if (_document.Users.Count > 0)
        {
            _documentLoadedUtc = DateTime.UtcNow;
            return ProviderResult.Success();
        }

        return ProviderResult.Fail(load.Error ?? "Identity registry could not be loaded.");
    }

    private async Task<(bool Ok, IdentityRegistryDocument? Document, string? Sha, string? Error)> LoadFromGithubUnsafe(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_githubRepo) || string.IsNullOrWhiteSpace(_githubPath) || string.IsNullOrWhiteSpace(_githubToken))
        {
            return (false, null, null, "Identity registry GitHub settings are incomplete.");
        }

        var client = _httpClientFactory.CreateClient();
        var endpoint = BuildGithubGetContentsEndpoint(_githubRepo, _githubPath, _githubBranch);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        request.Headers.UserAgent.ParseAdd("LatticeVeilGate/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (true, IdentityRegistryDocument.Empty(), string.Empty, string.Empty);

        if (!response.IsSuccessStatusCode)
            return (false, null, null, $"GitHub registry load failed: HTTP {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<GithubContentsResponse>(body, JsonOptions);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Content))
            return (false, null, null, "GitHub registry response missing content.");

        try
        {
            var base64 = parsed.Content.Replace("\n", string.Empty).Replace("\r", string.Empty).Trim();
            var bytes = Convert.FromBase64String(base64);
            var json = Encoding.UTF8.GetString(bytes);
            var doc = JsonSerializer.Deserialize<IdentityRegistryDocument>(json, JsonOptions) ?? IdentityRegistryDocument.Empty();
            return (true, doc, parsed.Sha ?? string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, null, $"GitHub registry decode failed: {ex.Message}");
        }
    }

    private async Task<SaveResult> SaveUnsafe(string message, CancellationToken ct)
    {
        _document.UpdatedUtc = DateTime.UtcNow;
        NormalizeDocumentUnsafe();

        if (!string.Equals(_source, "github", StringComparison.OrdinalIgnoreCase))
        {
            _documentLoadedUtc = DateTime.UtcNow;
            return SaveResult.Success(_documentSha);
        }

        if (string.IsNullOrWhiteSpace(_githubRepo) || string.IsNullOrWhiteSpace(_githubPath) || string.IsNullOrWhiteSpace(_githubToken))
            return SaveResult.Fail("Identity registry GitHub settings are incomplete.", retryable: false);

        var payload = JsonSerializer.Serialize(_document, new JsonSerializerOptions { WriteIndented = true });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var endpoint = BuildGithubPutContentsEndpoint(_githubRepo, _githubPath);
        SaveResult lastFailure = SaveResult.Fail("Unknown save failure.", retryable: false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0 || string.IsNullOrWhiteSpace(_documentSha))
            {
                // GitHub requires `sha` for updates when the file already exists.
                var latest = await LoadFromGithubUnsafe(ct);
                if (latest.Ok && !string.IsNullOrWhiteSpace(latest.Sha))
                    _documentSha = latest.Sha!;
            }

            var requestBody = new GithubUpdateRequest
            {
                Message = message,
                Content = encoded,
                Branch = _githubBranch,
                Sha = string.IsNullOrWhiteSpace(_documentSha) ? null : _documentSha
            };

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
            request.Headers.UserAgent.ParseAdd("LatticeVeilGate/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var details = (body ?? string.Empty).Trim();
                if (details.Length > 400)
                    details = details[..400];
                var retryable = IsRetryableSaveFailure(response.StatusCode, details);
                if (!string.IsNullOrWhiteSpace(details))
                    lastFailure = SaveResult.Fail($"GitHub registry save failed: HTTP {(int)response.StatusCode}. {details}", retryable);
                else
                    lastFailure = SaveResult.Fail($"GitHub registry save failed: HTTP {(int)response.StatusCode}", retryable);

                if (!retryable || attempt >= 2)
                    return lastFailure;

                continue;
            }

            var parsed = JsonSerializer.Deserialize<GithubUpdateResponse>(body, JsonOptions);
            var newSha = parsed?.Content?.Sha ?? _documentSha;
            _documentSha = newSha;
            _documentLoadedUtc = DateTime.UtcNow;
            return SaveResult.Success(newSha);
        }

        return lastFailure;
    }

    private IdentityResolvedUser? TryResolveUnsafe(string query)
    {
        var value = query.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        IdentityRegistryEntry? entry = null;
        if (value.StartsWith(FriendCodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            entry = _document.Users.FirstOrDefault(
                x => string.Equals(GenerateFriendCode(x.ProductUserId), value, StringComparison.OrdinalIgnoreCase));
        }

        entry ??= _document.Users.FirstOrDefault(
            x => string.Equals(x.ProductUserId, value, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            var normalizedUsername = value.ToLowerInvariant();
            entry = _document.Users.FirstOrDefault(
                x => string.Equals(x.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
        }

        return entry == null ? null : ToResolvedUser(entry);
    }

    private void NormalizeDocumentUnsafe()
    {
        _document.Version = _document.Version <= 0 ? 1 : _document.Version;
        _document.Users ??= new List<IdentityRegistryEntry>();
        _document.FriendLists ??= new Dictionary<string, IdentityFriendListEntry>(StringComparer.OrdinalIgnoreCase);
        _document.FriendRequests ??= new Dictionary<string, IdentityFriendRequestInboxEntry>(StringComparer.OrdinalIgnoreCase);
        _document.BlockLists ??= new Dictionary<string, IdentityBlockedListEntry>(StringComparer.OrdinalIgnoreCase);
        _document.Recovery ??= new Dictionary<string, IdentityRecoveryEntry>(StringComparer.OrdinalIgnoreCase);

        var cleaned = new List<IdentityRegistryEntry>(_document.Users.Count);
        var seenPuid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUsername = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in _document.Users)
        {
            if (user == null)
                continue;

            var puid = NormalizeProductUserId(user.ProductUserId);
            if (string.IsNullOrWhiteSpace(puid))
                continue;

            var username = (user.Username ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(username))
                continue;

            if (!seenPuid.Add(puid))
                continue;
            if (!seenUsername.Add(username))
                continue;

            cleaned.Add(new IdentityRegistryEntry
            {
                ProductUserId = puid,
                Username = username,
                DisplayName = NormalizeDisplayName(user.DisplayName, username),
                UpdatedUtc = user.UpdatedUtc == default ? DateTime.UtcNow : user.UpdatedUtc
            });
        }

        _document.Users = cleaned;

        var validUsers = new HashSet<string>(
            _document.Users.Select(x => NormalizeProductUserId(x.ProductUserId))
                .Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var normalizedFriendLists = new Dictionary<string, IdentityFriendListEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _document.FriendLists)
        {
            var ownerPuid = NormalizeProductUserId(pair.Key);
            if (string.IsNullOrWhiteSpace(ownerPuid))
                continue;

            var source = pair.Value ?? new IdentityFriendListEntry();
            var normalizedFriends = NormalizeFriendIds(source.Friends, ownerPuid);
            normalizedFriendLists[ownerPuid] = new IdentityFriendListEntry
            {
                Friends = normalizedFriends,
                UpdatedUtc = source.UpdatedUtc == default ? DateTime.UtcNow : source.UpdatedUtc
            };
        }

        _document.FriendLists = normalizedFriendLists;

        var normalizedFriendRequests = new Dictionary<string, IdentityFriendRequestInboxEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _document.FriendRequests)
        {
            var ownerPuid = NormalizeProductUserId(pair.Key);
            if (string.IsNullOrWhiteSpace(ownerPuid))
                continue;

            var source = pair.Value ?? new IdentityFriendRequestInboxEntry();
            var normalizedIncoming = NormalizeIncomingRequests(source.Incoming, ownerPuid);
            normalizedFriendRequests[ownerPuid] = new IdentityFriendRequestInboxEntry
            {
                Incoming = normalizedIncoming,
                UpdatedUtc = source.UpdatedUtc == default ? DateTime.UtcNow : source.UpdatedUtc
            };
        }
        _document.FriendRequests = normalizedFriendRequests;

        var normalizedBlocks = new Dictionary<string, IdentityBlockedListEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _document.BlockLists)
        {
            var ownerPuid = NormalizeProductUserId(pair.Key);
            if (string.IsNullOrWhiteSpace(ownerPuid))
                continue;

            var source = pair.Value ?? new IdentityBlockedListEntry();
            var normalizedBlocked = NormalizeBlockedIds(source.Blocked, ownerPuid);
            normalizedBlocks[ownerPuid] = new IdentityBlockedListEntry
            {
                Blocked = normalizedBlocked,
                UpdatedUtc = source.UpdatedUtc == default ? DateTime.UtcNow : source.UpdatedUtc
            };
        }
        _document.BlockLists = normalizedBlocks;

        var normalizedRecovery = new Dictionary<string, IdentityRecoveryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _document.Recovery)
        {
            var puid = NormalizeProductUserId(pair.Key);
            if (string.IsNullOrWhiteSpace(puid))
                continue;
            if (!validUsers.Contains(puid))
                continue;

            var source = pair.Value;
            if (source == null
                || string.IsNullOrWhiteSpace(source.CodeHash)
                || string.IsNullOrWhiteSpace(source.CodeSalt))
            {
                continue;
            }

            normalizedRecovery[puid] = new IdentityRecoveryEntry
            {
                CodeHash = source.CodeHash.Trim(),
                CodeSalt = source.CodeSalt.Trim(),
                RotatedUtc = source.RotatedUtc == default ? DateTime.UtcNow : source.RotatedUtc,
                FailedAttempts = Math.Max(0, source.FailedAttempts),
                LockUntilUtc = source.LockUntilUtc
            };
        }

        _document.Recovery = normalizedRecovery;
    }

    private static IdentityResolvedUser ToResolvedUser(IdentityRegistryEntry entry)
    {
        return new IdentityResolvedUser(
            entry.ProductUserId,
            entry.Username,
            string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Username : entry.DisplayName,
            GenerateFriendCode(entry.ProductUserId),
            entry.UpdatedUtc);
    }

    private static string NormalizeProductUserId(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private IdentityResolvedUser? BuildResolvedUserUnsafe(string? productUserId)
    {
        var puid = NormalizeProductUserId(productUserId);
        if (string.IsNullOrWhiteSpace(puid))
            return null;

        var existing = _document.Users.FirstOrDefault(x => string.Equals(x.ProductUserId, puid, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return ToResolvedUser(existing);

        return new IdentityResolvedUser(
            puid,
            string.Empty,
            ShortId(puid),
            GenerateFriendCode(puid),
            DateTime.UtcNow);
    }

    private IdentityFriendListEntry GetOrCreateFriendListUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (!_document.FriendLists.TryGetValue(ownerPuid, out var list) || list == null)
            list = new IdentityFriendListEntry();

        list.Friends = NormalizeFriendIds(list.Friends, ownerPuid);
        _document.FriendLists[ownerPuid] = list;
        return list;
    }

    private List<string> GetFriendIdsUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (!_document.FriendLists.TryGetValue(ownerPuid, out var list) || list == null)
            return new List<string>();

        var normalized = NormalizeFriendIds(list.Friends, ownerPuid);
        if (normalized.Count != list.Friends.Count)
        {
            list.Friends = normalized;
            list.UpdatedUtc = DateTime.UtcNow;
            _document.FriendLists[ownerPuid] = list;
        }

        return normalized;
    }

    private IdentityFriendRequestInboxEntry GetOrCreateRequestInboxUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (!_document.FriendRequests.TryGetValue(ownerPuid, out var inbox) || inbox == null)
            inbox = new IdentityFriendRequestInboxEntry();

        inbox.Incoming = NormalizeIncomingRequests(inbox.Incoming, ownerPuid);
        _document.FriendRequests[ownerPuid] = inbox;
        return inbox;
    }

    private List<IdentityFriendRequestInfo> GetIncomingRequestsUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (!_document.FriendRequests.TryGetValue(ownerPuid, out var inbox) || inbox == null)
            return new List<IdentityFriendRequestInfo>();

        var normalized = NormalizeIncomingRequests(inbox.Incoming, ownerPuid);
        if (normalized.Count != inbox.Incoming.Count)
        {
            inbox.Incoming = normalized;
            inbox.UpdatedUtc = DateTime.UtcNow;
            _document.FriendRequests[ownerPuid] = inbox;
        }

        var list = new List<IdentityFriendRequestInfo>(normalized.Count);
        for (var i = 0; i < normalized.Count; i++)
        {
            var entry = normalized[i];
            var user = BuildResolvedUserUnsafe(entry.FromProductUserId);
            if (user == null)
                continue;
            list.Add(new IdentityFriendRequestInfo(user.ProductUserId, user, entry.CreatedUtc));
        }

        return list
            .OrderByDescending(x => x.RequestedUtc)
            .ToList();
    }

    private List<IdentityFriendRequestInfo> GetOutgoingRequestsUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new List<IdentityFriendRequestInfo>();

        var outgoing = new List<IdentityFriendRequestInfo>();
        foreach (var pair in _document.FriendRequests)
        {
            var recipient = NormalizeProductUserId(pair.Key);
            if (string.IsNullOrWhiteSpace(recipient))
                continue;

            var inbox = pair.Value;
            var normalized = NormalizeIncomingRequests(inbox?.Incoming, recipient);
            for (var i = 0; i < normalized.Count; i++)
            {
                var incoming = normalized[i];
                if (!string.Equals(incoming.FromProductUserId, ownerPuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                var user = BuildResolvedUserUnsafe(recipient);
                if (user == null)
                    continue;
                outgoing.Add(new IdentityFriendRequestInfo(user.ProductUserId, user, incoming.CreatedUtc));
            }
        }

        return outgoing
            .OrderByDescending(x => x.RequestedUtc)
            .ToList();
    }

    private bool HasIncomingRequestUnsafe(string recipientPuid, string senderPuid)
    {
        recipientPuid = NormalizeProductUserId(recipientPuid);
        senderPuid = NormalizeProductUserId(senderPuid);
        if (string.IsNullOrWhiteSpace(recipientPuid) || string.IsNullOrWhiteSpace(senderPuid))
            return false;

        if (!_document.FriendRequests.TryGetValue(recipientPuid, out var inbox) || inbox?.Incoming == null)
            return false;

        return inbox.Incoming.Any(x =>
            string.Equals(NormalizeProductUserId(x.FromProductUserId), senderPuid, StringComparison.OrdinalIgnoreCase));
    }

    private bool AddIncomingRequestUnsafe(
        string recipientPuid,
        string senderPuid,
        out bool changed,
        out int count,
        out string message)
    {
        changed = false;
        count = 0;
        message = string.Empty;
        recipientPuid = NormalizeProductUserId(recipientPuid);
        senderPuid = NormalizeProductUserId(senderPuid);

        if (string.IsNullOrWhiteSpace(recipientPuid) || string.IsNullOrWhiteSpace(senderPuid))
        {
            message = "Friend request is invalid.";
            return false;
        }

        if (string.Equals(recipientPuid, senderPuid, StringComparison.OrdinalIgnoreCase))
        {
            message = "You cannot send a request to yourself.";
            return false;
        }

        var inbox = GetOrCreateRequestInboxUnsafe(recipientPuid);
        if (inbox.Incoming.Any(x => string.Equals(NormalizeProductUserId(x.FromProductUserId), senderPuid, StringComparison.OrdinalIgnoreCase)))
        {
            count = inbox.Incoming.Count;
            message = "Friend request already sent.";
            return true;
        }

        if (inbox.Incoming.Count >= MaxIncomingRequestsPerUser)
        {
            count = inbox.Incoming.Count;
            message = $"Recipient request inbox is full (max {MaxIncomingRequestsPerUser}).";
            return false;
        }

        inbox.Incoming.Add(new IdentityFriendRequestEntry
        {
            FromProductUserId = senderPuid,
            CreatedUtc = DateTime.UtcNow
        });
        inbox.Incoming = NormalizeIncomingRequests(inbox.Incoming, recipientPuid);
        inbox.UpdatedUtc = DateTime.UtcNow;
        _document.FriendRequests[recipientPuid] = inbox;
        changed = true;
        count = inbox.Incoming.Count;
        message = "Friend request sent.";
        return true;
    }

    private bool RemoveIncomingRequestUnsafe(string recipientPuid, string senderPuid, out bool changed)
    {
        changed = false;
        recipientPuid = NormalizeProductUserId(recipientPuid);
        senderPuid = NormalizeProductUserId(senderPuid);
        if (string.IsNullOrWhiteSpace(recipientPuid) || string.IsNullOrWhiteSpace(senderPuid))
            return false;

        if (!_document.FriendRequests.TryGetValue(recipientPuid, out var inbox) || inbox == null)
            return false;

        var before = inbox.Incoming.Count;
        inbox.Incoming = NormalizeIncomingRequests(inbox.Incoming, recipientPuid)
            .Where(x => !string.Equals(NormalizeProductUserId(x.FromProductUserId), senderPuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        changed = inbox.Incoming.Count != before;
        if (changed)
        {
            inbox.UpdatedUtc = DateTime.UtcNow;
            _document.FriendRequests[recipientPuid] = inbox;
        }

        return changed;
    }

    private IdentityBlockedListEntry GetOrCreateBlockedListUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (!_document.BlockLists.TryGetValue(ownerPuid, out var list) || list == null)
            list = new IdentityBlockedListEntry();

        list.Blocked = NormalizeBlockedIds(list.Blocked, ownerPuid);
        _document.BlockLists[ownerPuid] = list;
        return list;
    }

    private List<string> GetBlockedIdsUnsafe(string ownerPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid))
            return new List<string>();
        if (!_document.BlockLists.TryGetValue(ownerPuid, out var list) || list == null)
            return new List<string>();

        var normalized = NormalizeBlockedIds(list.Blocked, ownerPuid);
        if (normalized.Count != list.Blocked.Count)
        {
            list.Blocked = normalized;
            list.UpdatedUtc = DateTime.UtcNow;
            _document.BlockLists[ownerPuid] = list;
        }

        return normalized;
    }

    private bool AddBlockedUserUnsafe(string ownerPuid, string targetPuid, out bool changed, out int count)
    {
        changed = false;
        count = 0;
        ownerPuid = NormalizeProductUserId(ownerPuid);
        targetPuid = NormalizeProductUserId(targetPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid) || string.IsNullOrWhiteSpace(targetPuid))
            return false;
        if (string.Equals(ownerPuid, targetPuid, StringComparison.OrdinalIgnoreCase))
            return false;

        var list = GetOrCreateBlockedListUnsafe(ownerPuid);
        if (list.Blocked.Any(x => string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase)))
        {
            count = list.Blocked.Count;
            return true;
        }

        if (list.Blocked.Count >= MaxBlockedUsersPerUser)
        {
            count = list.Blocked.Count;
            return false;
        }

        list.Blocked.Add(targetPuid);
        list.Blocked = list.Blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.UpdatedUtc = DateTime.UtcNow;
        _document.BlockLists[ownerPuid] = list;
        changed = true;
        count = list.Blocked.Count;
        return true;
    }

    private bool RemoveBlockedUserUnsafe(string ownerPuid, string targetPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        targetPuid = NormalizeProductUserId(targetPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid) || string.IsNullOrWhiteSpace(targetPuid))
            return false;

        if (!_document.BlockLists.TryGetValue(ownerPuid, out var list) || list == null)
            return false;

        var before = list.Blocked.Count;
        list.Blocked = NormalizeBlockedIds(list.Blocked, ownerPuid)
            .Where(x => !string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var changed = list.Blocked.Count != before;
        if (changed)
        {
            list.UpdatedUtc = DateTime.UtcNow;
            _document.BlockLists[ownerPuid] = list;
        }

        return changed;
    }

    private bool IsBlockedUnsafe(string ownerPuid, string targetPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        targetPuid = NormalizeProductUserId(targetPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid) || string.IsNullOrWhiteSpace(targetPuid))
            return false;

        var blocked = GetBlockedIdsUnsafe(ownerPuid);
        return blocked.Any(x => string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase));
    }

    private bool RemoveFriendLinkUnsafe(string ownerPuid, string targetPuid)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        targetPuid = NormalizeProductUserId(targetPuid);
        if (string.IsNullOrWhiteSpace(ownerPuid) || string.IsNullOrWhiteSpace(targetPuid))
            return false;

        if (!_document.FriendLists.TryGetValue(ownerPuid, out var list) || list == null)
            return false;

        var before = list.Friends.Count;
        list.Friends = NormalizeFriendIds(list.Friends, ownerPuid)
            .Where(x => !string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var changed = list.Friends.Count != before;
        if (changed)
        {
            list.UpdatedUtc = DateTime.UtcNow;
            _document.FriendLists[ownerPuid] = list;
        }

        return changed;
    }

    private bool AddFriendUnsafe(
        string ownerPuid,
        string targetPuid,
        out string message,
        out bool changed,
        out int count)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        targetPuid = NormalizeProductUserId(targetPuid);
        message = string.Empty;
        changed = false;
        count = 0;

        if (string.IsNullOrWhiteSpace(ownerPuid))
        {
            message = "Owner ProductUserId is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetPuid))
        {
            message = "Target ProductUserId is required.";
            return false;
        }

        if (string.Equals(ownerPuid, targetPuid, StringComparison.OrdinalIgnoreCase))
        {
            message = "You cannot add yourself as a friend.";
            return false;
        }

        var list = GetOrCreateFriendListUnsafe(ownerPuid);
        if (list.Friends.Any(x => string.Equals(x, targetPuid, StringComparison.OrdinalIgnoreCase)))
        {
            count = list.Friends.Count;
            message = "Friend is already in your list.";
            return true;
        }

        if (list.Friends.Count >= MaxFriendsPerUser)
        {
            count = list.Friends.Count;
            message = $"Friends list is full (max {MaxFriendsPerUser}).";
            return false;
        }

        list.Friends.Add(targetPuid);
        list.Friends = list.Friends.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.UpdatedUtc = DateTime.UtcNow;
        _document.FriendLists[ownerPuid] = list;
        count = list.Friends.Count;
        changed = true;
        message = "Friend added.";
        return true;
    }

    private void MigrateFriendListsUnsafe(string oldPuid, string newPuid)
    {
        oldPuid = NormalizeProductUserId(oldPuid);
        newPuid = NormalizeProductUserId(newPuid);
        if (string.IsNullOrWhiteSpace(oldPuid) || string.IsNullOrWhiteSpace(newPuid))
            return;
        if (string.Equals(oldPuid, newPuid, StringComparison.OrdinalIgnoreCase))
            return;

        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_document.FriendLists.TryGetValue(oldPuid, out var oldList) && oldList?.Friends != null)
            merged.UnionWith(oldList.Friends);
        if (_document.FriendLists.TryGetValue(newPuid, out var newList) && newList?.Friends != null)
            merged.UnionWith(newList.Friends);
        merged.Remove(oldPuid);
        merged.Remove(newPuid);

        _document.FriendLists[newPuid] = new IdentityFriendListEntry
        {
            Friends = merged.ToList(),
            UpdatedUtc = DateTime.UtcNow
        };
        _document.FriendLists.Remove(oldPuid);

        var keys = _document.FriendLists.Keys.ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            var owner = keys[i];
            var entry = _document.FriendLists[owner];
            if (entry?.Friends == null || entry.Friends.Count == 0)
                continue;

            var changed = false;
            var remapped = new List<string>(entry.Friends.Count);
            for (var j = 0; j < entry.Friends.Count; j++)
            {
                var friend = NormalizeProductUserId(entry.Friends[j]);
                if (string.IsNullOrWhiteSpace(friend))
                {
                    changed = true;
                    continue;
                }

                if (string.Equals(friend, oldPuid, StringComparison.OrdinalIgnoreCase))
                {
                    friend = newPuid;
                    changed = true;
                }

                if (string.Equals(owner, friend, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }

                remapped.Add(friend);
            }

            var deduped = remapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (changed || deduped.Count != entry.Friends.Count)
            {
                entry.Friends = deduped;
                entry.UpdatedUtc = DateTime.UtcNow;
                _document.FriendLists[owner] = entry;
            }
        }

        var mergedBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_document.BlockLists.TryGetValue(oldPuid, out var oldBlocked) && oldBlocked?.Blocked != null)
            mergedBlocked.UnionWith(oldBlocked.Blocked);
        if (_document.BlockLists.TryGetValue(newPuid, out var newBlocked) && newBlocked?.Blocked != null)
            mergedBlocked.UnionWith(newBlocked.Blocked);
        mergedBlocked.Remove(oldPuid);
        mergedBlocked.Remove(newPuid);
        _document.BlockLists[newPuid] = new IdentityBlockedListEntry
        {
            Blocked = mergedBlocked.ToList(),
            UpdatedUtc = DateTime.UtcNow
        };
        _document.BlockLists.Remove(oldPuid);

        var blockOwners = _document.BlockLists.Keys.ToArray();
        for (var i = 0; i < blockOwners.Length; i++)
        {
            var owner = blockOwners[i];
            if (!_document.BlockLists.TryGetValue(owner, out var blockedEntry) || blockedEntry == null)
                continue;

            var changed = false;
            var remapped = new List<string>(blockedEntry.Blocked.Count);
            for (var j = 0; j < blockedEntry.Blocked.Count; j++)
            {
                var blocked = NormalizeProductUserId(blockedEntry.Blocked[j]);
                if (string.IsNullOrWhiteSpace(blocked))
                {
                    changed = true;
                    continue;
                }

                if (string.Equals(blocked, oldPuid, StringComparison.OrdinalIgnoreCase))
                {
                    blocked = newPuid;
                    changed = true;
                }

                if (string.Equals(owner, blocked, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }

                remapped.Add(blocked);
            }

            var deduped = remapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (changed || deduped.Count != blockedEntry.Blocked.Count)
            {
                blockedEntry.Blocked = deduped;
                blockedEntry.UpdatedUtc = DateTime.UtcNow;
                _document.BlockLists[owner] = blockedEntry;
            }
        }

        IdentityFriendRequestInboxEntry mergedInbox;
        if (_document.FriendRequests.TryGetValue(newPuid, out var existingInbox) && existingInbox != null)
        {
            mergedInbox = existingInbox;
        }
        else
        {
            mergedInbox = new IdentityFriendRequestInboxEntry();
        }

        if (_document.FriendRequests.TryGetValue(oldPuid, out var oldInbox) && oldInbox?.Incoming != null)
        {
            mergedInbox.Incoming ??= new List<IdentityFriendRequestEntry>();
            mergedInbox.Incoming.AddRange(oldInbox.Incoming);
        }

        mergedInbox.Incoming = NormalizeIncomingRequests(mergedInbox.Incoming, newPuid);
        mergedInbox.UpdatedUtc = DateTime.UtcNow;
        _document.FriendRequests[newPuid] = mergedInbox;
        _document.FriendRequests.Remove(oldPuid);

        var requestOwners = _document.FriendRequests.Keys.ToArray();
        for (var i = 0; i < requestOwners.Length; i++)
        {
            var owner = requestOwners[i];
            if (!_document.FriendRequests.TryGetValue(owner, out var inbox) || inbox == null)
                continue;

            var changed = false;
            var remapped = new List<IdentityFriendRequestEntry>(inbox.Incoming.Count);
            for (var j = 0; j < inbox.Incoming.Count; j++)
            {
                var source = inbox.Incoming[j];
                if (source == null)
                {
                    changed = true;
                    continue;
                }

                var from = NormalizeProductUserId(source.FromProductUserId);
                if (string.IsNullOrWhiteSpace(from))
                {
                    changed = true;
                    continue;
                }

                if (string.Equals(from, oldPuid, StringComparison.OrdinalIgnoreCase))
                {
                    from = newPuid;
                    changed = true;
                }

                if (string.Equals(owner, from, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }

                remapped.Add(new IdentityFriendRequestEntry
                {
                    FromProductUserId = from,
                    CreatedUtc = source.CreatedUtc
                });
            }

            var normalized = NormalizeIncomingRequests(remapped, owner);
            if (changed || normalized.Count != inbox.Incoming.Count)
            {
                inbox.Incoming = normalized;
                inbox.UpdatedUtc = DateTime.UtcNow;
                _document.FriendRequests[owner] = inbox;
            }
        }
    }

    private (bool Ok, bool StateChanged, string Message) VerifyRecoveryCodeUnsafe(string ownerPuid, string? providedCode)
    {
        ownerPuid = NormalizeProductUserId(ownerPuid);
        var now = DateTime.UtcNow;
        if (!_document.Recovery.TryGetValue(ownerPuid, out var record)
            || record == null
            || string.IsNullOrWhiteSpace(record.CodeHash)
            || string.IsNullOrWhiteSpace(record.CodeSalt))
        {
            return (false, false, "No recovery code is set for that identity.");
        }

        if (record.LockUntilUtc.HasValue && record.LockUntilUtc.Value > now)
        {
            var remaining = record.LockUntilUtc.Value - now;
            var mins = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return (false, false, $"Recovery is temporarily locked. Try again in about {mins} minute(s).");
        }

        var normalized = NormalizeRecoveryCode(providedCode);
        if (string.IsNullOrWhiteSpace(normalized))
            return (false, false, "Recovery code is required.");

        var expectedHash = record.CodeHash.Trim();
        var computedHash = ComputeRecoveryCodeHashHex(record.CodeSalt, normalized);
        if (!FixedTimeEqualsHex(expectedHash, computedHash))
        {
            record.FailedAttempts = Math.Max(0, record.FailedAttempts) + 1;
            var stateChanged = true;
            if (record.FailedAttempts >= RecoveryLockThreshold)
            {
                record.FailedAttempts = 0;
                record.LockUntilUtc = now.Add(RecoveryLockDuration);
                _document.Recovery[ownerPuid] = record;
                return (false, stateChanged, $"Too many invalid attempts. Recovery is locked for {(int)RecoveryLockDuration.TotalMinutes} minutes.");
            }

            _document.Recovery[ownerPuid] = record;
            var remainingAttempts = RecoveryLockThreshold - record.FailedAttempts;
            return (false, stateChanged, $"Invalid recovery code. {remainingAttempts} attempt(s) left before temporary lock.");
        }

        record.FailedAttempts = 0;
        record.LockUntilUtc = null;
        _document.Recovery[ownerPuid] = record;
        return (true, true, "ok");
    }

    private static List<string> NormalizeFriendIds(IEnumerable<string>? values, string ownerPuid)
    {
        var owner = NormalizeProductUserId(ownerPuid);
        return (values ?? Enumerable.Empty<string>())
            .Select(NormalizeProductUserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !string.Equals(x, owner, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeBlockedIds(IEnumerable<string>? values, string ownerPuid)
    {
        var owner = NormalizeProductUserId(ownerPuid);
        return (values ?? Enumerable.Empty<string>())
            .Select(NormalizeProductUserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !string.Equals(x, owner, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<IdentityFriendRequestEntry> NormalizeIncomingRequests(IEnumerable<IdentityFriendRequestEntry>? values, string ownerPuid)
    {
        var owner = NormalizeProductUserId(ownerPuid);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<IdentityFriendRequestEntry>();
        foreach (var entry in values ?? Enumerable.Empty<IdentityFriendRequestEntry>())
        {
            if (entry == null)
                continue;

            var from = NormalizeProductUserId(entry.FromProductUserId);
            if (string.IsNullOrWhiteSpace(from))
                continue;
            if (string.Equals(from, owner, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(from))
                continue;

            list.Add(new IdentityFriendRequestEntry
            {
                FromProductUserId = from,
                CreatedUtc = entry.CreatedUtc == default ? DateTime.UtcNow : entry.CreatedUtc
            });
        }

        return list;
    }

    private static string NormalizeRecoveryCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var cleaned = new string(raw
            .Trim()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray());
        return cleaned.ToUpperInvariant();
    }

    private static string GenerateRecoveryCode()
    {
        Span<byte> bytes = stackalloc byte[RecoveryCodeLength];
        RandomNumberGenerator.Fill(bytes);
        var raw = new char[RecoveryCodeLength];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = FriendCodeAlphabet[bytes[i] % FriendCodeAlphabet.Length];

        // User-friendly display grouping: XXXX-XXXX-XXXX-XXXX-XXXX
        return string.Concat(
            new string(raw, 0, 4), "-",
            new string(raw, 4, 4), "-",
            new string(raw, 8, 4), "-",
            new string(raw, 12, 4), "-",
            new string(raw, 16, 4));
    }

    private static string GenerateRecoverySalt()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeRecoveryCodeHashHex(string salt, string normalizedRecoveryCode)
    {
        var payload = $"{salt}:{normalizedRecoveryCode}".Trim();
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsHex(string leftHex, string rightHex)
    {
        if (string.IsNullOrWhiteSpace(leftHex) || string.IsNullOrWhiteSpace(rightHex))
            return false;

        try
        {
            var left = Convert.FromHexString(leftHex.Trim());
            var right = Convert.FromHexString(rightHex.Trim());
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDisplayName(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = fallback;

        if (normalized.Length > 32)
            normalized = normalized.Substring(0, 32);

        return normalized;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        if (!int.TryParse(value, out var parsed))
            return fallback;
        return parsed > 0 ? parsed : fallback;
    }

    private static bool IsRetryableSaveFailure(HttpStatusCode statusCode, string details)
    {
        if (statusCode == HttpStatusCode.Conflict)
            return true;

        if (statusCode == HttpStatusCode.TooManyRequests)
            return true;

        if ((int)statusCode >= 500)
            return true;

        if (statusCode != HttpStatusCode.UnprocessableEntity)
            return false;

        var normalized = (details ?? string.Empty).ToLowerInvariant();
        var hasShaMismatch =
            normalized.Contains("sha") &&
            (normalized.Contains("does not match")
             || normalized.Contains("was at")
             || normalized.Contains("not the latest")
             || normalized.Contains("missing")
             || normalized.Contains("weren't supplied")
             || normalized.Contains("not supplied"));
        return hasShaMismatch;
    }

    private static string BuildGithubGetContentsEndpoint(string repo, string path, string branch)
    {
        var encodedPath = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var normalizedBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch;
        return $"https://api.github.com/repos/{repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(normalizedBranch)}";
    }

    private static string BuildGithubPutContentsEndpoint(string repo, string path)
    {
        var encodedPath = string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"https://api.github.com/repos/{repo}/contents/{encodedPath}";
    }

    private static string GenerateFriendCode(string productUserId)
    {
        if (string.IsNullOrWhiteSpace(productUserId))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes($"{productUserId.Trim()}:{FriendCodeSalt}");
        var hash = SHA256.HashData(bytes);
        var chars = new char[FriendCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = FriendCodeAlphabet[hash[i] % FriendCodeAlphabet.Length];
        return FriendCodePrefix + new string(chars);
    }

    private static string ShortId(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= 10)
            return trimmed;
        return $"{trimmed[..5]}...{trimmed[^5..]}";
    }

    private sealed class GithubContentsResponse
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("encoding")]
        public string? Encoding { get; set; }

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }

    private sealed class GithubUpdateRequest
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; set; } = "main";

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }

    private sealed class GithubUpdateResponse
    {
        [JsonPropertyName("content")]
        public GithubContentInfo? Content { get; set; }
    }

    private sealed class GithubContentInfo
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }

    private sealed class SaveResult
    {
        public bool Ok { get; private init; }
        public bool Retryable { get; private init; }
        public string Error { get; private init; } = string.Empty;
        public string Sha { get; private init; } = string.Empty;

        public static SaveResult Success(string? sha) => new() { Ok = true, Sha = sha ?? string.Empty };
        public static SaveResult Fail(string error, bool retryable) => new() { Ok = false, Error = error, Retryable = retryable };
    }
}

sealed class ProviderResult
{
    public bool Ok { get; init; }
    public string Error { get; init; } = string.Empty;

    public static ProviderResult Success() => new() { Ok = true };
    public static ProviderResult Fail(string error) => new() { Ok = false, Error = error };
}

sealed class IdentityRegistryDocument
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<IdentityRegistryEntry> Users { get; set; } = new();
    public Dictionary<string, IdentityFriendListEntry> FriendLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IdentityFriendRequestInboxEntry> FriendRequests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IdentityBlockedListEntry> BlockLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IdentityRecoveryEntry> Recovery { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static IdentityRegistryDocument Empty() => new()
    {
        Version = 1,
        UpdatedUtc = DateTime.UtcNow,
        Users = new List<IdentityRegistryEntry>(),
        FriendLists = new Dictionary<string, IdentityFriendListEntry>(StringComparer.OrdinalIgnoreCase),
        FriendRequests = new Dictionary<string, IdentityFriendRequestInboxEntry>(StringComparer.OrdinalIgnoreCase),
        BlockLists = new Dictionary<string, IdentityBlockedListEntry>(StringComparer.OrdinalIgnoreCase),
        Recovery = new Dictionary<string, IdentityRecoveryEntry>(StringComparer.OrdinalIgnoreCase)
    };
}

sealed class IdentityRegistryEntry
{
    public string ProductUserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

sealed class IdentityFriendListEntry
{
    public List<string> Friends { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

sealed class IdentityFriendRequestInboxEntry
{
    public List<IdentityFriendRequestEntry> Incoming { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

sealed class IdentityFriendRequestEntry
{
    public string FromProductUserId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

sealed class IdentityBlockedListEntry
{
    public List<string> Blocked { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

sealed class IdentityRecoveryEntry
{
    public string CodeHash { get; set; } = string.Empty;
    public string CodeSalt { get; set; } = string.Empty;
    public DateTime RotatedUtc { get; set; } = DateTime.UtcNow;
    public int FailedAttempts { get; set; }
    public DateTime? LockUntilUtc { get; set; }
}

sealed record IdentityUsernameRules(int MinLength, int MaxLength, string Regex);
sealed record IdentityResolvedUser(string ProductUserId, string Username, string DisplayName, string FriendCode, DateTime UpdatedUtc);
sealed record IdentityResolveResult(bool Found, IdentityResolvedUser? User, string Reason);
sealed record IdentityClaimResult(bool Ok, string Message, string Code, IdentityResolvedUser? User);
sealed record IdentityAdminResult(bool Ok, string Message, IdentityResolvedUser? User);
sealed record IdentityFriendRequestInfo(string ProductUserId, IdentityResolvedUser User, DateTime RequestedUtc);
sealed record IdentityFriendsResult(
    bool Ok,
    string Message,
    IReadOnlyList<IdentityResolvedUser> Friends,
    IReadOnlyList<IdentityFriendRequestInfo> IncomingRequests,
    IReadOnlyList<IdentityFriendRequestInfo> OutgoingRequests,
    IReadOnlyList<IdentityResolvedUser> BlockedUsers);
sealed record IdentityFriendMutationResult(bool Ok, string Message, IdentityResolvedUser? Friend, int Count);
sealed record IdentityRecoveryRotateResult(bool Ok, string Message, string RecoveryCode);
sealed record IdentityTransferResult(bool Ok, string Message, IdentityResolvedUser? User);
