using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Gate;

public sealed class OnlineSocialStateService
{
    private static readonly object Sync = new();
    private static OnlineSocialStateService? _instance;

    private readonly OnlineGateClient _gate;
    private Logger _log;
    private readonly object _stateLock = new();
    private readonly Queue<SocialIncomingRequestNotification> _pendingNotifications = new();

    private List<GateIdentityUser> _friends = new();
    private List<GateFriendRequest> _incomingRequests = new();
    private List<GateFriendRequest> _outgoingRequests = new();
    private List<GateIdentityUser> _blockedUsers = new();
    private Dictionary<string, GatePresenceEntry> _presenceByUserId = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownIncomingKeys = new(StringComparer.Ordinal);

    private bool _pollInProgress;
    private bool _presencePollInProgress;
    private DateTime _nextFriendsPollUtc = DateTime.MinValue;
    private DateTime _nextPresencePollUtc = DateTime.MinValue;
    private bool _friendsEndpointUnavailable;
    private string _lastStatus = string.Empty;

    private static readonly TimeSpan FriendsPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MissingEndpointRetryInterval = TimeSpan.FromSeconds(45);

    private OnlineSocialStateService(Logger log)
    {
        _log = log;
        _gate = OnlineGateClient.GetOrCreate();
    }

    public static OnlineSocialStateService GetOrCreate(Logger log)
    {
        lock (Sync)
        {
            _instance ??= new OnlineSocialStateService(log);
            _instance._log = log;
            return _instance;
        }
    }

    public void Tick()
    {
        if (_pollInProgress)
            return;

        var now = DateTime.UtcNow;
        if (now < _nextFriendsPollUtc)
            return;

        _ = PollFriendsAsync();
    }

    public void ForceRefresh()
    {
        _nextFriendsPollUtc = DateTime.MinValue;
        _nextPresencePollUtc = DateTime.MinValue;
    }

    public OnlineSocialSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new OnlineSocialSnapshot
            {
                Friends = _friends.ToArray(),
                IncomingRequests = _incomingRequests.ToArray(),
                OutgoingRequests = _outgoingRequests.ToArray(),
                BlockedUsers = _blockedUsers.ToArray(),
                PresenceByUserId = new Dictionary<string, GatePresenceEntry>(_presenceByUserId, StringComparer.OrdinalIgnoreCase),
                PendingIncomingCount = _incomingRequests.Count,
                FriendsEndpointUnavailable = _friendsEndpointUnavailable,
                Status = _lastStatus
            };
        }
    }

    public bool TryDequeueNewRequestNotification(out SocialIncomingRequestNotification notification)
    {
        lock (_stateLock)
        {
            if (_pendingNotifications.Count > 0)
            {
                notification = _pendingNotifications.Dequeue();
                return true;
            }
        }

        notification = default!;
        return false;
    }

    private async Task PollFriendsAsync()
    {
        _pollInProgress = true;
        try
        {
            var result = await _gate.GetFriendsAsync().ConfigureAwait(false);
            if (!result.Ok)
            {
                var message = string.IsNullOrWhiteSpace(result.Message)
                    ? "Friends lookup failed."
                    : result.Message;

                lock (_stateLock)
                {
                    _lastStatus = message;
                    _friendsEndpointUnavailable = IsMissingFriendsEndpointError(message) || _gate.IsFriendsTemporarilyUnavailable;
                }

                _nextFriendsPollUtc = DateTime.UtcNow.Add(
                    _friendsEndpointUnavailable ? MissingEndpointRetryInterval : RetryPollInterval);
                return;
            }

            var newFriends = result.Friends?.ToList() ?? new List<GateIdentityUser>();
            var newIncoming = result.IncomingRequests?.ToList() ?? new List<GateFriendRequest>();
            var newOutgoing = result.OutgoingRequests?.ToList() ?? new List<GateFriendRequest>();
            var newBlocked = result.BlockedUsers?.ToList() ?? new List<GateIdentityUser>();

            lock (_stateLock)
            {
                _friends = newFriends;
                _incomingRequests = newIncoming;
                _outgoingRequests = newOutgoing;
                _blockedUsers = newBlocked;
                _friendsEndpointUnavailable = false;
                _lastStatus = string.Empty;

                var currentIncomingKeys = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < _incomingRequests.Count; i++)
                {
                    var request = _incomingRequests[i];
                    var key = BuildIncomingRequestKey(request);
                    currentIncomingKeys.Add(key);
                    if (_knownIncomingKeys.Contains(key))
                        continue;

                    var displayName = ResolveRequestDisplayName(request);
                    _pendingNotifications.Enqueue(new SocialIncomingRequestNotification(
                        request.ProductUserId,
                        displayName,
                        request.RequestedUtc));
                }

                _knownIncomingKeys = currentIncomingKeys;
            }

            _nextFriendsPollUtc = DateTime.UtcNow.Add(FriendsPollInterval);

            if (_presencePollInProgress || DateTime.UtcNow < _nextPresencePollUtc || newFriends.Count == 0)
                return;

            _ = PollPresenceAsync(newFriends.Select(f => (f.ProductUserId ?? string.Empty).Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
        catch (Exception ex)
        {
            _log.Warn($"Social service friends poll failed: {ex.Message}");
            _nextFriendsPollUtc = DateTime.UtcNow.Add(RetryPollInterval);
        }
        finally
        {
            _pollInProgress = false;
        }
    }

    private async Task PollPresenceAsync(IReadOnlyCollection<string> friendIds)
    {
        if (friendIds == null || friendIds.Count == 0)
        {
            _nextPresencePollUtc = DateTime.UtcNow.Add(PresencePollInterval);
            return;
        }

        _presencePollInProgress = true;
        try
        {
            var result = await _gate.QueryPresenceAsync(friendIds).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Warn($"Social service presence poll failed: requested={friendIds.Count} returned=0");
                _nextPresencePollUtc = DateTime.UtcNow.Add(RetryPollInterval);
                return;
            }

            var map = new Dictionary<string, GatePresenceEntry>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < result.Entries.Count; i++)
            {
                var entry = result.Entries[i];
                var id = (entry.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                map[id] = entry;
            }

            lock (_stateLock)
            {
                _presenceByUserId = map;
            }

            _log.Info($"Social service presence poll ok: requested={friendIds.Count} activeHosts={map.Count}");
            _nextPresencePollUtc = DateTime.UtcNow.Add(PresencePollInterval);
        }
        catch (Exception ex)
        {
            _log.Warn($"Social service presence poll failed: {ex.Message}");
            _nextPresencePollUtc = DateTime.UtcNow.Add(RetryPollInterval);
        }
        finally
        {
            _presencePollInProgress = false;
        }
    }

    private static string BuildIncomingRequestKey(GateFriendRequest request)
    {
        var id = (request.ProductUserId ?? string.Empty).Trim().ToLowerInvariant();
        var stamp = request.RequestedUtc.ToUniversalTime().ToString("O");
        return $"{id}|{stamp}";
    }

    private static string ResolveRequestDisplayName(GateFriendRequest request)
    {
        var fromUser = request.User;
        if (fromUser != null)
        {
            if (!string.IsNullOrWhiteSpace(fromUser.DisplayName))
                return fromUser.DisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(fromUser.Username))
                return fromUser.Username.Trim();
        }

        return string.IsNullOrWhiteSpace(request.ProductUserId)
            ? "Player"
            : PlayerProfile.ShortId(request.ProductUserId);
    }

    private static bool IsMissingFriendsEndpointError(string? message)
    {
        var value = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("Not Found", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public sealed class OnlineSocialSnapshot
{
    public IReadOnlyList<GateIdentityUser> Friends { get; init; } = Array.Empty<GateIdentityUser>();
    public IReadOnlyList<GateFriendRequest> IncomingRequests { get; init; } = Array.Empty<GateFriendRequest>();
    public IReadOnlyList<GateFriendRequest> OutgoingRequests { get; init; } = Array.Empty<GateFriendRequest>();
    public IReadOnlyList<GateIdentityUser> BlockedUsers { get; init; } = Array.Empty<GateIdentityUser>();
    public IReadOnlyDictionary<string, GatePresenceEntry> PresenceByUserId { get; init; } =
        new Dictionary<string, GatePresenceEntry>(StringComparer.OrdinalIgnoreCase);
    public int PendingIncomingCount { get; init; }
    public bool FriendsEndpointUnavailable { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed record SocialIncomingRequestNotification(
    string ProductUserId,
    string DisplayName,
    DateTime RequestedUtc);
