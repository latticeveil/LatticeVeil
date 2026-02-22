using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class ScreenshotsScreen : IScreen
{
    private const int ScrollStep = 40;
    private const int TileSpacing = 16;
    private const double DoubleClickSeconds = 0.35;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;

    private Texture2D? _bg;
    private Texture2D? _panel;
    private readonly Button _openFolderBtn;
    private readonly Button _deleteBtn;
    private readonly Button _backBtn;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _contentRect;
    private float _scroll;
    private int _contentHeight;
    private int _thumbW;
    private int _thumbH;
    private int _tileHeight;
    private int _columns;
    private int _labelHeight;
    private int _selectedIndex = -1;
    private int _lastClickIndex = -1;
    private double _lastClickTime;

    private readonly List<ScreenshotItem> _items = new();

    private static readonly RasterizerState ScissorState = new() { ScissorTestEnable = true };

    private sealed class ScreenshotItem
    {
        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public Texture2D Texture { get; init; } = null!;
        public int Width { get; init; }
        public int Height { get; init; }
    }

    public ScreenshotsScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;

        _openFolderBtn = new Button("OPEN FOLDER", OpenScreenshotsFolder);
        _deleteBtn = new Button("DELETE", DeleteSelectedScreenshot);
        _backBtn = new Button("BACK", () => _menus.Pop());

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/singleplayer_bg.png"); // Use existing background
            _panel = _assets.LoadTexture("textures/menu/GUIS/Singleplayer_GUI.png"); // Use existing GUI
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
            _deleteBtn.Texture = _assets.LoadTexture("textures/menu/buttons/DeleteDefault.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Screenshots screen asset load: {ex.Message}");
        }

        LoadScreenshots();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        // Use standard panel dimensions (1300x700) like other screens
        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 12;
        var buttonRowH = _font.LineHeight + 10;
        var buttonRowY = panelY + panelH - pad - buttonRowH;
        
        // Position back button with standard scale (240f) to match other screens
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH)); // Match SettingsScreen
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH_calc = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(
            viewport.X + backBtnMargin, 
            viewport.Bottom - backBtnMargin - backBtnH_calc, 
            backBtnW, 
            backBtnH_calc
        );
        
        // Position other buttons in the panel
        var availableWidth = panelW - pad * 2;
        var folderButtonW = availableWidth / 2;
        _openFolderBtn.Bounds = new Rectangle(panelX + pad, buttonRowY, folderButtonW, buttonRowH);
        
        // Make Delete button bigger and square
        var deleteButtonSize = buttonRowH * 2; // Make it twice as big
        _deleteBtn.Bounds = new Rectangle(panelX + pad + folderButtonW + 10, buttonRowY - (deleteButtonSize - buttonRowH) / 2, deleteButtonSize, deleteButtonSize);

        var titleHeight = _font.LineHeight * 2 + 10;
        _contentRect = new Rectangle(
            panelX + pad + 75, // Move 2.5 inches (75px) to the right (60 + 15)
            panelY + pad + titleHeight + 60, // Move 2 inches (60px) lower
            panelW - pad * 2 - 75 - 75, // Reduce width by additional 2.5 inches (75px) from right total
            buttonRowY - (panelY + pad + titleHeight + 60) - 10 - 180); // Adjust height for lower position

        RebuildLayout();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        _openFolderBtn.Update(input);
        _deleteBtn.Update(input);
        _backBtn.Update(input);
        HandleItemClick(gameTime, input);
        HandleScroll(input);
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

        // Draw GUI panel
        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);

        var title = "SCREENSHOTS";
        var count = _items.Count == 1 ? "1 FILE" : $"{_items.Count} FILES";
        var titlePos = new Vector2(_panelRect.X + 20, _panelRect.Y + 20);
        _font.DrawString(sb, title, titlePos, Color.White);
        _font.DrawString(sb, count, new Vector2(titlePos.X, titlePos.Y + _font.LineHeight + 4), Color.White);

        _openFolderBtn.Draw(sb, _pixel, _font);
        _deleteBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (_items.Count == 0)
        {
            var msg = "NO SCREENSHOTS FOUND";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_contentRect.Center.X - size.X / 2f, _contentRect.Center.Y - size.Y / 2f);
            _font.DrawString(sb, msg, pos, Color.White);
            sb.End();
            return;
        }

        sb.End();

        var device = sb.GraphicsDevice;
        var priorScissor = device.ScissorRectangle;
        device.ScissorRectangle = UiLayout.ToScreenRect(_contentRect);

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform, rasterizerState: ScissorState);

        for (int i = 0; i < _items.Count; i++)
        {
            var tileRect = GetTileRect(i, _scroll);
            if (tileRect.Bottom < _contentRect.Y || tileRect.Y > _contentRect.Bottom)
                continue;

            var thumbRect = new Rectangle(tileRect.X, tileRect.Y, _thumbW, _thumbH);
            DrawThumbnail(sb, _items[i], thumbRect);

            var labelRect = new Rectangle(tileRect.X, tileRect.Y + _thumbH + 2, _thumbW, _labelHeight);
            var label = TrimToWidth(_items[i].Name, _thumbW);
            var labelSize = _font.MeasureString(label);
            var labelPos = new Vector2(labelRect.X + (labelRect.Width - labelSize.X) / 2f, labelRect.Y + 2);
            _font.DrawString(sb, label, labelPos, Color.White);

            if (i == _selectedIndex)
                DrawSelectionBorder(sb, tileRect);
        }

        sb.End();
        device.ScissorRectangle = priorScissor;
    }

    private void LoadScreenshots()
    {
        _items.Clear();

        try
        {
            Directory.CreateDirectory(Paths.ScreenshotsDir);
            var files = Directory.GetFiles(Paths.ScreenshotsDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImage)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            foreach (var path in files)
            {
                var item = TryLoadScreenshot(path);
                if (item != null)
                    _items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load screenshots: {ex.Message}");
        }
    }

    private ScreenshotItem? TryLoadScreenshot(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var tex = Texture2D.FromStream(_assets.GraphicsDevice, fs);
            var name = Path.GetFileNameWithoutExtension(path);
            return new ScreenshotItem
            {
                Path = path,
                Name = string.IsNullOrWhiteSpace(name) ? "SCREENSHOT" : name,
                Texture = tex,
                Width = tex.Width,
                Height = tex.Height
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load screenshot {path}: {ex.Message}");
            return null;
        }
    }

    private static bool IsSupportedImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
    }

    private void DrawThumbnail(SpriteBatch sb, ScreenshotItem item, Rectangle thumbRect)
    {
        sb.Draw(_pixel, thumbRect, new Color(20, 20, 20));
        DrawBorder(sb, thumbRect, Color.White);

        var dest = FitRect(thumbRect, item.Width, item.Height);
        sb.Draw(item.Texture, dest, Color.White);
    }

    private static Rectangle FitRect(Rectangle bounds, int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0)
            return bounds;

        var scale = Math.Min(bounds.Width / (float)srcW, bounds.Height / (float)srcH);
        var w = (int)Math.Round(srcW * scale);
        var h = (int)Math.Round(srcH * scale);
        var x = bounds.X + (bounds.Width - w) / 2;
        var y = bounds.Y + (bounds.Height - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private void DrawSelectionBorder(SpriteBatch sb, Rectangle rect)
    {
        var color = new Color(255, 210, 90);
        DrawBorder(sb, rect, color);

        var inner = new Rectangle(rect.X + 3, rect.Y + 3, rect.Width - 6, rect.Height - 6);
        if (inner.Width > 4 && inner.Height > 4)
            DrawBorder(sb, inner, color);
    }

    private void HandleScroll(InputState input)
    {
        var delta = input.ScrollDelta;
        if (delta == 0)
            return;

        if (!_contentRect.Contains(input.MousePosition))
            return;

        var step = Math.Sign(delta) * ScrollStep;
        _scroll -= step;
        ClampScroll();
    }

    private void ClampScroll()
    {
        var max = Math.Max(0, _contentHeight - _contentRect.Height);
        if (_scroll < 0)
            _scroll = 0;
        else if (_scroll > max)
            _scroll = max;
    }

    private void RebuildLayout()
    {
        _labelHeight = _font.LineHeight + 4;
        var availableW = Math.Max(1, _contentRect.Width);

        var targetW = Math.Min(260, availableW);
        _columns = Math.Max(1, (availableW + TileSpacing) / (targetW + TileSpacing));

        var totalSpacing = (_columns - 1) * TileSpacing;
        _thumbW = Math.Max(1, (availableW - totalSpacing) / _columns);
        _thumbH = Math.Max(1, (int)Math.Round(_thumbW * 9f / 16f));
        _tileHeight = _thumbH + _labelHeight;

        var rows = _items.Count == 0 ? 0 : (_items.Count + _columns - 1) / _columns;
        _contentHeight = rows > 0 ? rows * (_tileHeight + TileSpacing) - TileSpacing : 0;

        ClampScroll();
    }

    private Rectangle GetTileRect(int index, float scroll)
    {
        var row = index / _columns;
        var col = index % _columns;
        var x = _contentRect.X + col * (_thumbW + TileSpacing);
        var y = _contentRect.Y + row * (_tileHeight + TileSpacing) - (int)Math.Round(scroll);
        return new Rectangle(x, y, _thumbW, _tileHeight);
    }

    private void HandleItemClick(GameTime gameTime, InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        if (_items.Count == 0)
            return;

        if (!_contentRect.Contains(input.MousePosition))
            return;

        var idx = HitTest(input.MousePosition);
        if (idx < 0 || idx >= _items.Count)
            return;

        _selectedIndex = idx;

        var now = gameTime.TotalGameTime.TotalSeconds;
        if (idx == _lastClickIndex && now - _lastClickTime <= DoubleClickSeconds)
        {
            OpenScreenshot(_items[idx]);
            _lastClickIndex = -1;
            _lastClickTime = 0;
            return;
        }

        _lastClickIndex = idx;
        _lastClickTime = now;
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var tile = GetTileRect(i, _scroll);
            if (tile.Contains(p))
                return i;
        }
        return -1;
    }

    private string TrimToWidth(string text, int maxWidth)
    {
        if (_font.MeasureString(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var trimmed = text;
        var maxChars = Math.Max(0, text.Length - 1);
        while (maxChars > 0)
        {
            var candidate = text.Substring(0, maxChars) + ellipsis;
            if (_font.MeasureString(candidate).X <= maxWidth)
                return candidate;
            maxChars--;
        }
        return ellipsis;
    }

    private void OpenScreenshot(ScreenshotItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            {
                _log.Warn("Screenshot file is missing.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true
            });
            _log.Info($"Opened screenshot: {item.Path}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to open screenshot: {ex.Message}");
        }
    }

    private void DeleteSelectedScreenshot()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _items.Count)
        {
            System.Windows.Forms.MessageBox.Show(
                "Select a screenshot to delete first.",
                "Screenshots",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        var item = _items[_selectedIndex];
        var result = System.Windows.Forms.MessageBox.Show(
            $"Delete this screenshot?\n\n{Path.GetFileName(item.Path)}",
            "Confirm Delete",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Warning);

        if (result != System.Windows.Forms.DialogResult.Yes)
            return;

        try
        {
            if (File.Exists(item.Path))
                File.Delete(item.Path);

            item.Texture.Dispose();
            _items.RemoveAt(_selectedIndex);

            if (_items.Count == 0)
                _selectedIndex = -1;
            else if (_selectedIndex >= _items.Count)
                _selectedIndex = _items.Count - 1;

            RebuildLayout();
            _log.Info($"Deleted screenshot: {item.Path}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete screenshot: {ex.Message}");
            System.Windows.Forms.MessageBox.Show(
                "Failed to delete screenshot.",
                "Screenshots",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
    }

    private void OpenScreenshotsFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.ScreenshotsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Paths.ScreenshotsDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to open screenshots folder: {ex.Message}");
        }
    }
}



