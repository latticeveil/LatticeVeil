using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

#if EOS_SDK
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Friends;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using Epic.OnlineServices.Presence;
using Epic.OnlineServices.UserInfo;
using EosComparisonOp = Epic.OnlineServices.ComparisonOp;
using EosAuth = Epic.OnlineServices.Auth;
#endif

namespace LatticeVeilMonoGame.Online.Eos;

public sealed class EosClient : IDisposable
{
    private readonly Logger _log;
    private bool _disposed;
    private const string PresenceKeyHosting = "rc_hosting";
    private const string PresenceKeyWorld = "rc_world";
    private const string LobbyBucketId = "latticeveil_online_worlds_v1";
    private const string LobbyAttrHosting = "rc_hosting";
    private const string LobbyAttrInWorld = "rc_in_world";
    private const string LobbyAttrWorld = "rc_world";
    private const string LobbyAttrMode = "rc_mode";
    private const string LobbyAttrCheats = "rc_cheats";
    private const string LobbyAttrPlayerCount = "rc_player_count";
    private const string LobbyAttrMaxPlayers = "rc_max_players";
    private const string LobbyAttrHostPuid = "rc_host_puid";
    private const string LobbyAttrHostUsername = "rc_host_username";
    private const string LobbyAttrWorldId = "rc_world_id";
    private const string LobbyAttrApp = "rc_app";
    private const string LobbyAttrAppValue = "latticeveil";

#if EOS_SDK
    private readonly EosConfig _config;
    private PlatformInterface? _platform;
    private ConnectInterface? _connect;
    private EosAuth.AuthInterface? _auth;
    private UserInfoInterface? _userInfo;
    private FriendsInterface? _friends;
    private PresenceInterface? _presence;
    private P2PInterface? _p2p;
    private LobbyInterface? _lobby;
    private ProductUserId? _localUserId;
    private EpicAccountId? _epicAccountId;
    private string? _epicDisplayName;
    private bool _deviceLoginStarted;
    private bool _epicLoginStarted;
    private bool _allowDeviceFallback;
    private bool _silentLoginOnly;
    private string _activeHostedLobbyId = string.Empty;
    private string _joinedLobbyId = string.Empty;
    private bool _activeHostedLobbyUsesBucket = true;
    private LobbyPermissionLevel _activeHostedLobbyPermission = LobbyPermissionLevel.Publicadvertised;
    private readonly SemaphoreSlim _hostedLobbyMutationLock = new(1, 1);
    private readonly object _sdkLock = new();
#endif

    private EosClient(Logger log, EosConfig config) { _log = log; _config = config; }

    public static EosClient? TryCreate(Logger log, string? loginModeOverride = null, bool autoLogin = true)
    {
        EosConfig? config;
        try
        {
            config = EosConfig.Load(log);
        }
        catch (Exception ex)
        {
            log.Error($"EOS config validation failed: {ex.Message}");
            return null;
        }

        if (config == null) return null;
#if EOS_SDK
        if (!string.IsNullOrWhiteSpace(loginModeOverride)) config.LoginMode = loginModeOverride;
        var client = new EosClient(log, config);
        if (!client.Initialize()) return null;
        if (autoLogin) client.BeginLogin();
        return client;
#else
        return null;
#endif
    }

    public string? LocalProductUserId => _localUserId?.ToString();
    public string? EpicAccountId => _epicAccountId?.ToString();
    public string? EpicDisplayName => _epicDisplayName;
    public string? DeviceId => _localUserId?.ToString();

#if EOS_SDK
    public ProductUserId? LocalProductUserIdHandle => _localUserId;
    public EpicAccountId? EpicAccountIdHandle => _epicAccountId;
    public P2PInterface? P2PInterface => _p2p;
#endif

    public bool IsLoggedIn { get { lock (_sdkLock) return _localUserId != null && _localUserId.IsValid(); } }
    public bool IsEpicLoggedIn { get { lock (_sdkLock) return _epicAccountId != null && _epicAccountId.IsValid(); } }

    public void Tick() { lock (_sdkLock) { if (!_disposed) _platform?.Tick(); } }

    public void StartLogin() { if (IsLoggedIn || _deviceLoginStarted) return; BeginLogin(); }

    public void StartSilentLogin() { if (IsLoggedIn || _deviceLoginStarted) return; BeginLogin(); }

    private bool Initialize()
    {
#if EOS_SDK
        try
        {
            InitializeOptions initOptions = new InitializeOptions { ProductName = _config.ProductName, ProductVersion = _config.ProductVersion };
            Result initResult; lock (_sdkLock) initResult = PlatformInterface.Initialize(ref initOptions);
            if (initResult != Result.Success && initResult != Result.AlreadyConfigured) return false;
            Options options = new Options { ProductId = _config.ProductId, SandboxId = _config.SandboxId, DeploymentId = _config.DeploymentId, ClientCredentials = new ClientCredentials { ClientId = _config.ClientId, ClientSecret = _config.ClientSecret ?? string.Empty }, IsServer = false };
            lock (_sdkLock) _platform = PlatformInterface.Create(ref options);
            if (_platform == null) return false;
            _connect = _platform.GetConnectInterface();
            _auth = _platform.GetAuthInterface();
            _userInfo = _platform.GetUserInfoInterface();
            _friends = _platform.GetFriendsInterface();
            _presence = _platform.GetPresenceInterface();
            _p2p = _platform.GetP2PInterface();
            _lobby = _platform.GetLobbyInterface();
            ConfigureRelayControl();
            return true;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    private void BeginLogin()
    {
#if EOS_SDK
        var mode = (_config.LoginMode ?? "deviceid").Trim().ToLowerInvariant();
        if (mode == "epic")
        {
            BeginDeviceIdLogin();
            return;
        }

        BeginDeviceIdLogin();
#endif
    }

    private void BeginDeviceIdLogin()
    {
#if EOS_SDK
        if (_connect == null || _deviceLoginStarted) return;
        _deviceLoginStarted = true;
        LoginOptions opts = new LoginOptions
        {
            Credentials = new Credentials
            {
                Type = ExternalCredentialType.DeviceidAccessToken,
                Token = ""
            },
            UserLoginInfo = new UserLoginInfo
            {
                DisplayName = Environment.UserName
            }
        };
        lock (_sdkLock) _connect.Login(ref opts, null, OnLoginComplete);
#endif
    }

    private void OnLoginComplete(ref LoginCallbackInfo info)
    {
#if EOS_SDK
        if (info.ResultCode == Result.Success)
        {
            _localUserId = info.LocalUserId;
            _deviceLoginStarted = false;
            _log.Info("EOS login success (deviceid)");
            return;
        }

        if (info.ResultCode == Result.InvalidUser)
        {
            _log.Warn("EOS login returned InvalidUser; attempting CreateDeviceId...");
            CreateDeviceId();
            return;
        }

        _deviceLoginStarted = false;
        _log.Warn($"EOS login failed: {info.ResultCode}");
#endif
    }

    private void CreateDeviceId()
    {
#if EOS_SDK
        if (_connect == null) return;
        CreateDeviceIdOptions opts = new CreateDeviceIdOptions { DeviceModel = Environment.MachineName };
        lock (_sdkLock) _connect.CreateDeviceId(ref opts, null, OnCreateDeviceId);
#endif
    }

    private void OnCreateDeviceId(ref CreateDeviceIdCallbackInfo info)
    {
#if EOS_SDK
        _log.Info($"EOS CreateDeviceId result: {info.ResultCode}");
        if (info.ResultCode == Result.Success || info.ResultCode == Result.DuplicateNotAllowed)
        {
            _log.Info("EOS CreateDeviceId completed; retrying deviceid login...");
            _deviceLoginStarted = false;
            BeginDeviceIdLogin();
            return;
        }
        return;
#endif
    }

    public async Task<bool> SetHostingPresenceAsync(string? world, bool hosting)
    {
#if EOS_SDK
        if (_presence == null || _localUserId == null) return false;
        // Connect-only apps use ProductUserId for presence calls if available, but SDK often expects EpicAccountId for the modification handle
        // If your SDK version requires EpicAccountId, we must skip this or use a dummy if not logged into Epic.
        if (_epicAccountId == null) return false; 

        CreatePresenceModificationOptions mOpts = new CreatePresenceModificationOptions { LocalUserId = _epicAccountId };
        PresenceModification? mod; lock (_sdkLock) { _presence.CreatePresenceModification(ref mOpts, out mod); }
        if (mod == null) return false;
        try {
            PresenceModificationSetStatusOptions sOpts = new PresenceModificationSetStatusOptions { Status = Status.Online };
            mod.SetStatus(ref sOpts);
            PresenceModificationSetDataOptions dOpts = new PresenceModificationSetDataOptions { Records = new[] { 
                new DataRecord { Key = PresenceKeyHosting, Value = hosting.ToString().ToLowerInvariant() }, 
                new DataRecord { Key = PresenceKeyWorld, Value = world ?? "" } 
            } };
            mod.SetData(ref dOpts);
            SetPresenceOptions uOpts = new SetPresenceOptions { LocalUserId = _epicAccountId, PresenceModificationHandle = mod };
            var tcs = new TaskCompletionSource<bool>();
            lock (_sdkLock) _presence.SetPresence(ref uOpts, null, (ref SetPresenceCallbackInfo info) => tcs.TrySetResult(info.ResultCode == Result.Success));
            return await tcs.Task;
        }
        finally { }
#else
        return false;
#endif
    }

    public async Task<bool> StartOrUpdateHostedLobbyAsync(
        string? worldName,
        string? gameMode,
        bool cheats,
        int playerCount,
        int maxPlayers,
        bool isInWorld,
        string? hostUsername = null,
        string? worldId = null)
    {
#if EOS_SDK
        await _hostedLobbyMutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
        if (!TryGetLobbyContext(out _, out _))
            return false;

        var normalizedWorld = NormalizeWorldName(worldName);
        var normalizedMode = NormalizeMode(gameMode);
        var normalizedHostUsername = NormalizeOptional(hostUsername);
        var normalizedWorldId = NormalizeOptional(worldId);
        var normalizedMaxPlayers = (uint)Math.Clamp(maxPlayers <= 0 ? 8 : maxPlayers, 1, (int)LobbyInterface.MAX_LOBBY_MEMBERS);
        var normalizedPlayerCount = Math.Clamp(playerCount <= 0 ? 1 : playerCount, 1, (int)normalizedMaxPlayers);

        if (!await EnsureHostedLobbyAsync(normalizedMaxPlayers).ConfigureAwait(false))
            return false;

        var lobbyId = (_activeHostedLobbyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(lobbyId))
            return false;

        var updated = await UpdateHostedLobbyStateAsync(
            lobbyId,
            normalizedWorld,
            normalizedMode,
            cheats,
            normalizedPlayerCount,
            (int)normalizedMaxPlayers,
            isInWorld,
            normalizedHostUsername,
            normalizedWorldId).ConfigureAwait(false);

        if (!updated)
        {
            _log.Warn($"EOS_LOBBY_UPDATE failed lobbyId={lobbyId}; attempting lobby recreate.");
            await DestroyLobbyAsync(lobbyId).ConfigureAwait(false);
            _activeHostedLobbyId = string.Empty;

            if (!await EnsureHostedLobbyAsync(normalizedMaxPlayers).ConfigureAwait(false))
                return false;

            lobbyId = (_activeHostedLobbyId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(lobbyId))
                return false;

            updated = await UpdateHostedLobbyStateAsync(
                lobbyId,
                normalizedWorld,
                normalizedMode,
                cheats,
                normalizedPlayerCount,
                (int)normalizedMaxPlayers,
                isInWorld,
                normalizedHostUsername,
                normalizedWorldId).ConfigureAwait(false);
        }

        _ = SetHostingPresenceAsync(normalizedWorld, true);
        _log.Info($"EOS_LOBBY_ADVERTISE lobbyId={lobbyId} world={normalizedWorld} inWorld={isInWorld} players={normalizedPlayerCount}/{normalizedMaxPlayers}");
        return updated;
        }
        finally
        {
            _hostedLobbyMutationLock.Release();
        }
#else
        return false;
#endif
    }

    public async Task<bool> StopHostedLobbyAsync()
    {
#if EOS_SDK
        await _hostedLobbyMutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
        var lobbyId = (_activeHostedLobbyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            _ = SetHostingPresenceAsync(null, false);
            return true;
        }

        var destroyed = await DestroyLobbyAsync(lobbyId).ConfigureAwait(false);
        if (destroyed)
        {
            _activeHostedLobbyId = string.Empty;
            _activeHostedLobbyUsesBucket = true;
            _activeHostedLobbyPermission = LobbyPermissionLevel.Publicadvertised;
            if (string.Equals(_joinedLobbyId, lobbyId, StringComparison.OrdinalIgnoreCase))
                _joinedLobbyId = string.Empty;
            _ = SetHostingPresenceAsync(null, false);
        }

        _log.Info($"EOS_LOBBY_ADVERTISE_STOP lobbyId={lobbyId} ok={destroyed}");
        return destroyed;
        }
        finally
        {
            _hostedLobbyMutationLock.Release();
        }
#else
        return false;
#endif
    }

    public async Task<bool> JoinLobbyAsync(string? lobbyId)
    {
#if EOS_SDK
        var normalizedLobbyId = (lobbyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedLobbyId))
            return false;

        if (string.Equals(_activeHostedLobbyId, normalizedLobbyId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_joinedLobbyId, normalizedLobbyId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return false;

        var (lookupResult, details) = await FindLobbyDetailsByIdAsync(normalizedLobbyId).ConfigureAwait(false);
        if (lookupResult != Result.Success || details == null)
        {
            _log.Warn($"EOS_LOBBY_JOIN lookup failed lobbyId={normalizedLobbyId} result={lookupResult}");
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_joinedLobbyId)
                && !string.Equals(_joinedLobbyId, normalizedLobbyId, StringComparison.OrdinalIgnoreCase))
            {
                await LeaveJoinedLobbyAsync().ConfigureAwait(false);
            }

            var joinOpts = new JoinLobbyOptions
            {
                LobbyDetailsHandle = details,
                LocalUserId = localUserId,
                PresenceEnabled = false
            };

            var joinTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sdkLock)
            {
                lobby.JoinLobby(ref joinOpts, null, (ref JoinLobbyCallbackInfo info) =>
                {
                    joinTcs.TrySetResult(info.ResultCode);
                });
            }

            var joinResult = await joinTcs.Task.ConfigureAwait(false);
            var ok = joinResult == Result.Success || joinResult == Result.AlreadyPending;
            if (ok)
                _joinedLobbyId = normalizedLobbyId;
            _log.Info($"EOS_LOBBY_JOIN lobbyId={normalizedLobbyId} result={joinResult} ok={ok}");
            return ok;
        }
        finally
        {
            details.Release();
        }
#else
        return false;
#endif
    }

    public async Task<bool> LeaveJoinedLobbyAsync()
    {
#if EOS_SDK
        var lobbyId = (_joinedLobbyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(lobbyId))
            return true;

        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return false;

        var leaveOpts = new LeaveLobbyOptions
        {
            LocalUserId = localUserId,
            LobbyId = lobbyId
        };

        var leaveTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sdkLock)
        {
            lobby.LeaveLobby(ref leaveOpts, null, (ref LeaveLobbyCallbackInfo info) =>
            {
                leaveTcs.TrySetResult(info.ResultCode);
            });
        }

        var leaveResult = await leaveTcs.Task.ConfigureAwait(false);
        var ok = leaveResult == Result.Success
            || leaveResult == Result.NotFound
            || leaveResult == Result.AlreadyPending
            || leaveResult == Result.NoChange;
        if (ok)
            _joinedLobbyId = string.Empty;
        _log.Info($"EOS_LOBBY_LEAVE lobbyId={lobbyId} result={leaveResult} ok={ok}");
        return ok;
#else
        return false;
#endif
    }

    public async Task<IReadOnlyList<EosHostedLobbyEntry>> FindHostedLobbiesAsync(IReadOnlyCollection<string>? hostProductUserIds = null)
    {
#if EOS_SDK
        var requestedHosts = (hostProductUserIds ?? Array.Empty<string>())
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = await SearchHostedLobbiesAsync(requestedHosts, includeBucketFilter: true).ConfigureAwait(false);
        if (entries.Count > 0)
            return entries;

        if (requestedHosts.Count > 0)
            return entries;

        // Fallback: some EOS environments can fail strict bucket filtering; retry without it.
        entries = await SearchHostedLobbiesAsync(requestedHosts, includeBucketFilter: false).ConfigureAwait(false);
        return entries;
#else
        return Array.Empty<EosHostedLobbyEntry>();
#endif
    }

#if EOS_SDK
    private async Task<List<EosHostedLobbyEntry>> SearchHostedLobbiesAsync(
        HashSet<string> requestedHosts,
        bool includeBucketFilter)
    {
        var entries = new List<EosHostedLobbyEntry>();
        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return entries;

        var searchOpts = new CreateLobbySearchOptions { MaxResults = 200 };
        LobbySearch? search;
        Result createSearchResult;
        lock (_sdkLock)
            createSearchResult = lobby.CreateLobbySearch(ref searchOpts, out search);
        if (createSearchResult != Result.Success || search == null)
        {
            _log.Warn($"EOS_LOBBY_SEARCH create failed: {createSearchResult}");
            return entries;
        }

        try
        {
            var setMaxOpts = new LobbySearchSetMaxResultsOptions { MaxResults = 200 };
            Result setMaxResult;
            lock (_sdkLock)
                setMaxResult = search.SetMaxResults(ref setMaxOpts);
            if (setMaxResult != Result.Success && setMaxResult != Result.NoChange)
                _log.Warn($"EOS_LOBBY_SEARCH set max failed: {setMaxResult}");

            if (includeBucketFilter)
            {
                var bucketParam = new LobbySearchSetParameterOptions
                {
                    Parameter = new AttributeData { Key = LobbyInterface.SEARCH_BUCKET_ID, Value = LobbyBucketId },
                    ComparisonOp = EosComparisonOp.Equal
                };
                Result setBucketResult;
                lock (_sdkLock)
                    setBucketResult = search.SetParameter(ref bucketParam);
                if (setBucketResult != Result.Success && setBucketResult != Result.NoChange)
                    _log.Warn($"EOS_LOBBY_SEARCH bucket filter failed: {setBucketResult}");
            }

            var findOpts = new LobbySearchFindOptions { LocalUserId = localUserId };
            var findTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sdkLock)
            {
                search.Find(ref findOpts, null, (ref LobbySearchFindCallbackInfo info) =>
                {
                    findTcs.TrySetResult(info.ResultCode);
                });
            }

            var findResult = await findTcs.Task.ConfigureAwait(false);
            if (findResult == Result.MissingFeature)
            {
                _log.Warn("EOS_LOBBY_SEARCH unavailable (MissingFeature). Enable Lobby feature/actions in EOS Client Policy.");
                return entries;
            }
            if (findResult != Result.Success && findResult != Result.NotFound)
            {
                _log.Warn($"EOS_LOBBY_SEARCH find failed: {findResult}");
                return entries;
            }

            var countOpts = new LobbySearchGetSearchResultCountOptions();
            uint count;
            lock (_sdkLock)
                count = search.GetSearchResultCount(ref countOpts);
            _log.Info($"EOS_LOBBY_SEARCH_RESULTS count={count} bucketFilter={includeBucketFilter}");

            for (uint i = 0; i < count; i++)
            {
                var copyOpts = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                LobbyDetails? details;
                Result copyResult;
                lock (_sdkLock)
                    copyResult = search.CopySearchResultByIndex(ref copyOpts, out details);
                if (copyResult != Result.Success || details == null)
                    continue;

                try
                {
                    if (!TryReadHostedLobbyEntry(details, out var entry))
                        continue;

                    if (requestedHosts.Count > 0 && !requestedHosts.Contains(entry.HostProductUserId))
                        continue;

                    entries.Add(entry);
                }
                finally
                {
                    details.Release();
                }
            }

            // Keep one row per lobby id in case EOS returns duplicates.
            return entries
                .GroupBy(e => (e.LobbyId ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        finally
        {
            search.Release();
        }
    }

    private bool TryGetLobbyContext(out LobbyInterface lobby, out ProductUserId localUserId)
    {
        lobby = _lobby!;
        localUserId = _localUserId!;
        if (_lobby == null || _localUserId == null || !_localUserId.IsValid())
            return false;
        return true;
    }

    private async Task<bool> EnsureHostedLobbyAsync(uint maxLobbyMembers)
    {
        if (!string.IsNullOrWhiteSpace(_activeHostedLobbyId))
            return true;

        return await CreateHostedLobbyAsync(maxLobbyMembers).ConfigureAwait(false);
    }

    private async Task<bool> CreateHostedLobbyAsync(uint maxLobbyMembers)
    {
        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return false;

        var attempts = new[]
        {
            new CreateLobbyOptions
            {
                LocalUserId = localUserId,
                MaxLobbyMembers = maxLobbyMembers,
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = true,
                BucketId = LobbyBucketId,
                DisableHostMigration = false,
                EnableRTCRoom = false,
                EnableJoinById = true
            },
            new CreateLobbyOptions
            {
                LocalUserId = localUserId,
                MaxLobbyMembers = maxLobbyMembers,
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = true,
                BucketId = default,
                DisableHostMigration = false,
                EnableRTCRoom = false,
                EnableJoinById = true
            },
            new CreateLobbyOptions
            {
                LocalUserId = localUserId,
                MaxLobbyMembers = maxLobbyMembers,
                PermissionLevel = LobbyPermissionLevel.Inviteonly,
                PresenceEnabled = false,
                AllowInvites = true,
                BucketId = default,
                DisableHostMigration = false,
                EnableRTCRoom = false,
                EnableJoinById = true
            }
        };

        for (var attempt = 0; attempt < attempts.Length; attempt++)
        {
            var createOpts = attempts[attempt];
            var createTcs = new TaskCompletionSource<CreateLobbyCallbackInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sdkLock)
            {
                lobby.CreateLobby(ref createOpts, null, (ref CreateLobbyCallbackInfo info) =>
                {
                    createTcs.TrySetResult(info);
                });
            }

            var callback = await createTcs.Task.ConfigureAwait(false);
            if (callback.ResultCode == Result.Success && !string.IsNullOrWhiteSpace(callback.LobbyId))
            {
                _activeHostedLobbyId = callback.LobbyId;
                _activeHostedLobbyUsesBucket = attempt == 0;
                _activeHostedLobbyPermission = createOpts.PermissionLevel;
                _log.Info($"EOS_LOBBY_CREATE ok lobbyId={_activeHostedLobbyId} attempt={attempt + 1}");
                return true;
            }

            _log.Warn($"EOS_LOBBY_CREATE failed result={callback.ResultCode} attempt={attempt + 1}");
            if (callback.ResultCode == Result.MissingFeature)
                break;
        }

        return false;
    }

    private async Task<bool> DestroyLobbyAsync(string? lobbyId)
    {
        var normalizedLobbyId = (lobbyId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedLobbyId))
            return true;

        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return false;

        var destroyOpts = new DestroyLobbyOptions
        {
            LocalUserId = localUserId,
            LobbyId = normalizedLobbyId
        };

        var destroyTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sdkLock)
        {
            lobby.DestroyLobby(ref destroyOpts, null, (ref DestroyLobbyCallbackInfo info) =>
            {
                destroyTcs.TrySetResult(info.ResultCode);
            });
        }

        var destroyResult = await destroyTcs.Task.ConfigureAwait(false);
        return destroyResult == Result.Success
            || destroyResult == Result.NotFound
            || destroyResult == Result.AlreadyPending
            || destroyResult == Result.NoChange;
    }

    private async Task<bool> UpdateHostedLobbyStateAsync(
        string lobbyId,
        string worldName,
        string gameMode,
        bool cheats,
        int playerCount,
        int maxPlayers,
        bool isInWorld,
        string hostUsername,
        string worldId)
    {
        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return false;

        var modOpts = new UpdateLobbyModificationOptions
        {
            LocalUserId = localUserId,
            LobbyId = lobbyId
        };

        LobbyModification? mod;
        Result createModResult;
        lock (_sdkLock)
            createModResult = lobby.UpdateLobbyModification(ref modOpts, out mod);
        if (createModResult != Result.Success || mod == null)
            return false;

        try
        {
            var setPermissionOpts = new LobbyModificationSetPermissionLevelOptions
            {
                PermissionLevel = _activeHostedLobbyPermission
            };
            var setPermissionResult = mod.SetPermissionLevel(ref setPermissionOpts);
            if (setPermissionResult != Result.Success && setPermissionResult != Result.NoChange)
                return false;

            if (_activeHostedLobbyUsesBucket)
            {
                var setBucketOpts = new LobbyModificationSetBucketIdOptions
                {
                    BucketId = LobbyBucketId
                };
                var setBucketResult = mod.SetBucketId(ref setBucketOpts);
                if (setBucketResult != Result.Success && setBucketResult != Result.NoChange)
                    return false;
            }

            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrHosting, true))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrInWorld, isInWorld))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrWorld, worldName))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrMode, gameMode))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrCheats, cheats))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrPlayerCount, playerCount))
                return false;
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrMaxPlayers, maxPlayers))
                return false;
            if (_localUserId != null && _localUserId.IsValid())
            {
                if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrHostPuid, _localUserId.ToString()))
                    return false;
            }
            if (!string.IsNullOrWhiteSpace(hostUsername))
            {
                if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrHostUsername, hostUsername))
                    return false;
            }
            if (!string.IsNullOrWhiteSpace(worldId))
            {
                if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrWorldId, worldId))
                    return false;
            }
            if (!AddOrReplaceLobbyAttribute(mod, LobbyAttrApp, LobbyAttrAppValue))
                return false;

            var updateOpts = new UpdateLobbyOptions { LobbyModificationHandle = mod };
            var updateTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sdkLock)
            {
                lobby.UpdateLobby(ref updateOpts, null, (ref UpdateLobbyCallbackInfo info) =>
                {
                    updateTcs.TrySetResult(info.ResultCode);
                });
            }

            var updateResult = await updateTcs.Task.ConfigureAwait(false);
            return updateResult == Result.Success || updateResult == Result.NoChange;
        }
        finally
        {
            mod.Release();
        }
    }

    private bool AddOrReplaceLobbyAttribute(LobbyModification mod, string key, AttributeDataValue value)
    {
        var removeOpts = new LobbyModificationRemoveAttributeOptions { Key = key };
        _ = mod.RemoveAttribute(ref removeOpts);

        var addOpts = new LobbyModificationAddAttributeOptions
        {
            Attribute = new AttributeData
            {
                Key = key,
                Value = value
            },
            Visibility = LobbyAttributeVisibility.Public
        };

        var addResult = mod.AddAttribute(ref addOpts);
        return addResult == Result.Success || addResult == Result.NoChange;
    }

    private async Task<(Result ResultCode, LobbyDetails? Details)> FindLobbyDetailsByIdAsync(string lobbyId)
    {
        if (!TryGetLobbyContext(out var lobby, out var localUserId))
            return (Result.InvalidState, null);

        var createSearchOpts = new CreateLobbySearchOptions { MaxResults = 1 };
        LobbySearch? search;
        Result createSearchResult;
        lock (_sdkLock)
            createSearchResult = lobby.CreateLobbySearch(ref createSearchOpts, out search);
        if (createSearchResult != Result.Success || search == null)
            return (createSearchResult, null);

        try
        {
            var setIdOpts = new LobbySearchSetLobbyIdOptions { LobbyId = lobbyId };
            Result setIdResult;
            lock (_sdkLock)
                setIdResult = search.SetLobbyId(ref setIdOpts);
            if (setIdResult != Result.Success)
                return (setIdResult, null);

            var findOpts = new LobbySearchFindOptions { LocalUserId = localUserId };
            var findTcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sdkLock)
            {
                search.Find(ref findOpts, null, (ref LobbySearchFindCallbackInfo info) =>
                {
                    findTcs.TrySetResult(info.ResultCode);
                });
            }

            var findResult = await findTcs.Task.ConfigureAwait(false);
            if (findResult != Result.Success)
                return (findResult, null);

            var countOpts = new LobbySearchGetSearchResultCountOptions();
            uint count;
            lock (_sdkLock)
                count = search.GetSearchResultCount(ref countOpts);
            if (count == 0)
                return (Result.NotFound, null);

            var copyOpts = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
            LobbyDetails? details;
            Result copyResult;
            lock (_sdkLock)
                copyResult = search.CopySearchResultByIndex(ref copyOpts, out details);
            return (copyResult, copyResult == Result.Success ? details : null);
        }
        finally
        {
            search.Release();
        }
    }

    private bool TryReadHostedLobbyEntry(LobbyDetails details, out EosHostedLobbyEntry entry)
    {
        entry = default!;
        var copyInfoOpts = new LobbyDetailsCopyInfoOptions();
        LobbyDetailsInfo? info;
        Result infoResult;
        lock (_sdkLock)
            infoResult = details.CopyInfo(ref copyInfoOpts, out info);
        if (infoResult != Result.Success || info == null)
            return false;

        var hostPuid = info.Value.LobbyOwnerUserId.ToString();
        if (string.IsNullOrWhiteSpace(hostPuid))
            hostPuid = CopyLobbyStringAttribute(details, LobbyAttrHostPuid, string.Empty);
        if (string.IsNullOrWhiteSpace(hostPuid))
            return false;

        var lobbyId = info.Value.LobbyId.ToString() ?? string.Empty;
        var isHosting = CopyLobbyBoolAttribute(details, LobbyAttrHosting, false);
        var isInWorld = CopyLobbyBoolAttribute(details, LobbyAttrInWorld, false);
        var worldName = CopyLobbyStringAttribute(details, LobbyAttrWorld, string.Empty);
        var gameMode = CopyLobbyStringAttribute(details, LobbyAttrMode, "Unknown");
        var cheats = CopyLobbyBoolAttribute(details, LobbyAttrCheats, false);
        var defaultPlayers = Math.Max(1, (int)info.Value.MaxMembers - (int)info.Value.AvailableSlots);
        var playerCount = Math.Max(1, CopyLobbyIntAttribute(details, LobbyAttrPlayerCount, defaultPlayers));
        var maxPlayers = Math.Max(playerCount, CopyLobbyIntAttribute(details, LobbyAttrMaxPlayers, (int)info.Value.MaxMembers));
        var hostUsername = CopyLobbyStringAttribute(details, LobbyAttrHostUsername, string.Empty);
        var worldId = CopyLobbyStringAttribute(details, LobbyAttrWorldId, string.Empty);

        entry = new EosHostedLobbyEntry
        {
            LobbyId = lobbyId,
            HostProductUserId = hostPuid.Trim(),
            HostUsername = hostUsername,
            IsHosting = isHosting,
            IsInWorld = isInWorld,
            WorldName = worldName,
            GameMode = gameMode,
            Cheats = cheats,
            PlayerCount = playerCount,
            MaxPlayers = maxPlayers,
            WorldId = worldId
        };

        return true;
    }

    private string CopyLobbyStringAttribute(LobbyDetails details, string key, string fallback)
    {
        if (!TryCopyLobbyAttribute(details, key, out var data))
            return fallback;

        if (data.Value.ValueType == AttributeType.String)
        {
            var value = data.Value.AsUtf8.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return fallback;
    }

    private bool CopyLobbyBoolAttribute(LobbyDetails details, string key, bool fallback)
    {
        if (!TryCopyLobbyAttribute(details, key, out var data))
            return fallback;

        var value = data.Value;
        if (value.ValueType == AttributeType.Boolean && value.AsBool.HasValue)
            return value.AsBool.Value;
        if (value.ValueType == AttributeType.Int64 && value.AsInt64.HasValue)
            return value.AsInt64.Value != 0;
        if (value.ValueType == AttributeType.String)
        {
            var raw = value.AsUtf8.ToString();
            if (bool.TryParse(raw, out var parsedBool))
                return parsedBool;
            if (long.TryParse(raw, out var parsedLong))
                return parsedLong != 0;
        }

        return fallback;
    }

    private int CopyLobbyIntAttribute(LobbyDetails details, string key, int fallback)
    {
        if (!TryCopyLobbyAttribute(details, key, out var data))
            return fallback;

        var value = data.Value;
        if (value.ValueType == AttributeType.Int64 && value.AsInt64.HasValue)
            return (int)value.AsInt64.Value;
        if (value.ValueType == AttributeType.Double && value.AsDouble.HasValue)
            return (int)Math.Round(value.AsDouble.Value);
        if (value.ValueType == AttributeType.String)
        {
            var raw = value.AsUtf8.ToString();
            if (int.TryParse(raw, out var parsedInt))
                return parsedInt;
            if (double.TryParse(raw, out var parsedDouble))
                return (int)Math.Round(parsedDouble);
        }

        return fallback;
    }

    private bool TryCopyLobbyAttribute(LobbyDetails details, string key, out AttributeData data)
    {
        data = default;
        var copyOpts = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
        Epic.OnlineServices.Lobby.Attribute? attribute;
        Result copyResult;
        lock (_sdkLock)
            copyResult = details.CopyAttributeByKey(ref copyOpts, out attribute);
        if (copyResult != Result.Success || attribute == null || attribute.Value.Data == null)
            return false;

        data = attribute.Value.Data.Value;
        return true;
    }

    private static string NormalizeWorldName(string? worldName)
    {
        var normalized = (worldName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "WORLD";

        const int MaxLobbyWorldNameLength = 64;
        if (normalized.Length > MaxLobbyWorldNameLength)
            normalized = normalized.Substring(0, MaxLobbyWorldNameLength);
        return normalized;
    }

    private static string NormalizeMode(string? gameMode)
    {
        var normalized = (gameMode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "Unknown";

        const int MaxLobbyModeLength = 32;
        if (normalized.Length > MaxLobbyModeLength)
            normalized = normalized.Substring(0, MaxLobbyModeLength);
        return normalized;
    }

    private static string NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        const int MaxLobbyOptionalLength = 64;
        if (normalized.Length > MaxLobbyOptionalLength)
            normalized = normalized.Substring(0, MaxLobbyOptionalLength);
        return normalized;
    }
#endif

    private void ConfigureRelayControl()
    {
        if (_p2p == null)
            return;

        SetRelayControlOptions opts = new SetRelayControlOptions { RelayControl = RelayControl.AllowRelays };
        lock (_sdkLock)
            _p2p.SetRelayControl(ref opts);
    }

    public Task<(bool Ok, string? Error)> LogoutAsync() { _localUserId = null; return Task.FromResult((true, (string?)null)); }
    public Task<(bool Ok, string? Error)> DeletePersistentAuthAsync() { return Task.FromResult((false, (string?)"Not implemented")); }

    public void Dispose() { lock (_sdkLock) { if (!_disposed) { _platform?.Release(); _platform = null; _disposed = true; } } }
}

public sealed class EosHostedLobbyEntry
{
    public string LobbyId { get; init; } = string.Empty;
    public string HostProductUserId { get; init; } = string.Empty;
    public string HostUsername { get; init; } = string.Empty;
    public bool IsHosting { get; init; }
    public bool IsInWorld { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string GameMode { get; init; } = string.Empty;
    public bool Cheats { get; init; }
    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public string WorldId { get; init; } = string.Empty;
}
