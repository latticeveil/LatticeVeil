using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class SingleplayerScreen : IScreen
{
    private const int ScrollStep = 40;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly PixelFont _rowFont;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly PlayerProfile _profile;

    private Texture2D? _bg;
    private Texture2D? _panel;
    private Texture2D? _deleteIconTexture;

    private readonly Button _createBtn;
    private readonly Button _resumeBtn;
    private readonly Button _backBtn;
    private readonly Button _confirmDeleteBtn;
    private readonly Button _cancelDeleteBtn;
    private GameSettings _settings;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private float _scroll;
    private int _rowHeight;
    private int _selectedIndex = -1;
    private double _now;
    private const double DoubleClickSeconds = 0.35;
    private double _lastClickTime;
    private int _lastClickIndex = -1;
    private string? _statusMessage;
    private double _statusUntil;
    private bool _deleteConfirmOpen;
    private int _deleteConfirmIndex = -1;
    private Rectangle _deleteConfirmRect;
    private Point _overlayMousePos;
    private int _hoverWorldIndex = -1;
    private readonly Dictionary<string, PreviewTextureCacheEntry> _previewTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _previewLoadFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _seedRevealByWorld = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Rectangle> _seedEyeRects = new();
    private readonly Dictionary<int, Rectangle> _rowPlayRects = new();
    private readonly Dictionary<int, Rectangle> _rowDeleteRects = new();

    private List<WorldListEntry> _worlds = new();

    public SingleplayerScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _rowFont = new PixelFont(pixel, scale: 3);
        _pixel = pixel;
        _log = log;
        _graphics = graphics;
        _profile = profile;
        _settings = GameSettings.LoadOrCreate(_log);

        _createBtn = new Button("CREATE WORLD", OpenCreateWorld);
        _resumeBtn = new Button("RESUME GENERATION", ResumeSelectedWorldGeneration);
        _backBtn = new Button("BACK", () => _menus.Pop());
        _confirmDeleteBtn = new Button("DELETE", ConfirmDeleteSelectedWorld) { BoldText = true, BackgroundColor = new Color(100, 26, 26) };
        _cancelDeleteBtn = new Button("CANCEL", CancelDeleteSelectedWorld) { BoldText = true };

        try
        {
            _log.Info("SingleplayerScreen - Loading assets...");
            _bg = _assets.LoadTexture("textures/menu/backgrounds/singleplayer_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Singleplayer_GUI.png");
            _createBtn.Texture = _assets.LoadTexture("textures/menu/buttons/CreateWorld.png");
            _deleteIconTexture = _assets.LoadTexture("textures/menu/buttons/DeleteDefault.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
            
            _log.Info($"SingleplayerScreen - Assets loaded successfully. Panel: {(_panel != null ? "LOADED" : "NULL")}");
        }
        catch (Exception ex)
        {
            _log.Error($"Singleplayer asset load failed: {ex.Message}");
            _log.Error($"Singleplayer asset load stack trace: {ex.StackTrace}");
        }

        RefreshWorlds();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        
        // Log viewport and panel sizing
        _log.Info($"SingleplayerScreen OnResize - Viewport: {viewport.Width}x{viewport.Height}");
        
        var panelW = Math.Min(1248, viewport.Width - 20); // Match settings screen
        var panelH = Math.Min(680, viewport.Height - 30); // Match settings screen
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);
        
        _log.Info($"SingleplayerScreen - Panel: {_panelRect.Width}x{_panelRect.Height} at ({_panelRect.X},{_panelRect.Y})");

        var margin = 24;
        var headerH = _font.LineHeight * 2 + 40;
        var buttonAreaH = Math.Clamp((int)(panelH * 0.2f), 60, 90);
        
        // Calculate proper content area inside 9-patch borders
        const int borderSize = 16; // Must match the borderSize in DrawNinePatch
        var contentArea = new Rectangle(
            _panelRect.X + borderSize,
            _panelRect.Y + borderSize,
            _panelRect.Width - borderSize * 2,
            _panelRect.Height - borderSize * 2
        );
        
        _log.Info($"SingleplayerScreen - ContentArea: {contentArea.Width}x{contentArea.Height} at ({contentArea.X},{contentArea.Y})");
        
        _listRect = new Rectangle(
            contentArea.X + margin + 45,
            contentArea.Y + headerH + 80,
            contentArea.Width - margin * 2 - 90,
            contentArea.Height - headerH - buttonAreaH - margin - 60 - 20);
        
        _log.Info($"SingleplayerScreen - ListRect: {_listRect.Width}x{_listRect.Height} at ({_listRect.X},{_listRect.Y})");

        _rowHeight = Math.Max(_rowFont.LineHeight + 18, 60);  // Make entries shorter vertically

        var gap = 16;

        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH)); // Match options screen
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(
            viewport.X + backBtnMargin, 
            viewport.Bottom - backBtnMargin - backBtnH, 
            backBtnW, 
            backBtnH
        );
        
        // Match the create-world screen footer anchor exactly.
        var createBtnW = Math.Min(320, panelW / 4);
        var createBtnH = Math.Max(44, (int)(createBtnW * 0.22f));
        var createBtnX = viewport.Center.X - createBtnW / 2;
        var createBtnY = viewport.Bottom - 20 - createBtnH;
        _createBtn.Bounds = new Rectangle(createBtnX, createBtnY, createBtnW, createBtnH);

        _resumeBtn.Bounds = new Rectangle(_createBtn.Bounds.X - createBtnW - gap, createBtnY, createBtnW, createBtnH);

        ClampScroll();
        LayoutDeleteConfirmOverlay();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusMessage != null && _now > _statusUntil)
            _statusMessage = null;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            if (_deleteConfirmOpen)
            {
                CancelDeleteSelectedWorld();
                return;
            }

            _menus.Pop();
            return;
        }

        if (_deleteConfirmOpen)
        {
            _confirmDeleteBtn.Update(input);
            _cancelDeleteBtn.Update(input);
            return;
        }

        _createBtn.Update(input);
        SyncActionButtons();
        _resumeBtn.Update(input);
        _backBtn.Update(input);
        _overlayMousePos = input.MousePosition;
        HandleListInput(input);

        if (input.IsNewKeyPress(Keys.Enter))
            JoinSelectedWorld();
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);

        if (_bg is not null)
            sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0));

        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        // Log GUI asset info
        if (_panel != null)
        {
            DrawNinePatch(sb, _panel, _panelRect);
        }
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(25, 25, 25));
        }

        // Account for GUI box borders (16px each side for 9-patch)
        const int borderSize = 16; // Must match the borderSize in DrawNinePatch

        var title = "WORLDS";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16 + borderSize + 80);  // Match CreateWorldScreen positioning + move down 80 pixels total
        _font.DrawString(sb, title, titlePos, Color.White);

        DrawWorldList(sb);

        _createBtn.Draw(sb, _pixel, _font);
        _resumeBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        DrawWorldHoverTooltip(sb);

        if (_deleteConfirmOpen)
            DrawDeleteConfirmOverlay(sb);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var size = _font.MeasureString(_statusMessage);
            var pos = new Vector2(_panelRect.Center.X - size.X / 2f, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _statusMessage, pos, Color.White);
        }

        sb.End();
    }

    private void DrawNinePatch(SpriteBatch sb, Texture2D texture, Rectangle destination)
    {
        if (texture == null) return;
        
        // Define the border sizes (adjust these based on your GUI texture)
        const int borderSize = 16;
        
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        
        // Create the 9 patches
        var patches = new[]
        {
            // Corners
            new Rectangle(source.X, source.Y, borderSize, borderSize), // top-left
            new Rectangle(source.X + borderSize, source.Y, source.Width - borderSize * 2, borderSize), // top-middle
            new Rectangle(source.Right - borderSize, source.Y, borderSize, borderSize), // top-right
            new Rectangle(source.X, source.Y + borderSize, borderSize, source.Height - borderSize * 2), // left-middle
            new Rectangle(source.X + borderSize, source.Y + borderSize, source.Width - borderSize * 2, source.Height - borderSize * 2), // center
            new Rectangle(source.Right - borderSize, source.Y + borderSize, borderSize, source.Height - borderSize * 2), // right-middle
            new Rectangle(source.X, source.Bottom - borderSize, borderSize, borderSize), // bottom-left
            new Rectangle(source.X + borderSize, source.Bottom - borderSize, source.Width - borderSize * 2, borderSize), // bottom-middle
            new Rectangle(source.Right - borderSize, source.Bottom - borderSize, borderSize, borderSize) // bottom-right
        };
        
        var destPatches = new[]
        {
            // Corners
            new Rectangle(destination.X, destination.Y, borderSize, borderSize), // top-left
            new Rectangle(destination.X + borderSize, destination.Y, destination.Width - borderSize * 2, borderSize), // top-middle
            new Rectangle(destination.Right - borderSize, destination.Y, borderSize, borderSize), // top-right
            new Rectangle(destination.X, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2), // left-middle
            new Rectangle(destination.X + borderSize, destination.Y + borderSize, destination.Width - borderSize * 2, destination.Height - borderSize * 2), // center
            new Rectangle(destination.Right - borderSize, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2), // right-middle
            new Rectangle(destination.X, destination.Bottom - borderSize, borderSize, borderSize), // bottom-left
            new Rectangle(destination.X + borderSize, destination.Bottom - borderSize, destination.Width - borderSize * 2, borderSize), // bottom-middle
            new Rectangle(destination.Right - borderSize, destination.Bottom - borderSize, borderSize, borderSize) // bottom-right
        };
        
        // Draw all patches
        for (int i = 0; i < 9; i++)
        {
            if (destPatches[i].Width > 0 && destPatches[i].Height > 0)
                sb.Draw(texture, destPatches[i], patches[i], Color.White);
        }
    }

    private void DrawWorldList(SpriteBatch sb)
    {
        _hoverWorldIndex = -1;
        _seedEyeRects.Clear();
        _rowPlayRects.Clear();
        _rowDeleteRects.Clear();
        if (_worlds.Count == 0)
        {
            var msg = "NO WORLDS FOUND";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_listRect.Center.X - size.X / 2f, _listRect.Center.Y - size.Y / 2f);
            _font.DrawString(sb, msg, pos, Color.White);
            return;
        }

        for (int i = 0; i < _worlds.Count; i++)
        {
            var y = _listRect.Y + i * _rowHeight - (int)Math.Round(_scroll);
            var rowRect = new Rectangle(_listRect.X, y, _listRect.Width, _rowHeight);
            if (rowRect.Bottom < _listRect.Y || rowRect.Y > _listRect.Bottom)
                continue;

            var bg = new Color(20, 20, 20, 180);
            if (i == _selectedIndex)
                bg = new Color(70, 70, 70, 220);
            sb.Draw(_pixel, rowRect, bg);
            DrawBorder(sb, rowRect, Color.White);

            var entry = _worlds[i];
            var actionSize = Math.Max(24, rowRect.Height - 18);
            var deleteRect = new Rectangle(rowRect.Right - 8 - actionSize, rowRect.Y + (rowRect.Height - actionSize) / 2, actionSize, actionSize);
            var playRect = new Rectangle(deleteRect.X - 8 - actionSize, deleteRect.Y, actionSize, actionSize);
            _rowPlayRects[i] = playRect;
            _rowDeleteRects[i] = deleteRect;

            var previewSize = rowRect.Height - 12 - 30;  // Make smaller by an inch (30 pixels)
            var previewRect = new Rectangle(rowRect.X + 6, rowRect.Y + (rowRect.Height - previewSize) / 2, previewSize, previewSize);
            DrawWorldPreviewTile(sb, entry, previewRect);

            var seedSectionWidth = 230; // Always show seed info
            var textLeft = previewRect.Right + 10;
            var textWidth = Math.Max(40, playRect.X - textLeft - 18 - seedSectionWidth);
            var titleText = TruncateToWidth(entry.Name, textWidth, _rowFont);
            var subtitle = $"MODE: {entry.CurrentMode.ToString().ToUpperInvariant()}";
            subtitle = TruncateToWidth(subtitle, textWidth, _font);

            var titlePos = new Vector2(textLeft, rowRect.Y + 6);
            var subtitlePos = new Vector2(textLeft, rowRect.Bottom - _font.LineHeight - 6);
            _rowFont.DrawString(sb, titleText, titlePos, Color.White);
            _font.DrawString(sb, subtitle, subtitlePos, new Color(220, 220, 220));

            var badgeX = textLeft + Math.Max(0, (int)_rowFont.MeasureString(titleText).X + 12);
            if (entry.ValidationStatus == WorldValidationStatus.IncompleteGeneration)
            {
                var badge = new Rectangle(badgeX, rowRect.Y + 8, 124, 22);
                sb.Draw(_pixel, badge, new Color(124, 78, 20, 220));
                DrawBorder(sb, badge, new Color(255, 220, 160));
                _font.DrawString(sb, "INCOMPLETE", new Vector2(badge.X + 8, badge.Y + 4), Color.White);
                badgeX = badge.Right + 8;
            }

            if (entry.StorageBudgetState >= WorldStorageBudgetState.Warn)
            {
                var label = entry.StorageBudgetState >= WorldStorageBudgetState.OverTarget ? "SIZE 1GB+" : "SIZE WARN";
                var badge = new Rectangle(badgeX, rowRect.Y + 8, 116, 22);
                var badgeBg = entry.StorageBudgetState >= WorldStorageBudgetState.OverTarget
                    ? new Color(114, 32, 32, 225)
                    : new Color(120, 96, 18, 225);
                var border = entry.StorageBudgetState >= WorldStorageBudgetState.OverTarget
                    ? new Color(255, 190, 190)
                    : new Color(255, 232, 164);
                sb.Draw(_pixel, badge, badgeBg);
                DrawBorder(sb, badge, border);
                _font.DrawString(sb, label, new Vector2(badge.X + 8, badge.Y + 4), Color.White);
            }

            // Always show seed info section
            var seedRect = new Rectangle(playRect.X - 12 - seedSectionWidth, rowRect.Y + 4, seedSectionWidth - 6, rowRect.Height - 8);
            sb.Draw(_pixel, seedRect, new Color(12, 12, 12, 170));
            DrawBorder(sb, seedRect, new Color(180, 180, 180));

            var seedLabel = "SEED";
            _font.DrawString(sb, seedLabel, new Vector2(seedRect.X + 8, seedRect.Y + 6), new Color(220, 220, 220));

            var eyeRect = new Rectangle(seedRect.Right - 28, seedRect.Y + 6, 20, 20);
            var revealed = IsSeedRevealed(entry);
            DrawEyeIcon(sb, eyeRect, revealed);
            _seedEyeRects[i] = eyeRect;

            var seedValue = revealed ? ShortSeed(entry.Seed) : "HIDDEN";
            _font.DrawString(sb, seedValue, new Vector2(seedRect.X + 8, seedRect.Bottom - _font.LineHeight - 6), Color.White);

            var playHovered = playRect.Contains(_overlayMousePos);
            var deleteHovered = deleteRect.Contains(_overlayMousePos);
            DrawRowPlayButton(sb, playRect, playHovered, entry.ValidationStatus == WorldValidationStatus.IncompleteGeneration);
            DrawRowDeleteButton(sb, deleteRect, deleteHovered);

            if (rowRect.Contains(_overlayMousePos) && revealed)
                _hoverWorldIndex = i;
        }
    }

    private void DrawWorldHoverTooltip(SpriteBatch sb)
    {
        if (_hoverWorldIndex < 0 || _hoverWorldIndex >= _worlds.Count)
            return;

        var entry = _worlds[_hoverWorldIndex];
        if (!IsSeedRevealed(entry))
            return;
        var tooltip = $"Seed: {entry.Seed} | Initial: {entry.InitialMode.ToString().ToUpperInvariant()}";
        var size = _font.MeasureString(tooltip);
        var rect = new Rectangle(
            Math.Clamp(_overlayMousePos.X + 14, _viewport.X + 8, _viewport.Right - (int)Math.Ceiling(size.X) - 24),
            Math.Clamp(_overlayMousePos.Y - _font.LineHeight - 16, _viewport.Y + 8, _viewport.Bottom - _font.LineHeight - 20),
            (int)Math.Ceiling(size.X) + 14,
            _font.LineHeight + 10);
        sb.Draw(_pixel, rect, new Color(0, 0, 0, 220));
        DrawBorder(sb, rect, new Color(200, 200, 200));
        _font.DrawString(sb, tooltip, new Vector2(rect.X + 7, rect.Y + 5), new Color(230, 230, 230));
    }

    private void DrawWorldPreviewTile(SpriteBatch sb, WorldListEntry entry, Rectangle rect)
    {
        var preview = GetPreviewTexture(entry.PreviewPath);
        if (preview != null)
        {
            sb.Draw(preview, rect, Color.White);
        }
        else
        {
            sb.Draw(_pixel, rect, new Color(42, 42, 42, 220));
            var label = "MAP";
            var labelSize = _font.MeasureString(label);
            var labelPos = new Vector2(
                rect.Center.X - labelSize.X / 2f,
                rect.Center.Y - labelSize.Y / 2f);
            _font.DrawString(sb, label, labelPos, new Color(200, 200, 200));
        }

        DrawBorder(sb, rect, new Color(220, 220, 220));
    }

    private void DrawEyeIcon(SpriteBatch sb, Rectangle rect, bool revealed)
    {
        var bg = revealed ? new Color(64, 112, 164, 210) : new Color(50, 50, 50, 210);
        sb.Draw(_pixel, rect, bg);
        DrawBorder(sb, rect, new Color(220, 220, 220));

        var iris = new Rectangle(rect.X + rect.Width / 2 - 3, rect.Y + rect.Height / 2 - 3, 6, 6);
        sb.Draw(_pixel, iris, revealed ? new Color(235, 245, 255) : new Color(150, 150, 150));
    }

    private bool IsSeedRevealed(WorldListEntry entry)
    {
        if (!_seedRevealByWorld.TryGetValue(entry.WorldPath, out var revealed))
            return false;
        return revealed;
    }

    private Texture2D? GetPreviewTexture(string previewPath)
    {
        if (string.IsNullOrWhiteSpace(previewPath))
            return null;

        if (!File.Exists(previewPath))
            return null;

        var stamp = File.GetLastWriteTimeUtc(previewPath);
        if (_previewTextures.TryGetValue(previewPath, out var cached))
        {
            if (cached.LastWriteUtc == stamp && !cached.Texture.IsDisposed)
                return cached.Texture;

            cached.Texture.Dispose();
            _previewTextures.Remove(previewPath);
        }

        try
        {
            using var fs = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var texture = Texture2D.FromStream(_assets.GraphicsDevice, fs);
            _previewTextures[previewPath] = new PreviewTextureCacheEntry(texture, stamp);
            _previewLoadFailures.Remove(previewPath);
            return texture;
        }
        catch (Exception ex)
        {
            if (_previewLoadFailures.Add(previewPath))
                _log.Warn($"Failed to load world preview '{previewPath}': {ex.Message}");
            return null;
        }
    }

    private void DisposePreviewTextures()
    {
        foreach (var cached in _previewTextures.Values)
            cached.Texture.Dispose();

        _previewTextures.Clear();
        _previewLoadFailures.Clear();
    }

    private void HandleListInput(InputState input)
    {
        if (_listRect.Contains(input.MousePosition) && input.ScrollDelta != 0)
        {
            var step = Math.Sign(input.ScrollDelta) * ScrollStep;
            _scroll -= step;
            ClampScroll();
        }

        if (input.IsNewLeftClick() && _listRect.Contains(input.MousePosition))
        {
            foreach (var pair in _seedEyeRects)
            {
                if (!pair.Value.Contains(input.MousePosition))
                    continue;
                var worldIndex = pair.Key;
                if (worldIndex >= 0 && worldIndex < _worlds.Count)
                {
                    var entry = _worlds[worldIndex];
                    _seedRevealByWorld[entry.WorldPath] = !IsSeedRevealed(entry);
                }
                return;
            }

            foreach (var pair in _rowPlayRects)
            {
                if (!pair.Value.Contains(input.MousePosition))
                    continue;

                _selectedIndex = pair.Key;
                if (pair.Key >= 0 && pair.Key < _worlds.Count)
                {
                    if (_worlds[pair.Key].ValidationStatus == WorldValidationStatus.IncompleteGeneration)
                        ResumeWorldGeneration(pair.Key);
                    else
                        JoinWorld(pair.Key);
                }
                return;
            }

            foreach (var pair in _rowDeleteRects)
            {
                if (!pair.Value.Contains(input.MousePosition))
                    continue;

                _selectedIndex = pair.Key;
                OpenDeleteConfirm(pair.Key);
                return;
            }

            var idx = (int)((input.MousePosition.Y - _listRect.Y + _scroll) / _rowHeight);
            if (idx >= 0 && idx < _worlds.Count)
            {
                if (idx == _lastClickIndex && (_now - _lastClickTime) <= DoubleClickSeconds)
                {
                    _selectedIndex = idx;
                    _lastClickIndex = -1;
                    _lastClickTime = 0;
                    JoinSelectedWorld();
                    return;
                }

                _selectedIndex = idx;
                _lastClickIndex = idx;
                _lastClickTime = _now;
            }
        }
    }

    private void RefreshWorlds()
    {
        _hoverWorldIndex = -1;
        DisposePreviewTextures();
        try
        {
            _worlds = WorldListService.LoadSingleplayerWorlds(_log);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to read worlds: {ex.Message}");
            _worlds = new List<WorldListEntry>();
        }

        var validWorldPaths = new HashSet<string>(_worlds.Select(w => w.WorldPath), StringComparer.OrdinalIgnoreCase);
        var stale = _seedRevealByWorld.Keys.Where(k => !validWorldPaths.Contains(k)).ToArray();
        for (var i = 0; i < stale.Length; i++)
            _seedRevealByWorld.Remove(stale[i]);
        for (var i = 0; i < _worlds.Count; i++)
        {
            if (!_seedRevealByWorld.ContainsKey(_worlds[i].WorldPath))
                _seedRevealByWorld[_worlds[i].WorldPath] = false;
        }

        if (_worlds.Count == 0)
        {
            _selectedIndex = -1;
            _scroll = 0;
        }
        else
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _worlds.Count - 1);
        }

        ClampScroll();
    }

    private void ClampScroll()
    {
        var contentHeight = _worlds.Count * _rowHeight;
        var max = Math.Max(0, contentHeight - _listRect.Height);
        if (_scroll < 0)
            _scroll = 0;
        else if (_scroll > max)
            _scroll = max;
    }

    private void OpenCreateWorld()
    {
        _log.Info("Opening Create World screen with new world generation system");
        _menus.Push(new CreateWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, OnWorldCreated), _viewport);
    }

    private void ResumeSelectedWorldGeneration()
    {
        if (!IsSelectedIncompleteWorld())
        {
            ShowStatus("SELECT AN INCOMPLETE WORLD");
            return;
        }

        ResumeWorldGeneration(_selectedIndex);
    }

    private void OnWorldCreated(string worldName)
    {
        RefreshWorlds();
        if (!string.IsNullOrWhiteSpace(worldName))
        {
            var idx = _worlds.FindIndex(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _selectedIndex = idx;
        }
    }

    private void OpenDeleteConfirm(int index)
    {
        if (index < 0 || index >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD TO DELETE");
            return;
        }

        var entry = _worlds[index];
        var name = entry.Name;
        _deleteConfirmOpen = true;
        _deleteConfirmIndex = index;
        _log.Info($"Delete world requested: {name}");
    }

    private void ConfirmDeleteSelectedWorld()
    {
        if (!_deleteConfirmOpen)
            return;

        var index = _deleteConfirmIndex;
        CancelDeleteSelectedWorld();
        if (index < 0 || index >= _worlds.Count)
            return;

        var entry = _worlds[index];
        var name = entry.Name;
        var path = entry.WorldPath;

        try
        {
            if (Directory.Exists(path))
            {
                // Clear read-only attributes recursively before deletion
                ClearReadOnlyAttributes(path);
                
                // Try to delete both possible region folders (case sensitivity issues)
                var regionsPath = Path.Combine(path, "regions");
                var regionsPathUpper = Path.Combine(path, "Regions");
                
                if (Directory.Exists(regionsPath))
                {
                    ClearReadOnlyAttributes(regionsPath);
                    Directory.Delete(regionsPath, true);
                }
                
                if (Directory.Exists(regionsPathUpper) && regionsPathUpper != regionsPath)
                {
                    ClearReadOnlyAttributes(regionsPathUpper);
                    Directory.Delete(regionsPathUpper, true);
                }
                
                // Finally delete the world folder
                Directory.Delete(path, true);
            }

            _log.Info($"Deleted world: {name} at {path}");
            RefreshWorlds();
            ShowStatus("WORLD DELETED");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete world {name} at {path}: {ex.Message}");
            ShowStatus("FAILED TO DELETE WORLD");
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists)
            {
                dirInfo.Attributes &= ~FileAttributes.ReadOnly;
                
                foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }
                
                foreach (var dir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
                {
                    dir.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch (Exception ex)
        {
            // Non-critical - just log and continue
            Console.WriteLine($"Warning: Failed to clear read-only attributes for {path}: {ex.Message}");
        }
    }

    private void CancelDeleteSelectedWorld()
    {
        _deleteConfirmOpen = false;
        _deleteConfirmIndex = -1;
    }

    private void ShowStatus(string message)
    {
        _statusMessage = message;
        _statusUntil = _now + 2.5;
    }

    private void JoinSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD TO JOIN");
            return;
        }

        JoinWorld(_selectedIndex);
    }

    private void ResumeWorldGeneration(int index)
    {
        if (index < 0 || index >= _worlds.Count)
            return;

        var entry = _worlds[index];
        _menus.Push(new CreateWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, OnWorldCreated, entry.WorldPath), _viewport);
    }

    private void JoinWorld(int index)
    {
        if (index < 0 || index >= _worlds.Count)
            return;

        var entry = _worlds[index];
        var name = entry.Name;
        var worldPath = entry.WorldPath;
        
        // Validate world format before attempting to load
        var validation = WorldValidator.ValidateWorldFolder(worldPath);
        
        switch (validation.Status)
        {
            case WorldValidationStatus.ValidNew:
                // Proceed with loading - use existing legacy meta path for now
                var metaPath = Paths.ResolveWorldMetaPath(worldPath);
                if (!Directory.Exists(worldPath) || !File.Exists(metaPath))
                {
                    _log.Warn($"World data missing for '{name}'.");
                    ShowStatus("WORLD DATA MISSING");
                    return;
                }
                _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldPath, metaPath), _viewport);
                break;

            case WorldValidationStatus.IncompleteGeneration:
                ResumeWorldGeneration(index);
                break;
                
            case WorldValidationStatus.LegacyDetected:
                ShowLegacyWorldPopup(name, validation.Reason);
                break;
                
            case WorldValidationStatus.CorruptedNew:
                ShowCorruptedWorldPopup(name, validation.Reason);
                break;
                
            case WorldValidationStatus.NotAWorld:
                ShowStatus("NOT A VALID WORLD");
                break;
        }
    }
    
    private void ShowLegacyWorldPopup(string worldName, string reason)
    {
        // For now, just show status - popup implementation would need UI framework
        ShowStatus($"LEGACY WORLD: {worldName}");
        _log.Warn($"Legacy world detected: {worldName} - {reason}");
    }
    
    private void ShowCorruptedWorldPopup(string worldName, string reason)
    {
        // For now, just show status - popup implementation would need UI framework
        ShowStatus($"CORRUPTED WORLD: {worldName}");
        _log.Error($"Corrupted world detected: {worldName} - {reason}");
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private bool IsSelectedIncompleteWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
            return false;
        return _worlds[_selectedIndex].ValidationStatus == WorldValidationStatus.IncompleteGeneration;
    }

    private void SyncActionButtons()
    {
        var showResume = IsSelectedIncompleteWorld();
        _resumeBtn.Visible = showResume;
        _resumeBtn.Enabled = showResume;
    }

    private void DrawRowPlayButton(SpriteBatch sb, Rectangle rect, bool hovered, bool resumeAction)
    {
        var bg = hovered ? new Color(68, 120, 186, 230) : new Color(38, 78, 128, 215);
        if (resumeAction)
            bg = hovered ? new Color(150, 112, 44, 230) : new Color(116, 82, 28, 215);

        sb.Draw(_pixel, rect, bg);
        DrawBorder(sb, rect, new Color(230, 240, 255));

        var triangleWidth = Math.Max(8, rect.Width / 3);
        var triangleHeight = Math.Max(10, rect.Height / 2);
        var left = rect.Center.X - triangleWidth / 2;
        for (var x = 0; x < triangleWidth; x++)
        {
            var currentHeight = Math.Max(1, (int)Math.Round((triangleWidth - x) / (float)triangleWidth * triangleHeight));
            var bar = new Rectangle(left + x, rect.Center.Y - currentHeight / 2, 1, currentHeight);
            sb.Draw(_pixel, bar, Color.White);
        }
    }

    private void DrawRowDeleteButton(SpriteBatch sb, Rectangle rect, bool hovered)
    {
        var bg = hovered ? new Color(150, 50, 50, 230) : new Color(100, 34, 34, 215);
        sb.Draw(_pixel, rect, bg);
        DrawBorder(sb, rect, new Color(255, 220, 220));
        if (_deleteIconTexture is not null)
        {
            var inset = Math.Max(2, rect.Width / 8);
            var iconRect = new Rectangle(
                rect.X + inset,
                rect.Y + inset,
                Math.Max(1, rect.Width - inset * 2),
                Math.Max(1, rect.Height - inset * 2));
            sb.Draw(_deleteIconTexture, iconRect, hovered ? Color.White : new Color(240, 240, 240));
        }
    }

    private void LayoutDeleteConfirmOverlay()
    {
        var modalWidth = Math.Clamp(_panelRect.Width - 140, 320, 560);
        var modalHeight = 172;
        _deleteConfirmRect = new Rectangle(
            _panelRect.Center.X - modalWidth / 2,
            _panelRect.Center.Y - modalHeight / 2,
            modalWidth,
            modalHeight);

        var buttonsY = _deleteConfirmRect.Bottom - 22 - 44;
        var gap = 16;
        var buttonWidth = Math.Clamp((_deleteConfirmRect.Width - 48 - gap) / 2, 120, 220);
        var totalWidth = buttonWidth * 2 + gap;
        var startX = _deleteConfirmRect.Center.X - totalWidth / 2;
        _confirmDeleteBtn.Bounds = new Rectangle(startX, buttonsY, buttonWidth, 44);
        _cancelDeleteBtn.Bounds = new Rectangle(startX + buttonWidth + gap, buttonsY, buttonWidth, 44);
    }

    private void DrawDeleteConfirmOverlay(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewport, new Color(0, 0, 0, 180));
        sb.Draw(_pixel, _deleteConfirmRect, new Color(18, 18, 18, 235));
        DrawBorder(sb, _deleteConfirmRect, new Color(230, 230, 230));

        var title = "DELETE WORLD?";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(
            sb,
            title,
            new Vector2(_deleteConfirmRect.Center.X - titleSize.X / 2f, _deleteConfirmRect.Y + 16),
            Color.White);

        var worldName = (_deleteConfirmIndex >= 0 && _deleteConfirmIndex < _worlds.Count)
            ? _worlds[_deleteConfirmIndex].Name
            : "UNKNOWN WORLD";
        var body = TruncateToWidth(worldName, _deleteConfirmRect.Width - 36, _font);
        var bodySize = _font.MeasureString(body);
        _font.DrawString(
            sb,
            body,
            new Vector2(_deleteConfirmRect.Center.X - bodySize.X / 2f, _deleteConfirmRect.Y + 16 + _font.LineHeight + 10),
            new Color(230, 230, 230));

        _confirmDeleteBtn.Draw(sb, _pixel, _font);
        _cancelDeleteBtn.Draw(sb, _pixel, _font);
    }

    private static string ShortSeed(int seed)
    {
        var value = seed.ToString();
        if (value.Length <= 8)
            return value;

        return $"{value.Substring(0, 4)}...{value.Substring(value.Length - 2)}";
    }

    private static string TruncateToWidth(string value, int maxWidth, PixelFont font)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (font.MeasureString(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        var maxChars = Math.Max(1, value.Length - 1);
        while (maxChars > 1)
        {
            var candidate = value.Substring(0, maxChars).TrimEnd() + ellipsis;
            if (font.MeasureString(candidate).X <= maxWidth)
                return candidate;
            maxChars--;
        }

        return ellipsis;
    }

    public void OnClose()
    {
        DisposePreviewTextures();
    }

    private sealed class PreviewTextureCacheEntry
    {
        public PreviewTextureCacheEntry(Texture2D texture, DateTime lastWriteUtc)
        {
            Texture = texture;
            LastWriteUtc = lastWriteUtc;
        }

        public Texture2D Texture { get; }
        public DateTime LastWriteUtc { get; }
    }
}
