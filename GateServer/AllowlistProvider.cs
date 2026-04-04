using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed class AllowlistProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _refreshInterval;
    private AllowlistSnapshot _snapshot = AllowlistSnapshot.Empty("startup");

    private readonly string _githubRepo;
    private readonly string _githubPath;
    private readonly string _githubBranch;
    private readonly string _githubToken;
    private readonly string _allowlistSource;
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
        _githubRepo = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_REPO") ?? "").Trim();
        _githubPath = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_PATH") ?? "allowlist.json").Trim();
        _githubBranch = (Environment.GetEnvironmentVariable("GITHUB_ALLOWLIST_BRANCH") ?? "main").Trim();
        _githubToken = (Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "").Trim();
        _allowlistSource = (Environment.GetEnvironmentVariable("ALLOWLIST_SOURCE") ?? "env").Trim();
        _envProofTokens = (Environment.GetEnvironmentVariable("ALLOWLIST_PROOF_TOKENS") ?? "").Trim();
        _envAllowedClientHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_CLIENT_EXE_SHA256") ?? "").Trim();
        _envAllowedDevHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_DEV_EXE_SHA256") ?? "").Trim();
        _envAllowedReleaseHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_ALLOWED_RELEASE_EXE_SHA256") ?? "").Trim();
        _envLegacyHashes = (Environment.GetEnvironmentVariable("ALLOWLIST_EXE_SHA256") ?? "").Trim();
        _envMinVersion = (Environment.GetEnvironmentVariable("ALLOWLIST_MIN_VERSION") ?? "").Trim();
        var refreshSeconds = 30;
        if (int.TryParse(Environment.GetEnvironmentVariable("ALLOWLIST_REFRESH_SECONDS"), out var parsed)) refreshSeconds = Math.Max(5, parsed);
        _refreshInterval = TimeSpan.FromSeconds(refreshSeconds);
    }

    public RuntimeAllowlistView GetRuntimeView()
    {
        lock (_runtimeSync) return BuildRuntimeViewUnsafe();
    }

    public RuntimeAllowlistAdminResult UpdateRuntimeAllowlist(string operation, string? applyMode, AllowlistModel model)
    {
        var op = (operation ?? "replace").Trim().ToLowerInvariant();
        var mode = (applyMode ?? "").Trim().ToLowerInvariant() == "merge" ? RuntimeApplyMode.Merge : RuntimeApplyMode.ReplaceSource;
        lock (_runtimeSync)
        {
            if (op == "clear") { _runtimeModel = null; _runtimeApplyMode = mode; _runtimeUpdatedUtc = DateTime.MinValue; return new RuntimeAllowlistAdminResult(true, "Cleared", BuildRuntimeViewUnsafe()); }
            if (op == "replace") { _runtimeModel = model; _runtimeApplyMode = mode; _runtimeUpdatedUtc = DateTime.UtcNow; return new RuntimeAllowlistAdminResult(true, "Replaced", BuildRuntimeViewUnsafe()); }
            if (op == "merge") { _runtimeModel ??= new AllowlistModel(); MergeModels(_runtimeModel, model); _runtimeApplyMode = mode; _runtimeUpdatedUtc = DateTime.UtcNow; return new RuntimeAllowlistAdminResult(true, "Merged", BuildRuntimeViewUnsafe()); }
            return new RuntimeAllowlistAdminResult(false, "Unknown op", BuildRuntimeViewUnsafe());
        }
    }

    public RuntimeAllowlistAdminResult SetRuntimeCurrentHash(string? hash, string? target, bool replaceTargetList, bool clearOtherHashes, string? applyMode)
    {
        var h = hash?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(h) || h.Length != 64) return new RuntimeAllowlistAdminResult(false, "Invalid hash", GetRuntimeView());
        var mode = (applyMode ?? "").Trim().ToLowerInvariant() == "merge" ? RuntimeApplyMode.Merge : RuntimeApplyMode.ReplaceSource;
        lock (_runtimeSync)
        {
            _runtimeModel ??= new AllowlistModel();
            if (clearOtherHashes) { _runtimeModel.AllowedClientExeSha256.Clear(); _runtimeModel.AllowedDevExeSha256.Clear(); _runtimeModel.AllowedReleaseExeSha256.Clear(); _runtimeModel.ExeSha256.Clear(); }
            var t = (target ?? "release").Trim().ToLowerInvariant();
            var list = t switch { "client" or "any" => _runtimeModel.AllowedClientExeSha256, "dev" => _runtimeModel.AllowedDevExeSha256, _ => _runtimeModel.AllowedReleaseExeSha256 };
            if (replaceTargetList) list.Clear();
            if (!list.Contains(h, StringComparer.OrdinalIgnoreCase)) list.Add(h);
            _runtimeApplyMode = mode; _runtimeUpdatedUtc = DateTime.UtcNow;
            return new RuntimeAllowlistAdminResult(true, $"Updated {t}", BuildRuntimeViewUnsafe());
        }
    }

    private static void MergeModels(AllowlistModel t, AllowlistModel i)
    {
        t.ProofTokens = (t.ProofTokens ?? new()).Concat(i.ProofTokens ?? new()).Distinct().ToList();
        t.AllowedClientExeSha256 = (t.AllowedClientExeSha256 ?? new()).Concat(i.AllowedClientExeSha256 ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        t.AllowedDevExeSha256 = (t.AllowedDevExeSha256 ?? new()).Concat(i.AllowedDevExeSha256 ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        t.AllowedReleaseExeSha256 = (t.AllowedReleaseExeSha256 ?? new()).Concat(i.AllowedReleaseExeSha256 ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        t.ExeSha256 = (t.ExeSha256 ?? new()).Concat(i.ExeSha256 ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(i.MinVersion)) t.MinVersion = i.MinVersion;
    }

    public async Task<AllowlistSnapshot> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _snapshot.RefreshedUtc < _refreshInterval) return ApplyRuntimeOverride(_snapshot);
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _snapshot.RefreshedUtc < _refreshInterval) return ApplyRuntimeOverride(_snapshot);
            var loaded = await LoadAllowlistAsync(ct);
            _snapshot = loaded with { RefreshedUtc = DateTime.UtcNow };
            return ApplyRuntimeOverride(_snapshot);
        }
        finally { _refreshLock.Release(); }
    }

    private async Task<AllowlistSnapshot> LoadAllowlistAsync(CancellationToken ct)
    {
        var env = TryLoadFromEnvironment();
        if (_allowlistSource.ToLowerInvariant() == "env") return env ?? AllowlistSnapshot.Unavailable("env", "No env config");
        var github = await TryLoadFromGithubAsync(ct);
        if (github != null && github.IsAvailable)
        {
            if (env != null && env.IsAvailable) return MergeSnapshots(github, env, "env");
            return github;
        }
        return env ?? AllowlistSnapshot.Unavailable("env", "No allowlist available");
    }

    private static AllowlistSnapshot MergeSnapshots(AllowlistSnapshot b, AllowlistSnapshot o, string label)
    {
        var proofs = new HashSet<string>(b.ProofTokens); proofs.UnionWith(o.ProofTokens);
        var hashes = new HashSet<string>(b.ExeSha256, StringComparer.OrdinalIgnoreCase); hashes.UnionWith(o.ExeSha256);
        var min = (string.IsNullOrWhiteSpace(o.MinVersion) || o.MinVersion == "0.0.0") ? b.MinVersion : o.MinVersion;
        return new AllowlistSnapshot(proofs, hashes, min, $"{b.Source}+{label}", DateTime.UtcNow, true, "");
    }

    private AllowlistSnapshot ApplyRuntimeOverride(AllowlistSnapshot b)
    {
        lock (_runtimeSync)
        {
            if (_runtimeModel == null) return b;
            var r = AllowlistSnapshot.FromModel(_runtimeModel, "runtime");
            if (_runtimeApplyMode == RuntimeApplyMode.ReplaceSource) return r with { IsAvailable = true };
            return MergeSnapshots(b, r, "runtime");
        }
    }

    private RuntimeAllowlistView BuildRuntimeViewUnsafe()
    {
        var mode = _runtimeApplyMode == RuntimeApplyMode.Merge ? "merge" : "replace_source";
        if (_runtimeModel == null) return new RuntimeAllowlistView(false, mode, DateTime.MinValue, new(), new(), new(), new(), new(), "0.0.0", 0, 0);
        return new RuntimeAllowlistView(true, mode, _runtimeUpdatedUtc, _runtimeModel.ProofTokens, _runtimeModel.AllowedClientExeSha256, _runtimeModel.AllowedDevExeSha256, _runtimeModel.AllowedReleaseExeSha256, _runtimeModel.ExeSha256, _runtimeModel.MinVersion, _runtimeModel.ExeSha256.Count, _runtimeModel.ProofTokens.Count);
    }

    private AllowlistSnapshot? TryLoadFromEnvironment()
    {
        var model = new AllowlistModel { ProofTokens = ParseList(_envProofTokens), AllowedClientExeSha256 = ParseList(_envAllowedClientHashes), AllowedDevExeSha256 = ParseList(_envAllowedDevHashes), AllowedReleaseExeSha256 = ParseList(_envAllowedReleaseHashes), ExeSha256 = ParseList(_envLegacyHashes), MinVersion = string.IsNullOrWhiteSpace(_envMinVersion) ? "0.0.0" : _envMinVersion };
        if (model.ProofTokens.Count == 0 && model.ExeSha256.Count == 0 && model.AllowedClientExeSha256.Count == 0) return null;
        return AllowlistSnapshot.FromModel(model, "env");
    }

    private static List<string> ParseList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Replace("\r", "\n").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private async Task<AllowlistSnapshot?> TryLoadFromGithubAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_githubRepo) || string.IsNullOrWhiteSpace(_githubToken)) return null;
        try
        {
            var url = $"https://api.github.com/repos/{_githubRepo}/contents/{_githubPath}?ref={_githubBranch}";
            var client = _httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/vnd.github+json");
            req.Headers.Add("User-Agent", "GateServer");
            req.Headers.Add("Authorization", $"Bearer {_githubToken}");
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(ct);
            var github = JsonSerializer.Deserialize<GithubContentResponse>(body, JsonOptions);
            if (github == null || string.IsNullOrWhiteSpace(github.Content)) return null;
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(github.Content.Replace("\n", "").Replace("\r", "")));
            var model = JsonSerializer.Deserialize<AllowlistModel>(json, JsonOptions);
            return model == null ? null : AllowlistSnapshot.FromModel(model, "github");
        }
        catch { return null; }
    }

    private sealed class GithubContentResponse { public string Content { get; set; } = ""; }
    private enum RuntimeApplyMode { Merge, ReplaceSource }
}

sealed class TicketRequest
{
    public string? ProductUserId { get; set; }
    public string? DisplayName { get; set; }
    public string GameVersion { get; set; } = "1.0.0";
    public string Platform { get; set; } = "windows";
    public string BuildFlavor { get; set; } = "release";
    public string? Proof { get; set; }
    public string? ExeHash { get; set; }
    public string? DevKey { get; set; }
    public Dictionary<string, string>? PublicConfigIds { get; set; }
}

sealed class TicketResponse
{
    public bool Ok { get; set; }
    public string? Ticket { get; set; }
    public string? ExpiresUtc { get; set; }
    public string? Reason { get; set; }
    public static TicketResponse Approved(string t, DateTime e) => new() { Ok = true, Ticket = t, ExpiresUtc = e.ToString("O"), Reason = "approved" };
    public static TicketResponse Denied(string r) => new() { Ok = false, Reason = r };
}

sealed record EosConfigPayload(string ProductId, string SandboxId, string DeploymentId, string ClientId, string ClientSecret, string ProductName, string ProductVersion, string LoginMode);

sealed class AllowlistModel
{
    public List<string> ProofTokens { get; set; } = new();
    public List<string> AllowedClientExeSha256 { get; set; } = new();
    public List<string> AllowedDevExeSha256 { get; set; } = new();
    public List<string> AllowedReleaseExeSha256 { get; set; } = new();
    public List<string> ExeSha256 { get; set; } = new();
    public string MinVersion { get; set; } = "0.0.0";
}

sealed record AllowlistSnapshot(HashSet<string> ProofTokens, HashSet<string> ExeSha256, string MinVersion, string Source, DateTime RefreshedUtc, bool IsAvailable, string FailureReason)
{
    public static AllowlistSnapshot Empty(string s) => new(new(), new(StringComparer.OrdinalIgnoreCase), "0.0.0", s, DateTime.MinValue, false, "");
    public static AllowlistSnapshot Unavailable(string s, string r) => new(new(), new(StringComparer.OrdinalIgnoreCase), "0.0.0", s, DateTime.UtcNow, false, r);
    public static AllowlistSnapshot FromModel(AllowlistModel m, string s)
    {
        var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (m.AllowedClientExeSha256 != null) foreach (var x in m.AllowedClientExeSha256) h.Add(x.Trim().ToLowerInvariant());
        if (m.AllowedDevExeSha256 != null) foreach (var x in m.AllowedDevExeSha256) h.Add(x.Trim().ToLowerInvariant());
        if (m.AllowedReleaseExeSha256 != null) foreach (var x in m.AllowedReleaseExeSha256) h.Add(x.Trim().ToLowerInvariant());
        if (m.ExeSha256 != null) foreach (var x in m.ExeSha256) h.Add(x.Trim().ToLowerInvariant());
        return new AllowlistSnapshot(new HashSet<string>(m.ProofTokens ?? new()), h, m.MinVersion ?? "0.0.0", s, DateTime.UtcNow, true, "");
    }
    public bool ContainsProof(string t) => ProofTokens.Contains(t?.Trim() ?? "");
    public bool ContainsHash(string h) => ExeSha256.Contains(h?.Trim().ToLowerInvariant() ?? "");
    public bool IsVersionAllowed(string v) => true;
}

sealed record RuntimeAllowlistView(bool Enabled, string ApplyMode, DateTime UpdatedUtc, List<string> ProofTokens, List<string> AllowedClientExeSha256, List<string> AllowedDevExeSha256, List<string> AllowedReleaseExeSha256, List<string> ExeSha256, string MinVersion, int HashCount, int ProofCount);
sealed record RuntimeAllowlistAdminResult(bool Ok, string Message, RuntimeAllowlistView View);
sealed class RuntimeAllowlistUpdateRequest { public string? Operation { get; set; } public string? ApplyMode { get; set; } public List<string>? ProofTokens { get; set; } public List<string>? AllowedClientExeSha256 { get; set; } public List<string>? AllowedDevExeSha256 { get; set; } public List<string>? AllowedReleaseExeSha256 { get; set; } public List<string>? ExeSha256 { get; set; } public string? MinVersion { get; set; } }
sealed class RuntimeCurrentHashRequest { public string? Hash { get; set; } public string? Target { get; set; } public bool ReplaceTargetList { get; set; } = true; public bool ClearOtherHashes { get; set; } = true; public string? ApplyMode { get; set; } }
sealed record FriendsAddRequest(string Query);
sealed record FriendsRespondRequest(string RequesterProductUserId, bool Accept, bool Block);
sealed record FriendsRemoveRequest(string ProductUserId);
sealed record IdentityResolveRequest(string Query);
