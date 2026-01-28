using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

#if EOS_SDK
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Friends;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using Epic.OnlineServices.Presence;
using Epic.OnlineServices.UserInfo;
using EosAuth = Epic.OnlineServices.Auth;
#endif

namespace LatticeVeilMonoGame.Online.Eos;

public sealed class EosClient : IDisposable
{
    private readonly Logger _log;
    private bool _disposed;
    private const string PresenceKeyHosting = "rc_hosting";
    private const string PresenceKeyWorld = "rc_world";

#if EOS_SDK
    private readonly EosConfig _config;
    private PlatformInterface? _platform;
    private ConnectInterface? _connect;
    private EosAuth.AuthInterface? _auth;
    private UserInfoInterface? _userInfo;
    private FriendsInterface? _friends;
    private PresenceInterface? _presence;
    private P2PInterface? _p2p;
    private ProductUserId? _localUserId;
    private EpicAccountId? _epicAccountId;
    private string? _epicDisplayName;
    private bool _deviceLoginStarted;
    private bool _epicLoginStarted;
    private bool _allowDeviceFallback;
    private bool _silentLoginOnly;
    private bool _silentLoginFailed;
    private EpicAuthStage _epicAuthStage = EpicAuthStage.None;
    private readonly Dictionary<string, string> _displayNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _displayNameCacheStamp = new(StringComparer.OrdinalIgnoreCase);
#endif

#if EOS_SDK
    private enum LoginMode
    {
        DeviceId,
        EpicAccount,
        EpicThenDeviceId
    }

    private enum EpicAuthStage
    {
        None,
        PersistentAuth,
        AccountPortal
    }
#endif

    private EosClient(Logger log
#if EOS_SDK
        , EosConfig config
#endif
        )
    {
        _log = log;
#if EOS_SDK
        _config = config;
#endif
    }

    public static EosClient? TryCreate(Logger log, string? loginModeOverride = null, bool autoLogin = true)
    {
        var config = EosConfig.Load(log);
        if (config == null)
            return null;

#if EOS_SDK
        if (!string.IsNullOrWhiteSpace(loginModeOverride))
            config.LoginMode = loginModeOverride;

        var client = new EosClient(log, config);
        if (!client.Initialize())
            return null;

        if (autoLogin)
            client.BeginLogin();
        return client;
#else
        log.Warn("EOS config found but EOS SDK is not installed. Place EOS C# SDK files under ThirdParty/EOS/SDK and EOSSDK-Win64-Shipping.dll in ThirdParty/EOS.");
        return null;
#endif
    }

    public string? LocalProductUserId =>
#if EOS_SDK
        _localUserId?.ToString();
#else
        null;
#endif

    public string? EpicAccountId =>
#if EOS_SDK
        _epicAccountId?.ToString();
#else
        null;
#endif

    public string? EpicDisplayName =>
#if EOS_SDK
        _epicDisplayName;
#else
        null;
#endif

    public bool IsLoggedIn =>
#if EOS_SDK
        _localUserId != null && _localUserId.IsValid();
#else
        false;
#endif

    public bool IsEpicLoggedIn =>
#if EOS_SDK
        _epicAccountId != null && _epicAccountId.IsValid();
#else
        false;
#endif

    public bool SilentLoginFailed => _silentLoginFailed;

#if EOS_SDK
    public ProductUserId? LocalProductUserIdHandle => _localUserId;
    public EpicAccountId? EpicAccountIdHandle => _epicAccountId;
    public P2PInterface? P2PInterface => _p2p;
#endif

    public void Tick()
    {
#if EOS_SDK
        _platform?.Tick();
#endif
    }

    public Task<(bool Ok, string? Error)> LogoutAsync()
    {
#if EOS_SDK
        return LogoutInternalAsync();
#else
        return Task.FromResult((false, "EOS SDK not available."));
#endif
    }

    public Task<(bool Ok, string? Error)> DeletePersistentAuthAsync()
    {
#if EOS_SDK
        return DeletePersistentAuthInternalAsync();
#else
        return Task.FromResult((false, "EOS SDK not available."));
#endif
    }

    public void StartLogin()
    {
#if EOS_SDK
        if (_platform == null)
            return;
        if (IsLoggedIn || _epicLoginStarted || _deviceLoginStarted)
            return;
        _silentLoginOnly = false;
        _silentLoginFailed = false;
        BeginLogin();
#endif
    }

    public void StartSilentLogin()
    {
#if EOS_SDK
        if (_platform == null)
            return;
        if (IsLoggedIn || _epicLoginStarted || _deviceLoginStarted)
            return;
        _silentLoginOnly = true;
        _silentLoginFailed = false;
        BeginLogin();
#endif
    }

    public void Dispose()
    {
#if EOS_SDK
        if (_disposed)
            return;

        _disposed = true;

        try { _platform?.Release(); } catch { }
        _platform = null;

        try { PlatformInterface.Shutdown(); } catch { }
#endif
    }

#if EOS_SDK
    private bool Initialize()
    {
        try
        {
            var initOptions = new InitializeOptions
            {
                ProductName = _config.ProductName,
                ProductVersion = _config.ProductVersion
            };

            var initResult = PlatformInterface.Initialize(ref initOptions);
            if (initResult != Result.Success && initResult != Result.AlreadyConfigured)
            {
                _log.Warn($"EOS initialize failed: {initResult}");
                return false;
            }
            if (initResult == Result.AlreadyConfigured)
                _log.Info("EOS already configured; continuing.");

            var options = new Options
            {
                ProductId = _config.ProductId,
                SandboxId = _config.SandboxId,
                DeploymentId = _config.DeploymentId,
                ClientCredentials = new ClientCredentials
                {
                    ClientId = _config.ClientId,
                    ClientSecret = _config.ClientSecret
                },
                IsServer = false
            };

            _platform = PlatformInterface.Create(ref options);
            if (_platform == null)
            {
                _log.Warn("EOS platform creation failed.");
                return false;
            }

            _connect = _platform.GetConnectInterface();
            _auth = _platform.GetAuthInterface();
            _userInfo = _platform.GetUserInfoInterface();
            _friends = _platform.GetFriendsInterface();
            _presence = _platform.GetPresenceInterface();
            _p2p = _platform.GetP2PInterface();
            ConfigureRelayControl();
            _log.Info("EOS platform initialized.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS initialize failed: {ex.Message}");
            return false;
        }
    }

    private void BeginLogin()
    {
        var mode = ResolveLoginMode(_config.LoginMode);
        switch (mode)
        {
            case LoginMode.EpicAccount:
                _log.Info("EOS login mode: Epic account.");
                BeginEpicLogin(allowDeviceFallback: false);
                break;
            case LoginMode.EpicThenDeviceId:
                _log.Info("EOS login mode: Epic account (fallback to device).");
                BeginEpicLogin(allowDeviceFallback: true);
                break;
            default:
                _log.Info("EOS login mode: Device ID.");
                BeginDeviceIdLogin();
                break;
        }
    }

    private LoginMode ResolveLoginMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LoginMode.DeviceId;

        var mode = value.Trim();
        if (mode.Equals("epic", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("accountportal", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("epicaccount", StringComparison.OrdinalIgnoreCase))
            return LoginMode.EpicAccount;

        if (mode.Equals("epic-or-device", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("epic_then_device", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("fallback", StringComparison.OrdinalIgnoreCase))
            return LoginMode.EpicThenDeviceId;

        if (mode.Equals("device", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("deviceid", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("device-id", StringComparison.OrdinalIgnoreCase))
            return LoginMode.DeviceId;

        _log.Warn($"EOS login mode '{value}' not recognized; using device id.");
        return LoginMode.DeviceId;
    }

    private void BeginEpicLogin(bool allowDeviceFallback)
    {
        if (_auth == null || _connect == null || _epicLoginStarted)
            return;

        _allowDeviceFallback = allowDeviceFallback;
        _epicLoginStarted = true;
        _epicAuthStage = EpicAuthStage.PersistentAuth;

        StartAuthLogin(EosAuth.LoginCredentialType.PersistentAuth, noUserInterface: true);
    }

    private void StartAuthLogin(EosAuth.LoginCredentialType credentialType, bool noUserInterface)
    {
        if (_auth == null)
            return;

        var loginOptions = new EosAuth.LoginOptions
        {
            Credentials = new EosAuth.Credentials
            {
                Type = credentialType,
                Id = null,
                Token = null
            },
            ScopeFlags = EosAuth.AuthScopeFlags.BasicProfile
                | EosAuth.AuthScopeFlags.FriendsList
                | EosAuth.AuthScopeFlags.Presence
                | EosAuth.AuthScopeFlags.Country,
            LoginFlags = noUserInterface ? EosAuth.LoginFlags.NoUserInterface : EosAuth.LoginFlags.None
        };

        _log.Info($"EOS auth login start: {credentialType}, ui={(noUserInterface ? "no-ui" : "ui")}, scopes=BasicProfile|FriendsList|Presence|Country");
        _auth.Login(ref loginOptions, null, OnAuthLoginComplete);
    }

    private void OnAuthLoginComplete(ref EosAuth.LoginCallbackInfo info)
    {
        if (info.ResultCode == Result.Success)
        {
            var accountId = info.SelectedAccountId != null && info.SelectedAccountId.IsValid()
                ? info.SelectedAccountId
                : info.LocalUserId;

            if (accountId == null || !accountId.IsValid())
            {
                _log.Warn("EOS auth login succeeded but account id is invalid.");
                FallbackToDeviceLoginIfNeeded();
                return;
            }

            _epicAccountId = accountId;
            _epicDisplayName = null;
            _log.Info($"EOS auth login success. EpicAccountId={accountId}");
            QueryEpicDisplayName(accountId);
            StartConnectLoginWithEpic(accountId);
            _silentLoginOnly = false;
            return;
        }

        if (_silentLoginOnly && _epicAuthStage == EpicAuthStage.PersistentAuth && RequiresUserInterface(info.ResultCode))
        {
            _log.Info("EOS silent login requires UI; waiting for user action.");
            _silentLoginOnly = false;
            _silentLoginFailed = true;
            ResetLoginState();
            return;
        }

        if (_epicAuthStage == EpicAuthStage.PersistentAuth && RequiresUserInterface(info.ResultCode))
        {
            _epicAuthStage = EpicAuthStage.AccountPortal;
            StartAuthLogin(EosAuth.LoginCredentialType.AccountPortal, noUserInterface: false);
            return;
        }

        _log.Warn($"EOS auth login failed: {info.ResultCode}");
        _silentLoginOnly = false;
        _silentLoginFailed = true;
        FallbackToDeviceLoginIfNeeded();
    }

    private void StartConnectLoginWithEpic(EpicAccountId accountId)
    {
        if (_auth == null || _connect == null)
            return;

        var tokenOptions = new EosAuth.CopyIdTokenOptions
        {
            AccountId = accountId
        };

        var tokenResult = _auth.CopyIdToken(ref tokenOptions, out var idToken);
        if (tokenResult != Result.Success || idToken == null)
        {
            _log.Warn($"EOS auth token copy failed: {tokenResult}");
            FallbackToDeviceLoginIfNeeded();
            return;
        }

        var jwt = (string)idToken.Value.JsonWebToken;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            _log.Warn("EOS auth id token missing.");
            FallbackToDeviceLoginIfNeeded();
            return;
        }

        var loginOptions = new LoginOptions
        {
            Credentials = new Credentials
            {
                Type = ExternalCredentialType.EpicIdToken,
                Token = jwt
            }
        };

        _connect.Login(ref loginOptions, null, OnLoginComplete);
    }

    private void QueryEpicDisplayName(EpicAccountId accountId)
    {
        if (_userInfo == null)
            return;

        var options = new QueryUserInfoOptions
        {
            LocalUserId = accountId,
            TargetUserId = accountId
        };

        _log.Info("EOS user info query start.");
        _userInfo.QueryUserInfo(ref options, null, OnQueryUserInfoComplete);
    }

    private void OnQueryUserInfoComplete(ref QueryUserInfoCallbackInfo info)
    {
        if (info.ResultCode != Result.Success)
        {
            _log.Warn($"EOS user info query failed: {info.ResultCode}");
            return;
        }

        if (_userInfo == null)
            return;

        var copyOptions = new CopyUserInfoOptions
        {
            LocalUserId = info.LocalUserId,
            TargetUserId = info.TargetUserId
        };

        var copyResult = _userInfo.CopyUserInfo(ref copyOptions, out var userInfo);
        if (copyResult != Result.Success || userInfo == null)
        {
            _log.Warn($"EOS user info copy failed: {copyResult}");
            return;
        }

        var displayName = ((string?)userInfo.Value.DisplayName)?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        _epicDisplayName = displayName;
        CacheDisplayName(info.TargetUserId, displayName);
        _log.Info($"EOS display name: {_epicDisplayName}");
    }

    private bool RequiresUserInterface(Result result)
    {
        return result == Result.AuthUserInterfaceRequired
            || result == Result.NotFound
            || result == Result.InvalidAuth
            || result == Result.AuthInvalidToken
            || result == Result.AuthInvalidRefreshToken;
    }

    private void FallbackToDeviceLoginIfNeeded()
    {
        if (!_allowDeviceFallback)
            return;

        _log.Info("Falling back to EOS device ID login.");
        BeginDeviceIdLogin();
    }

    private void BeginDeviceIdLogin()
    {
        if (_connect == null || _deviceLoginStarted)
            return;

        _deviceLoginStarted = true;

        var createOptions = new CreateDeviceIdOptions
        {
            DeviceModel = Environment.MachineName
        };

        _connect.CreateDeviceId(ref createOptions, null, OnCreateDeviceId);
    }

    private void OnCreateDeviceId(ref CreateDeviceIdCallbackInfo info)
    {
        if (info.ResultCode == Result.Success || info.ResultCode == Result.DuplicateNotAllowed)
        {
            LoginWithDeviceId();
            return;
        }

        _log.Warn($"EOS CreateDeviceId failed: {info.ResultCode}");
    }

    private void LoginWithDeviceId()
    {
        if (_connect == null)
            return;

        var loginOptions = new LoginOptions
        {
            Credentials = new Credentials
            {
                Type = ExternalCredentialType.DeviceidAccessToken,
                Token = string.Empty
            },
            UserLoginInfo = new UserLoginInfo
            {
                DisplayName = BuildDeviceDisplayName()
            }
        };

        _connect.Login(ref loginOptions, null, OnLoginComplete);
    }

    private void OnLoginComplete(ref LoginCallbackInfo info)
    {
        if (info.ResultCode == Result.Success)
        {
            _localUserId = info.LocalUserId;
            _log.Info($"EOS login success. PUID={_localUserId}");
            _silentLoginOnly = false;
            return;
        }

        if (info.ResultCode == Result.InvalidUser && info.ContinuanceToken != null)
        {
            var createUserOptions = new CreateUserOptions
            {
                ContinuanceToken = info.ContinuanceToken
            };
            if (_connect != null)
                _connect.CreateUser(ref createUserOptions, null, OnCreateUserComplete);
            return;
        }

        _log.Warn($"EOS login failed: {info.ResultCode}");
        _silentLoginOnly = false;
        _silentLoginFailed = true;
    }

    private void OnCreateUserComplete(ref CreateUserCallbackInfo info)
    {
        if (info.ResultCode != Result.Success)
        {
            _log.Warn($"EOS CreateUser failed: {info.ResultCode}");
            return;
        }

        _localUserId = info.LocalUserId;
        _log.Info($"EOS user created. PUID={_localUserId}");
    }

    public async Task<List<EosFriendSnapshot>> GetFriendsWithPresenceAsync()
    {
        var friends = new List<EosFriendSnapshot>();
        if (_friends == null || _presence == null || _userInfo == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return friends;

        var queryResult = await WaitWithTimeout(QueryFriendsInternalAsync(), TimeSpan.FromSeconds(8));
        if (queryResult != Result.Success)
        {
            _log.Warn($"EOS friends query failed: {queryResult}");
            return friends;
        }

        var countOptions = new GetFriendsCountOptions { LocalUserId = _epicAccountId };
        var count = _friends.GetFriendsCount(ref countOptions);
        for (var i = 0; i < count; i++)
        {
            var friendOptions = new GetFriendAtIndexOptions { LocalUserId = _epicAccountId, Index = i };
            var friendId = _friends.GetFriendAtIndex(ref friendOptions);
            if (friendId == null || !friendId.IsValid())
                continue;

            var statusOptions = new GetStatusOptions { LocalUserId = _epicAccountId, TargetUserId = friendId };
            var status = _friends.GetStatus(ref statusOptions);

            var displayName = await GetDisplayNameAsync(friendId);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = friendId.ToString();

            var presence = await GetPresenceSnapshotAsync(friendId);

            friends.Add(new EosFriendSnapshot
            {
                AccountId = friendId.ToString(),
                DisplayName = displayName ?? friendId.ToString(),
                Status = ToFriendStatusLabel(status),
                Presence = presence.Status,
                IsHosting = presence.IsHosting,
                WorldName = presence.WorldName,
                JoinInfo = presence.JoinInfo
            });
        }

        return friends;
    }

    public async Task<(bool Ok, string? Error)> SendFriendInviteByDisplayNameAsync(string displayName)
    {
        if (_friends == null || _userInfo == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return (false, "Epic login required.");

        displayName = (displayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            return (false, "Enter an Epic display name.");

        var (timedOut, lookup) = await WaitWithTimeout(QueryUserInfoByDisplayNameInternalAsync(displayName), TimeSpan.FromSeconds(8));
        if (timedOut)
            return (false, "Lookup timed out.");

        if (lookup.Result != Result.Success)
            return (false, lookup.Result == Result.NotFound ? "User not found." : $"Lookup failed: {lookup.Result}");

        if (lookup.TargetUserId == null || !lookup.TargetUserId.IsValid())
            return (false, "User not found.");

        var invite = await WaitWithTimeout(SendInviteInternalAsync(lookup.TargetUserId), TimeSpan.FromSeconds(8));
        if (invite != Result.Success)
            return (false, $"Invite failed: {invite}");

        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> AcceptFriendInviteAsync(string targetAccountId)
    {
        if (_friends == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return (false, "Epic login required.");

        // NOTE: This class also exposes a public string property named EpicAccountId.
        // Fully-qualify the EOS type to avoid resolving to the string property.
        var target = global::Epic.OnlineServices.EpicAccountId.FromString((targetAccountId ?? "").Trim());
        if (target == null || !target.IsValid())
            return (false, "Invalid friend.");

        var res = await WaitWithTimeout(AcceptInviteInternalAsync(target), TimeSpan.FromSeconds(8));
        if (res != Result.Success)
            return (false, $"Accept failed: {res}");
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> RejectFriendInviteAsync(string targetAccountId)
    {
        if (_friends == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return (false, "Epic login required.");

        // See note in AcceptFriendInviteAsync.
        var target = global::Epic.OnlineServices.EpicAccountId.FromString((targetAccountId ?? "").Trim());
        if (target == null || !target.IsValid())
            return (false, "Invalid friend.");

        var res = await WaitWithTimeout(RejectInviteInternalAsync(target), TimeSpan.FromSeconds(8));
        if (res != Result.Success)
            return (false, $"Reject failed: {res}");
        return (true, null);
    }

    public async Task<bool> SetHostingPresenceAsync(string? worldName, bool hosting)
    {
        if (_presence == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return false;

        if (_localUserId == null || !_localUserId.IsValid())
            return false;

        var modOptions = new CreatePresenceModificationOptions
        {
            LocalUserId = _epicAccountId
        };

        var modResult = _presence.CreatePresenceModification(ref modOptions, out var mod);
        if (modResult != Result.Success)
        {
            _log.Warn($"EOS presence modification failed: {modResult}");
            return false;
        }

        try
        {
            var statusOptions = new PresenceModificationSetStatusOptions { Status = Status.Online };
            mod.SetStatus(ref statusOptions);

            if (hosting)
            {
                var joinInfo = _localUserId.ToString();
                var joinOptions = new PresenceModificationSetJoinInfoOptions { JoinInfo = joinInfo };
                mod.SetJoinInfo(ref joinOptions);

                var records = new List<DataRecord>
                {
                    new DataRecord { Key = PresenceKeyHosting, Value = "1" }
                };
                if (!string.IsNullOrWhiteSpace(worldName))
                    records.Add(new DataRecord { Key = PresenceKeyWorld, Value = worldName });

                var dataOptions = new PresenceModificationSetDataOptions { Records = records.ToArray() };
                mod.SetData(ref dataOptions);

                var rich = $"Hosting {worldName ?? "World"}";
                var richOptions = new PresenceModificationSetRawRichTextOptions { RichText = rich };
                mod.SetRawRichText(ref richOptions);
            }
            else
            {
                var joinOptions = new PresenceModificationSetJoinInfoOptions { JoinInfo = "" };
                mod.SetJoinInfo(ref joinOptions);

                var deleteOptions = new PresenceModificationDeleteDataOptions
                {
                    Records = new[]
                    {
                        new PresenceModificationDataRecordId { Key = PresenceKeyHosting },
                        new PresenceModificationDataRecordId { Key = PresenceKeyWorld }
                    }
                };
                mod.DeleteData(ref deleteOptions);

                var richOptions = new PresenceModificationSetRawRichTextOptions { RichText = "In menus" };
                mod.SetRawRichText(ref richOptions);
            }

            var setOptions = new SetPresenceOptions
            {
                LocalUserId = _epicAccountId,
                PresenceModificationHandle = mod
            };

            var setResult = await WaitWithTimeout(SetPresenceInternalAsync(setOptions), TimeSpan.FromSeconds(6));
            if (setResult != Result.Success)
            {
                _log.Warn($"EOS presence set failed: {setResult}");
                return false;
            }

            _log.Info(hosting
                ? $"EOS presence hosting set (world={worldName ?? "World"})."
                : "EOS presence hosting cleared.");
            return true;
        }
        finally
        {
            try { mod.Release(); } catch { }
        }
    }

    private void ConfigureRelayControl()
    {
        if (_p2p == null)
            return;

        var relayOptions = new SetRelayControlOptions { RelayControl = RelayControl.ForceRelays };
        var relayResult = _p2p.SetRelayControl(ref relayOptions);
        if (relayResult != Result.Success)
            _log.Warn($"EOS relay control set failed: {relayResult}");
        else
            _log.Info("EOS relay control set to ForceRelays.");
    }

    private async Task<(bool Ok, string? Error)> LogoutInternalAsync()
    {
        if (_connect == null || _auth == null)
            return (false, "EOS not initialized.");

        var authOk = true;
        var connectOk = true;

        if (_epicAccountId != null && _epicAccountId.IsValid())
        {
            var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new EosAuth.LogoutOptions { LocalUserId = _epicAccountId };
            _auth.Logout(ref options, tcs, (ref EosAuth.LogoutCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
            var result = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(6));
            if (result != Result.Success && result != Result.NotFound && result != Result.InvalidUser)
            {
                authOk = false;
                _log.Warn($"EOS auth logout failed: {result}");
            }
        }

        if (_localUserId != null && _localUserId.IsValid())
        {
            var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new LogoutOptions { LocalUserId = _localUserId };
            _connect.Logout(ref options, tcs, (ref LogoutCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
            var result = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(6));
            if (result != Result.Success && result != Result.NotFound && result != Result.InvalidUser)
            {
                connectOk = false;
                _log.Warn($"EOS connect logout failed: {result}");
            }
        }

        ResetLoginState();
        return (authOk && connectOk, authOk && connectOk ? null : "EOS logout failed.");
    }

    private async Task<(bool Ok, string? Error)> DeletePersistentAuthInternalAsync()
    {
        if (_auth == null)
            return (false, "EOS not initialized.");

        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new EosAuth.DeletePersistentAuthOptions { RefreshToken = null };
        _auth.DeletePersistentAuth(ref options, tcs, (ref EosAuth.DeletePersistentAuthCallbackInfo info) =>
            tcs.TrySetResult(info.ResultCode));

        var result = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(6));
        if (result != Result.Success)
        {
            _log.Warn($"EOS delete persistent auth failed: {result}");
            return (false, $"Delete persistent auth failed: {result}");
        }

        _log.Info("EOS persistent auth cleared.");
        return (true, null);
    }

    private void ResetLoginState()
    {
        _localUserId = null;
        _epicAccountId = null;
        _epicDisplayName = null;
        _deviceLoginStarted = false;
        _epicLoginStarted = false;
        _allowDeviceFallback = false;
        _epicAuthStage = EpicAuthStage.None;
        lock (_displayNameCache)
        {
            _displayNameCache.Clear();
            _displayNameCacheStamp.Clear();
        }
    }

    private Task<Result> QueryFriendsInternalAsync()
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryFriendsOptions { LocalUserId = _epicAccountId };
        _friends!.QueryFriends(ref options, tcs, (ref QueryFriendsCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<Result> QueryPresenceInternalAsync(EpicAccountId targetUserId)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryPresenceOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        _presence!.QueryPresence(ref options, tcs, (ref QueryPresenceCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<Result> QueryUserInfoInternalAsync(EpicAccountId targetUserId)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryUserInfoOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        _userInfo!.QueryUserInfo(ref options, tcs, (ref QueryUserInfoCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<UserInfoLookupResult> QueryUserInfoByDisplayNameInternalAsync(string displayName)
    {
        var tcs = new TaskCompletionSource<UserInfoLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryUserInfoByDisplayNameOptions { LocalUserId = _epicAccountId, DisplayName = displayName };
        _userInfo!.QueryUserInfoByDisplayName(ref options, tcs, (ref QueryUserInfoByDisplayNameCallbackInfo info) =>
        {
            tcs.TrySetResult(new UserInfoLookupResult { Result = info.ResultCode, TargetUserId = info.TargetUserId });
        });
        return tcs.Task;
    }

    private Task<Result> SendInviteInternalAsync(EpicAccountId targetUserId)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SendInviteOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        _friends!.SendInvite(ref options, tcs, (ref SendInviteCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<Result> AcceptInviteInternalAsync(EpicAccountId targetUserId)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AcceptInviteOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        _friends!.AcceptInvite(ref options, tcs, (ref AcceptInviteCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<Result> RejectInviteInternalAsync(EpicAccountId targetUserId)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RejectInviteOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        _friends!.RejectInvite(ref options, tcs, (ref RejectInviteCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private Task<Result> SetPresenceInternalAsync(SetPresenceOptions options)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        _presence!.SetPresence(ref options, tcs, (ref SetPresenceCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
        return tcs.Task;
    }

    private async Task<PresenceSnapshot> GetPresenceSnapshotAsync(EpicAccountId targetUserId)
    {
        var snapshot = new PresenceSnapshot
        {
            Status = "offline",
            IsHosting = false,
            WorldName = null,
            JoinInfo = null
        };

        if (_presence == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return snapshot;

        var queryResult = await WaitWithTimeout(QueryPresenceInternalAsync(targetUserId), TimeSpan.FromSeconds(6));
        if (queryResult == Result.Success)
        {
            var copyOptions = new CopyPresenceOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
            var copyResult = _presence.CopyPresence(ref copyOptions, out var info);
            if (copyResult == Result.Success && info.HasValue)
            {
                snapshot.Status = ToPresenceLabel(info.Value.Status);
                snapshot.WorldName = TryGetPresenceRecord(info.Value.Records, PresenceKeyWorld);
                var hostingRecord = TryGetPresenceRecord(info.Value.Records, PresenceKeyHosting);
                if (string.Equals(hostingRecord, "1", StringComparison.OrdinalIgnoreCase))
                    snapshot.IsHosting = true;
            }
        }
        else if (queryResult != Result.NotFound)
        {
            _log.Warn($"EOS presence query failed: {queryResult}");
        }

        var joinInfo = TryGetJoinInfo(targetUserId);
        if (!string.IsNullOrWhiteSpace(joinInfo))
        {
            snapshot.JoinInfo = joinInfo;
            snapshot.IsHosting = true;
        }

        return snapshot;
    }

    private string? TryGetJoinInfo(EpicAccountId targetUserId)
    {
        if (_presence == null || _epicAccountId == null || !_epicAccountId.IsValid())
            return null;

        var options = new GetJoinInfoOptions { LocalUserId = _epicAccountId, TargetUserId = targetUserId };
        var joinResult = _presence.GetJoinInfo(ref options, out var joinInfo);
        if (joinResult != Result.Success)
            return null;

        var value = ((string?)joinInfo)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task<string?> GetDisplayNameAsync(EpicAccountId accountId)
    {
        var key = accountId.ToString();
        if (TryGetCachedDisplayName(key, out var cached))
            return cached;

        var queryResult = await WaitWithTimeout(QueryUserInfoInternalAsync(accountId), TimeSpan.FromSeconds(6));
        if (queryResult != Result.Success && queryResult != Result.NotFound)
            _log.Warn($"EOS user info query failed: {queryResult}");

        var copyOptions = new CopyUserInfoOptions { LocalUserId = _epicAccountId, TargetUserId = accountId };
        var copyResult = _userInfo!.CopyUserInfo(ref copyOptions, out var userInfo);
        if (copyResult != Result.Success || userInfo == null)
            return null;

        var name = ((string?)userInfo.Value.DisplayName)?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            CacheDisplayName(accountId, name);

        return name;
    }

    private void CacheDisplayName(EpicAccountId accountId, string displayName)
    {
        if (accountId == null || string.IsNullOrWhiteSpace(displayName))
            return;

        var key = accountId.ToString();
        lock (_displayNameCache)
        {
            _displayNameCache[key] = displayName;
            _displayNameCacheStamp[key] = DateTime.UtcNow;
        }
    }

    private bool TryGetCachedDisplayName(string accountId, out string displayName)
    {
        lock (_displayNameCache)
        {
            if (_displayNameCache.TryGetValue(accountId, out displayName) &&
                _displayNameCacheStamp.TryGetValue(accountId, out var stamp) &&
                DateTime.UtcNow - stamp < TimeSpan.FromMinutes(5))
            {
                return true;
            }
        }

        displayName = "";
        return false;
    }

    private static async Task<Result> WaitWithTimeout(Task<Result> task, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != task)
            return Result.TimedOut;
        return await task.ConfigureAwait(false);
    }

    private static async Task<(bool TimedOut, T Value)> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != task)
            return (true, default!);
        return (false, await task.ConfigureAwait(false));
    }

    private struct UserInfoLookupResult
    {
        public Result Result { get; set; }
        public EpicAccountId? TargetUserId { get; set; }
    }

    private static string ToPresenceLabel(Status status)
    {
        return status switch
        {
            Status.Online => "online",
            Status.Away => "away",
            Status.ExtendedAway => "away",
            Status.DoNotDisturb => "dnd",
            _ => "offline"
        };
    }

    private static string ToFriendStatusLabel(FriendsStatus status)
    {
        return status switch
        {
            FriendsStatus.Friends => "friends",
            FriendsStatus.InviteSent => "invite_sent",
            FriendsStatus.InviteReceived => "invite_received",
            _ => "not_friends"
        };
    }

    private static string? TryGetPresenceRecord(DataRecord[]? records, string key)
    {
        if (records == null)
            return null;

        for (var i = 0; i < records.Length; i++)
        {
            var recordKey = ((string?)records[i].Key)?.Trim();
            if (!string.Equals(recordKey, key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = ((string?)records[i].Value)?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private struct PresenceSnapshot
    {
        public string Status { get; set; }
        public bool IsHosting { get; set; }
        public string? WorldName { get; set; }
        public string? JoinInfo { get; set; }
    }

    private static string BuildDeviceDisplayName()
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name))
            name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name))
            name = "Player";

        name = name.Trim();
        const int max = ConnectInterface.USERLOGININFO_DISPLAYNAME_MAX_LENGTH;
        if (name.Length > max)
            name = name.Substring(0, max);

        return name;
    }
#endif
}
