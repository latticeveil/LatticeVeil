using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;
using WinClipboard = System.Windows.Forms.Clipboard;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class AddFriendScreen : IScreen
{
    private const int MaxQueryLength = 64;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly Button _addBtn;
    private readonly Button _backBtn;
    private readonly MemoryWebTextureLoader _webTextureLoader;
    private readonly Dictionary<string, AvatarVisualState> _avatarVisuals = new(StringComparer.OrdinalIgnoreCase);

    private Texture2D? _bg;
    private Texture2D? _panel;
    private Texture2D? _selectedBannerTexture;
    private Texture2D? _selectedAvatarTexture;
    private Task<byte[]?>? _selectedBannerBytesTask;
    private Task<byte[]?>? _selectedAvatarBytesTask;
    private string _selectedBannerUrl = string.Empty;
    private string _selectedAvatarUrl = string.Empty;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _inputRect;
    private Rectangle _resultsRect;
    private Rectangle _viewerRect;
    private Rectangle _viewerBannerRect;
    private Rectangle _viewerAvatarRect;
    private Rectangle _viewerBodyRect;

    private bool _inputActive = true;
    private bool _inputSelectAll;
    private bool _busy;
    private bool _searchInFlight;
    private string _query = string.Empty;
    private string _status = string.Empty;
    private string _lastSearchedQuery = string.Empty;
    private Color _statusColor = Color.White;
    private readonly List<GateIdentityUser> _searchResults = new();
    private GateIdentityUser? _selectedUser;
    private CancellationTokenSource? _searchCts;
    private double _now;
    private double _statusUntil;

    public AddFriendScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eos)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _webTextureLoader = new MemoryWebTextureLoader();

        _addBtn = new Button("SEND REQUEST", () => _ = SendRequestAsync()) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/Profile_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Profile_GUI.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch
        {
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var margin = 96;
        var contentRect = new Rectangle(_panelRect.X + margin, _panelRect.Y + margin, _panelRect.Width - margin * 2, _panelRect.Height - margin * 2);

        var titleH = _font.LineHeight + 12;
        var inputH = _font.LineHeight + 14;
        _inputRect = new Rectangle(contentRect.X, contentRect.Y + titleH + 20, contentRect.Width, inputH);

        var sectionTop = _inputRect.Bottom + 20;
        var sectionHeight = contentRect.Bottom - sectionTop - 60;
        var splitGap = 14;
        var leftWidth = Math.Max(280, (contentRect.Width * 42) / 100);
        _resultsRect = new Rectangle(contentRect.X, sectionTop, leftWidth, sectionHeight);
        _viewerRect = new Rectangle(_resultsRect.Right + splitGap, sectionTop, contentRect.Right - (_resultsRect.Right + splitGap), sectionHeight);

        _viewerBannerRect = new Rectangle(_viewerRect.X + 8, _viewerRect.Y + 8, _viewerRect.Width - 16, Math.Min(150, _viewerRect.Height / 3));
        var avatarSize = Math.Min(96, _viewerBannerRect.Height - 16);
        _viewerAvatarRect = new Rectangle(_viewerBannerRect.X + 10, _viewerBannerRect.Bottom - (avatarSize / 2) - 8, avatarSize, avatarSize);
        _viewerBodyRect = new Rectangle(_viewerRect.X + 8, _viewerBannerRect.Bottom + 10, _viewerRect.Width - 16, _viewerRect.Bottom - (_viewerBannerRect.Bottom + 18));

        var buttonH = Math.Max(44, _font.LineHeight * 2);
        var buttonW = 230;
        _addBtn.Bounds = new Rectangle(contentRect.X, contentRect.Bottom - buttonH, buttonW, buttonH);

        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(viewport.X + backBtnMargin, viewport.Bottom - backBtnMargin - backBtnH, backBtnW, backBtnH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;

        ProcessCompletedImageLoads();

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            TextFieldVisuals.HandleWholeFieldPointerSelection(input, _inputRect, ref _inputActive, ref _inputSelectAll, _query);
            HandleResultSelection(input.MousePosition);
        }

        if (_inputActive && !string.IsNullOrEmpty(_query) && input.IsLeftDragActive() && _inputRect.Contains(input.MousePosition))
            _inputSelectAll = true;

        if (_inputActive && !_busy)
        {
            var before = _query;
            HandleTextInput(input, ref _query, MaxQueryLength);
            if (!string.Equals(before, _query, StringComparison.Ordinal))
                QueueSearch();
            if (input.IsNewKeyPress(Keys.Enter))
                _ = SendRequestAsync();
        }

        _addBtn.Enabled = !_busy && _selectedUser != null;
        _addBtn.Update(input);
        _backBtn.Update(input);

        if (_statusUntil > 0 && _now > _statusUntil)
        {
            _statusUntil = 0;
            _status = string.Empty;
        }
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);
        if (_bg is not null)
            sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, Color.Black);
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));
            DrawBorder(sb, _panelRect, Color.White);
        }

        var title = "ADD FRIEND";
        var tSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_panelRect.Center.X - tSize.X / 2f, _panelRect.Y + 96), Color.White);

        var labelPos = new Vector2(_inputRect.X, _inputRect.Y - _font.LineHeight - 6);
        _font.DrawString(sb, "SEARCH USERNAMES", labelPos, Color.White);

        sb.Draw(_pixel, _inputRect, _inputActive ? new Color(35, 35, 35, 230) : new Color(20, 20, 20, 230));
        DrawBorder(sb, _inputRect, Color.White);

        var queryText = string.IsNullOrWhiteSpace(_query) ? "(type here...)" : _query;
        var queryColor = string.IsNullOrWhiteSpace(_query) ? new Color(120, 120, 120) : Color.White;
        var queryPos = new Vector2(_inputRect.X + 8, _inputRect.Y + 6);
        TextFieldVisuals.DrawWholeFieldSelection(sb, _pixel, _font, _query, queryPos, _inputRect, _inputSelectAll, new Color(88, 148, 218, 170));
        _font.DrawString(sb, queryText, queryPos, queryColor);
        TextFieldVisuals.DrawCaret(sb, _pixel, _font, _query, queryPos, _inputRect, _inputActive, _inputSelectAll, _now, Color.White);

        DrawSearchResults(sb);
        DrawViewerPanel(sb);

        _addBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var pos = new Vector2(_panelRect.X + 96, _panelRect.Bottom - 96 - _font.LineHeight);
            _font.DrawString(sb, _status, pos, _statusColor);
        }

        sb.End();
    }

    private async Task SendRequestAsync()
    {
        if (_busy || _selectedUser == null)
            return;

        var gate = OnlineGateClient.GetOrCreate();
        _busy = true;
        SetStatus("Sending request...");

        try
        {
            var result = await gate.AddFriendAsync(_selectedUser).ConfigureAwait(false);
            if (result.Ok)
            {
                await SyncLocalFriendsCacheAsync(gate).ConfigureAwait(false);
                SetStatus("REQUEST SENT", seconds: 5.0, color: new Color(80, 220, 120));
                _query = string.Empty;
                _selectedUser = null;
                _searchResults.Clear();
                _lastSearchedQuery = string.Empty;
                ClearSelectedViewerAssets();
                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(result.Message) ? "Failed to send request." : result.Message, color: new Color(220, 60, 60));
        }
        catch (Exception ex)
        {
            _log.Error($"AddFriendScreen error: {ex.Message}");
            SetStatus("An error occurred.", color: new Color(220, 60, 60));
        }
        finally
        {
            _busy = false;
        }
    }

    private void QueueSearch()
    {
        _selectedUser = null;
        ClearSelectedViewerAssets();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _ = SearchAsync(_query, _searchCts.Token);
    }

    private async Task SearchAsync(string rawQuery, CancellationToken ct)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchResults.Clear();
            _lastSearchedQuery = string.Empty;
            _searchInFlight = false;
            return;
        }

        try
        {
            _searchInFlight = true;
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;

            var gate = OnlineGateClient.GetOrCreate();
            var result = await gate.SearchVeilnetUsersAsync(query, 8, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;

            _searchResults.Clear();
            _lastSearchedQuery = query;
            if (result.Ok)
            {
                _searchResults.AddRange(result.Users);
                EnsureAvatarLoads();
            }
            else
            {
                SetStatus(string.IsNullOrWhiteSpace(result.Message) ? "Search failed." : result.Message, color: new Color(220, 60, 60));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                _searchInFlight = false;
        }
    }

    private void HandleResultSelection(Point mousePosition)
    {
        if (!_resultsRect.Contains(mousePosition))
            return;

        var rowHeight = Math.Max(56, _font.LineHeight + 24);
        var startY = _resultsRect.Y + 34;
        var relY = mousePosition.Y - startY;
        if (relY < 0)
            return;

        var index = relY / rowHeight;
        if (index < 0 || index >= _searchResults.Count)
            return;

        _selectedUser = _searchResults[index];
        EnsureSelectedViewerLoads();
    }

    private void DrawSearchResults(SpriteBatch sb)
    {
        sb.Draw(_pixel, _resultsRect, new Color(20, 20, 20, 190));
        DrawBorder(sb, _resultsRect, new Color(100, 100, 100));
        _font.DrawString(sb, "SEARCH RESULTS", new Vector2(_resultsRect.X + 8, _resultsRect.Y + 8), Color.White);

        var rowHeight = Math.Max(56, _font.LineHeight + 24);
        var y = _resultsRect.Y + 34;

        if (_searchResults.Count == 0)
        {
            var text = string.IsNullOrWhiteSpace(_query)
                ? "Search results will appear here."
                : string.Equals(_lastSearchedQuery, _query.Trim(), StringComparison.Ordinal) && !_searchInFlight
                    ? "No matching players found."
                    : "Searching...";
            _font.DrawString(sb, text, new Vector2(_resultsRect.X + 8, y), new Color(180, 180, 180));
            return;
        }

        for (var i = 0; i < _searchResults.Count; i++)
        {
            var user = _searchResults[i];
            var row = new Rectangle(_resultsRect.X + 6, y, _resultsRect.Width - 12, rowHeight - 6);
            var selected = _selectedUser != null
                && string.Equals(_selectedUser.ProductUserId, user.ProductUserId, StringComparison.OrdinalIgnoreCase);

            sb.Draw(_pixel, row, selected ? new Color(60, 90, 130, 220) : new Color(26, 26, 26, 200));
            DrawBorder(sb, row, selected ? new Color(200, 230, 255) : new Color(110, 110, 110));

            var avatarRect = new Rectangle(row.X + 8, row.Y + 6, row.Height - 12, row.Height - 12);
            DrawAvatar(sb, user, avatarRect);

            var textX = avatarRect.Right + 10;
            _font.DrawString(sb, ResolveUsername(user), new Vector2(textX, row.Y + 8), Color.White);
            _font.DrawString(sb, "Select to preview profile.", new Vector2(textX, row.Y + 8 + _font.LineHeight), new Color(180, 188, 205));

            y += rowHeight;
            if (y + rowHeight > _resultsRect.Bottom)
                break;
        }
    }

    private void DrawViewerPanel(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewerRect, new Color(20, 20, 20, 190));
        DrawBorder(sb, _viewerRect, new Color(100, 100, 100));

        if (_selectedUser == null)
        {
            _font.DrawString(sb, "PROFILE VIEWER", new Vector2(_viewerRect.X + 8, _viewerRect.Y + 8), Color.White);
            _font.DrawString(sb, "Select a search result to preview the profile.", new Vector2(_viewerRect.X + 8, _viewerRect.Y + 36), new Color(180, 180, 180));
            return;
        }

        if (_selectedBannerTexture != null)
            DrawTextureCover(sb, _selectedBannerTexture, _viewerBannerRect, Color.White);
        else
            sb.Draw(_pixel, _viewerBannerRect, new Color(42, 52, 72, 220));
        DrawBorder(sb, _viewerBannerRect, new Color(160, 170, 190));

        var bannerOverlay = new Rectangle(_viewerBannerRect.X, _viewerBannerRect.Y, _viewerBannerRect.Width, _viewerBannerRect.Height);
        sb.Draw(_pixel, bannerOverlay, new Color(0, 0, 0, _selectedBannerTexture != null ? 70 : 40));

        if (_selectedAvatarTexture != null)
            sb.Draw(_selectedAvatarTexture, _viewerAvatarRect, Color.White);
        else
            DrawBadge(sb, _viewerAvatarRect, ResolveUsername(_selectedUser), new Color(74, 150, 170));
        DrawBorder(sb, _viewerAvatarRect, Color.White);

        var namePos = new Vector2(_viewerAvatarRect.Right + 12, _viewerBannerRect.Bottom - _font.LineHeight - 12);
        _font.DrawString(sb, ResolveUsername(_selectedUser), namePos, Color.White);

        var bodyTop = Math.Max(_viewerBodyRect.Y + 8, _viewerAvatarRect.Bottom + 10);
        _font.DrawString(sb, "ABOUT", new Vector2(_viewerBodyRect.X + 8, bodyTop), new Color(190, 220, 245));
        var bodyTextRect = new Rectangle(_viewerBodyRect.X + 8, bodyTop + _font.LineHeight + 8, _viewerBodyRect.Width - 16, Math.Max(0, _viewerBodyRect.Bottom - (bodyTop + _font.LineHeight + 12)));
        DrawWrappedText(sb, string.IsNullOrWhiteSpace(_selectedUser.AboutMe) ? "No profile bio available yet." : NormalizeMultiline(_selectedUser.AboutMe), bodyTextRect, new Color(198, 206, 228));
    }

    private void EnsureAvatarLoads()
    {
        for (var i = 0; i < _searchResults.Count; i++)
        {
            var user = _searchResults[i];
            var id = (user.ProductUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(user.PictureUrl))
                continue;

            if (!_avatarVisuals.TryGetValue(id, out var state))
            {
                state = new AvatarVisualState();
                _avatarVisuals[id] = state;
            }

            if (state.Texture == null && state.BytesTask == null)
                state.BytesTask = _webTextureLoader.DownloadImageBytesAsync(user.PictureUrl);
        }
    }

    private void EnsureSelectedViewerLoads()
    {
        if (_selectedUser == null)
            return;

        var nextAvatarUrl = (_selectedUser.PictureUrl ?? string.Empty).Trim();
        if (!string.Equals(nextAvatarUrl, _selectedAvatarUrl, StringComparison.OrdinalIgnoreCase))
        {
            _selectedAvatarTexture?.Dispose();
            _selectedAvatarTexture = null;
            _selectedAvatarBytesTask = string.IsNullOrWhiteSpace(nextAvatarUrl) ? null : _webTextureLoader.DownloadImageBytesAsync(nextAvatarUrl);
            _selectedAvatarUrl = nextAvatarUrl;
        }

        var nextBannerUrl = (_selectedUser.BannerUrl ?? string.Empty).Trim();
        if (!string.Equals(nextBannerUrl, _selectedBannerUrl, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBannerTexture?.Dispose();
            _selectedBannerTexture = null;
            _selectedBannerBytesTask = string.IsNullOrWhiteSpace(nextBannerUrl) ? null : _webTextureLoader.DownloadImageBytesAsync(nextBannerUrl);
            _selectedBannerUrl = nextBannerUrl;
        }
    }

    private void ProcessCompletedImageLoads()
    {
        if (_graphics?.GraphicsDevice == null)
            return;

        foreach (var pair in _avatarVisuals)
        {
            var state = pair.Value;
            if (state.BytesTask == null || !state.BytesTask.IsCompleted)
                continue;

            if (state.BytesTask.Status == TaskStatus.RanToCompletion)
            {
                var bytes = state.BytesTask.Result;
                if (bytes is { Length: > 0 })
                    state.Texture = CreateCircularAvatarTextureFromBytes(bytes);
            }

            state.BytesTask = null;
        }

        if (_selectedAvatarBytesTask != null && _selectedAvatarBytesTask.IsCompleted)
        {
            if (_selectedAvatarBytesTask.Status == TaskStatus.RanToCompletion)
            {
                var bytes = _selectedAvatarBytesTask.Result;
                if (bytes is { Length: > 0 })
                    _selectedAvatarTexture = CreateCircularAvatarTextureFromBytes(bytes);
            }

            _selectedAvatarBytesTask = null;
        }

        if (_selectedBannerBytesTask != null && _selectedBannerBytesTask.IsCompleted)
        {
            if (_selectedBannerBytesTask.Status == TaskStatus.RanToCompletion)
            {
                var bytes = _selectedBannerBytesTask.Result;
                if (bytes is { Length: > 0 })
                    _selectedBannerTexture = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, bytes);
            }

            _selectedBannerBytesTask = null;
        }
    }

    private void DrawAvatar(SpriteBatch sb, GateIdentityUser user, Rectangle rect)
    {
        var id = (user.ProductUserId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(id)
            && _avatarVisuals.TryGetValue(id, out var state)
            && state.Texture != null)
        {
            sb.Draw(state.Texture, rect, Color.White);
            return;
        }

        DrawBadge(sb, rect, ResolveUsername(user), ResolveBadgeColor(ResolveUsername(user)));
    }

    private void DrawBadge(SpriteBatch sb, Rectangle rect, string seed, Color fill)
    {
        sb.Draw(_pixel, rect, fill);
        DrawBorder(sb, rect, Color.White);
        var glyph = string.IsNullOrWhiteSpace(seed) ? "?" : seed[..1].ToUpperInvariant();
        var glyphPos = new Vector2(rect.Center.X - (_font.MeasureString(glyph).X / 2f), rect.Center.Y - (_font.LineHeight / 2f));
        _font.DrawString(sb, glyph, glyphPos, Color.White);
    }

    private void DrawTextureCover(SpriteBatch sb, Texture2D texture, Rectangle dest, Color color)
    {
        if (texture.Width <= 0 || texture.Height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return;

        var scale = Math.Max(dest.Width / (float)texture.Width, dest.Height / (float)texture.Height);
        var sourceWidth = Math.Max(1, (int)Math.Round(dest.Width / scale));
        var sourceHeight = Math.Max(1, (int)Math.Round(dest.Height / scale));
        sourceWidth = Math.Min(sourceWidth, texture.Width);
        sourceHeight = Math.Min(sourceHeight, texture.Height);
        var sourceX = Math.Max(0, (texture.Width - sourceWidth) / 2);
        var sourceY = Math.Max(0, (texture.Height - sourceHeight) / 2);
        var source = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight);
        sb.Draw(texture, dest, source, color);
    }

    private Texture2D? CreateCircularAvatarTextureFromBytes(byte[] imageBytes)
    {
        var source = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, imageBytes);
        if (source == null)
            return null;

        try
        {
            return CreateCircularAvatarTexture(source);
        }
        finally
        {
            source.Dispose();
        }
    }

    private Texture2D? CreateCircularAvatarTexture(Texture2D source)
    {
        try
        {
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var side = Math.Min(sourceWidth, sourceHeight);
            if (side <= 1)
                return null;

            var sourcePixels = new Color[sourceWidth * sourceHeight];
            source.GetData(sourcePixels);

            var cropX = (sourceWidth - side) / 2;
            var cropY = (sourceHeight - side) / 2;
            var output = new Color[side * side];
            var center = (side - 1) / 2f;
            var radius = side / 2f;
            var fadeStart = Math.Max(0f, radius - 1.5f);

            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var srcIndex = (cropY + y) * sourceWidth + (cropX + x);
                    var color = sourcePixels[srcIndex];
                    var dx = x - center;
                    var dy = y - center;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist >= radius)
                    {
                        color.A = 0;
                    }
                    else if (dist > fadeStart)
                    {
                        var t = 1f - ((dist - fadeStart) / (radius - fadeStart));
                        color.A = (byte)Math.Clamp((int)(color.A * t), 0, 255);
                    }

                    output[y * side + x] = color;
                }
            }

            var texture = new Texture2D(_graphics.GraphicsDevice, side, side, false, SurfaceFormat.Color);
            texture.SetData(output);
            return texture;
        }
        catch (Exception ex)
        {
            _log.Warn($"Circular avatar conversion failed: {ex.Message}");
            return null;
        }
    }

    private void ClearSelectedViewerAssets()
    {
        _selectedBannerTexture?.Dispose();
        _selectedBannerTexture = null;
        _selectedAvatarTexture?.Dispose();
        _selectedAvatarTexture = null;
        _selectedBannerBytesTask = null;
        _selectedAvatarBytesTask = null;
        _selectedBannerUrl = string.Empty;
        _selectedAvatarUrl = string.Empty;
    }

    private void SetStatus(string msg, double seconds = 4.0, Color? color = null)
    {
        _status = msg;
        _statusColor = color ?? Color.White;
        _statusUntil = _now + seconds;
    }

    private async Task SyncLocalFriendsCacheAsync(OnlineGateClient gate)
    {
        try
        {
            var friendsResult = await gate.GetFriendsAsync().ConfigureAwait(false);
            if (!friendsResult.Ok)
                return;

            var merged = new List<PlayerProfile.FriendEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in friendsResult.Friends)
            {
                var userId = (friend.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(userId) || !seen.Add(userId))
                    continue;

                var label = (friend.Username ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label))
                    label = (friend.DisplayName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                if (Guid.TryParse(label, out _))
                    continue;

                merged.Add(new PlayerProfile.FriendEntry
                {
                    UserId = userId,
                    Label = label,
                    LastKnownDisplayName = label,
                    LastKnownPresence = string.Empty
                });
            }

            _profile.Friends = merged;
            _profile.Save(_log);
        }
        catch (Exception ex)
        {
            _log.Warn($"AddFriendScreen: friend cache sync failed: {ex.Message}");
        }
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var ctrl = input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl);
        if (ctrl)
        {
            if (input.IsNewKeyPress(Keys.A))
            {
                _inputSelectAll = true;
                return;
            }

            if (input.IsNewKeyPress(Keys.C))
            {
                if (!string.IsNullOrEmpty(value))
                    WinClipboard.SetText(value);
                return;
            }

            if (input.IsNewKeyPress(Keys.X))
            {
                if (!string.IsNullOrEmpty(value))
                    WinClipboard.SetText(value);
                value = string.Empty;
                _inputSelectAll = false;
                return;
            }

            if (input.IsNewKeyPress(Keys.V))
            {
                var clip = (WinClipboard.GetText() ?? string.Empty).Replace("\r", "").Replace("\n", "");
                clip = new string(clip.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                if (_inputSelectAll)
                    value = string.Empty;
                if (value.Length < maxLen)
                    value += clip[..Math.Min(clip.Length, maxLen - value.Length)];
                _inputSelectAll = false;
                return;
            }
        }

        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetTextInputKeys())
        {
            if (key == Keys.Back)
            {
                if (_inputSelectAll)
                {
                    value = string.Empty;
                    _inputSelectAll = false;
                }
                else if (value.Length > 0)
                    value = value.Substring(0, value.Length - 1);
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                if (_inputSelectAll) value = string.Empty;
                if (value.Length < maxLen) value += shift ? '_' : '-';
                _inputSelectAll = false;
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                if (_inputSelectAll) value = string.Empty;
                if (value.Length < maxLen) value += (char)('0' + (key - Keys.D0));
                _inputSelectAll = false;
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                if (_inputSelectAll) value = string.Empty;
                if (value.Length < maxLen) value += (char)('0' + (key - Keys.NumPad0));
                _inputSelectAll = false;
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                if (_inputSelectAll) value = string.Empty;
                var c = (char)('A' + (key - Keys.A));
                if (!shift) c = char.ToLowerInvariant(c);
                if (value.Length < maxLen) value += c;
                _inputSelectAll = false;
            }
        }
    }

    private void DrawWrappedText(SpriteBatch sb, string text, Rectangle rect, Color color)
    {
        var wrapped = WrapText(text, Math.Max(80, rect.Width));
        var y = rect.Y;
        for (var i = 0; i < wrapped.Count; i++)
        {
            if (y + _font.LineHeight > rect.Bottom)
                break;
            _font.DrawString(sb, wrapped[i], new Vector2(rect.X, y), color);
            y += _font.LineHeight + 2;
        }
    }

    private static string NormalizeMultiline(string text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n").Trim();
    }

    private List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var content = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var paragraphs = content.Split('\n');
        for (var p = 0; p < paragraphs.Length; p++)
        {
            var paragraph = (paragraphs[p] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = string.Empty;
            for (var w = 0; w < words.Length; w++)
            {
                var word = words[w];
                var candidate = string.IsNullOrWhiteSpace(current) ? word : $"{current} {word}";
                if (_font.MeasureString(candidate).X <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(current))
                    lines.Add(current);
                current = word;
            }

            if (!string.IsNullOrWhiteSpace(current))
                lines.Add(current);
        }

        return lines;
    }

    private static string ResolveUsername(GateIdentityUser user)
    {
        var username = (user.Username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = (user.DisplayName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(username) ? "PLAYER" : username;
    }

    private static string TrimPreview(string text, int maxChars)
    {
        var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= maxChars)
            return value;
        return value[..Math.Max(0, maxChars - 3)] + "...";
    }

    private static Color ResolveBadgeColor(string seed)
    {
        var text = string.IsNullOrWhiteSpace(seed) ? "friend" : seed.Trim().ToLowerInvariant();
        var hash = 17;
        for (var i = 0; i < text.Length; i++)
            hash = (hash * 31) + text[i];

        var palette = new[]
        {
            new Color(88, 114, 214),
            new Color(98, 168, 122),
            new Color(184, 123, 82),
            new Color(147, 102, 196),
            new Color(74, 150, 170)
        };

        return palette[Math.Abs(hash) % palette.Length];
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private sealed class AvatarVisualState
    {
        public Task<byte[]?>? BytesTask;
        public Texture2D? Texture;
    }
}
