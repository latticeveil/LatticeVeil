using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MoreWorldOptionsScreen : IScreen
{
    private const int PanelMaxWidth = 1300;
    private const int PanelMaxHeight = 700;
    
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly Action<bool, bool, bool, bool, bool, int> _onSettingsChanged; // (generateOres, generateCaves, generateStructures, flat, enableHomes, homeSlots)

    // UI Elements
    private readonly Button _backBtn;
    private readonly Button _generateOresBtn;
    private readonly Button _generateCavesBtn;
    private readonly Button _generateStructuresBtn;
    private readonly Button _flatBtn;
    private readonly Button _enableHomesBtn;
    private Rectangle _homeSlotsInputRect;

    // Settings
    private bool _generateOres;
    private bool _generateCaves;
    private bool _generateStructures;
    private bool _flat;
    private bool _enableHomes;
    private int _maxHomesPerPlayer = 2;
    private bool _unlimitedHomes = false;
    private int _maxHomesCap = 10;
    private bool _homeSlotsInputActive = false;
    private string _homeSlotsInputText = "2";
    private double _now;

    // Layout
    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _contentArea;
    private Texture2D? _bgTexture;
    private Texture2D? _guiTexture;

    public MoreWorldOptionsScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        bool generateOres,
        bool generateCaves,
        bool generateStructures,
        bool flat,
        bool enableHomes,
        int maxHomesPerPlayer,
        int maxHomesCap,
        Action<bool, bool, bool, bool, bool, int> onSettingsChanged)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _generateOres = generateOres;
        _generateCaves = generateCaves;
        _generateStructures = generateStructures;
        _flat = flat;
        _enableHomes = enableHomes;
        _maxHomesPerPlayer = Math.Clamp(maxHomesPerPlayer, 1, maxHomesCap);
        _maxHomesCap = maxHomesCap;
        _homeSlotsInputText = _maxHomesPerPlayer.ToString();
        _onSettingsChanged = onSettingsChanged;

        // Create UI elements
        _backBtn = new Button("← BACK", () => _menus.Pop());
        _generateOresBtn = new Button(string.Empty, ToggleGenerateOres);
        _generateCavesBtn = new Button(string.Empty, ToggleGenerateCaves);
        _generateStructuresBtn = new Button(string.Empty, ToggleGenerateStructures);
        _flatBtn = new Button(string.Empty, ToggleFlat);
        _enableHomesBtn = new Button(string.Empty, ToggleEnableHomes);
        _homeSlotsInputRect = Rectangle.Empty;

        SyncButtonLabels();
        LoadAssets();
    }

    private void LoadAssets()
    {
        _bgTexture = TryLoadTexture("textures/menu/backgrounds/CreateWorld_bg.png");
        _guiTexture = TryLoadTexture("textures/menu/GUIS/CreateWorld_GUI.png");
        _backBtn.Texture = TryLoadTexture("textures/menu/buttons/Back.png");
    }

    private Texture2D? TryLoadTexture(string assetPath)
    {
        try
        {
            return _assets.LoadTexture(assetPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Missing texture '{assetPath}': {ex.Message}");
            return null;
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(PanelMaxWidth, viewport.Width - 20);
        var panelH = Math.Min(PanelMaxHeight, viewport.Height - 30);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var innerPadX = Math.Clamp(_panelRect.Width / 16, 38, 72);
        var innerPadTop = Math.Clamp(_panelRect.Height / 8, 44, 78);
        var innerPadBottom = Math.Clamp(_panelRect.Height / 10, 34, 60);
        // Shrink content area by 2 inches (60 pixels) for better fit
        var shrinkAmount = 60;
        _contentArea = new Rectangle(
            _panelRect.X + innerPadX + shrinkAmount / 2,
            _panelRect.Y + innerPadTop + shrinkAmount / 2,
            _panelRect.Width - innerPadX * 2 - shrinkAmount,
            _panelRect.Height - innerPadTop - innerPadBottom - shrinkAmount
        );

        // Layout buttons
        var buttonY = _contentArea.Y + 20 + 60;  // Move all buttons down 2 inches
        var buttonW = Math.Max(200, _contentArea.Width - 40);
        var buttonH = 40;
        var buttonX = _contentArea.X + (_contentArea.Width - buttonW) / 2;

        _generateOresBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 60;

        _generateCavesBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 60;

        _generateStructuresBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 60;

        _flatBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 60;

        _enableHomesBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 60;

        _homeSlotsInputRect = new Rectangle(buttonX + 150, buttonY, 120, 30);  // Move further right to avoid overlap
        buttonY += 80;

        // Back button - match CreateWorldScreen and SingleplayerScreen positioning (bottom-left of full screen)
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
            backBtnH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            _homeSlotsInputActive = _homeSlotsInputRect.Contains(input.MousePosition) && _enableHomes;
        }

        if (_homeSlotsInputActive)
        {
            HandleHomeSlotsInput(input);
            if (input.IsNewKeyPress(Keys.Enter))
            {
                _homeSlotsInputActive = false;
                return;
            }
        }

        _generateOresBtn.Update(input);
        _generateCavesBtn.Update(input);
        _generateStructuresBtn.Update(input);
        _flatBtn.Update(input);
        _enableHomesBtn.Update(input);
        _backBtn.Update(input);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);

        // Draw background
        if (_bgTexture != null)
            sb.Draw(_bgTexture, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 180));

        sb.End();
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        // Draw GUI panel
        if (_guiTexture != null)
            sb.Draw(_guiTexture, _panelRect, Color.White);
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(25, 25, 25, 240));
            DrawBorder(sb, _panelRect, new Color(100, 100, 100));
        }

        // Draw title
        const int borderSize = 16;
        var title = "MORE WORLD OPTIONS";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16 + borderSize + 80);  // Match other screens positioning
        _font.DrawString(sb, title, titlePos, Color.White);

        // Draw buttons
        _generateOresBtn.Draw(sb, _pixel, _font);
        _generateCavesBtn.Draw(sb, _pixel, _font);
        _generateStructuresBtn.Draw(sb, _pixel, _font);
        _flatBtn.Draw(sb, _pixel, _font);
        _enableHomesBtn.Draw(sb, _pixel, _font);
        
        // Draw home slots input
        DrawHomeSlotsInput(sb);

        // Draw back button
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawHomeSlotsInput(SpriteBatch sb)
    {
        // Draw label
        var labelX = _homeSlotsInputRect.X - _font.MeasureString("HOME SLOTS:").X - 10;  // Position text to the left of input box
        _font.DrawString(sb, "HOME SLOTS:", new Vector2(labelX, _homeSlotsInputRect.Y + 6), Color.White);
        
        // Draw input field
        sb.Draw(_pixel, _homeSlotsInputRect, _homeSlotsInputActive ? new Color(35, 35, 35, 235) : new Color(20, 20, 20, 220));
        DrawBorder(sb, _homeSlotsInputRect, _enableHomes ? Color.White : new Color(120, 120, 120));
        
        // Draw text
        var displayText = string.IsNullOrWhiteSpace(_homeSlotsInputText) ? "2" : _homeSlotsInputText;
        var textColor = _enableHomes ? Color.White : new Color(120, 120, 120);
        var textPos = new Vector2(_homeSlotsInputRect.X + 8, _homeSlotsInputRect.Y + (_homeSlotsInputRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, displayText, textPos, textColor);
        
        // Draw cursor when active
        if (_homeSlotsInputActive && ((_now * 2.0) % 2.0) < 1.0 && _enableHomes)
        {
            var cursorText = displayText;
            var cursorX = textPos.X + _font.MeasureString(cursorText).X + 2f;
            var cursorRect = new Rectangle((int)cursorX, _homeSlotsInputRect.Y + 6, 2, _homeSlotsInputRect.Height - 12);
            sb.Draw(_pixel, cursorRect, Color.White);
        }
    }

    private void ToggleGenerateOres()
    {
        _generateOres = !_generateOres;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void ToggleGenerateCaves()
    {
        _generateCaves = !_generateCaves;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void ToggleGenerateStructures()
    {
        _generateStructures = !_generateStructures;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void ToggleFlat()
    {
        _flat = !_flat;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void ToggleEnableHomes()
    {
        _enableHomes = !_enableHomes;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void HandleHomeSlotsInput(InputState input)
    {
        if (_homeSlotsInputActive && _enableHomes)
        {
            // Only accept numbers, no text input
            foreach (var key in input.GetTextInputKeys())
            {
                if (key == Keys.Back)
                {
                    if (_homeSlotsInputText.Length > 0)
                        _homeSlotsInputText = _homeSlotsInputText.Substring(0, _homeSlotsInputText.Length - 1);
                    continue;
                }

                if (key >= Keys.D0 && key <= Keys.D9)
                {
                    AppendToHomeSlotsInput((char)('0' + (key - Keys.D0)), 10);
                    continue;
                }

                if (key == Keys.Enter)
                {
                    _homeSlotsInputActive = false;
                    UpdateHomeSlotsFromInput();
                    NotifySettingsChanged();
                    return;
                }
            }
        }
    }

    private void AppendToHomeSlotsInput(char c, int maxLen)
    {
        if (_homeSlotsInputText.Length >= maxLen)
            return;
        _homeSlotsInputText += c;
    }

    private void UpdateHomeSlotsFromInput()
    {
        if (string.IsNullOrWhiteSpace(_homeSlotsInputText) || _homeSlotsInputText.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            _unlimitedHomes = true;
            _maxHomesPerPlayer = _maxHomesCap;
        }
        else if (int.TryParse(_homeSlotsInputText, out var value))
        {
            _unlimitedHomes = false;
            _maxHomesPerPlayer = Math.Clamp(value, 1, _maxHomesCap);
        }
    }

    private void SyncButtonLabels()
    {
        _generateOresBtn.Label = _generateOres ? "ORES: Enabled" : "ORES: Disabled";
        _generateCavesBtn.Label = _generateCaves ? "CAVES: Enabled" : "CAVES: Disabled";
        _generateStructuresBtn.Label = _generateStructures ? "STRUCTURES: Enabled" : "STRUCTURES: Disabled";
        _flatBtn.Label = _flat ? "FLAT: Enabled" : "FLAT: Disabled";
        _enableHomesBtn.Label = _enableHomes ? "HOMES: Enabled" : "HOMES: Disabled";
    }

    private void NotifySettingsChanged()
    {
        _onSettingsChanged?.Invoke(_generateOres, _generateCaves, _generateStructures, _flat, _enableHomes, _unlimitedHomes ? -1 : _maxHomesPerPlayer);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}
