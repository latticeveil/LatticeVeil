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

    private enum Tab
    {
        WorldGen,
        Gameplay,
        Performance
    }

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;

    private readonly Action<bool, bool, bool, bool, string, bool, int> _onSettingsChanged;
    // (generateOres, generateCaves, generateStructures, generateTrees, worldType, enableHomes, homeSlots)

    // Tabs
    private readonly Button _tabWorldGenBtn;
    private readonly Button _tabGameplayBtn;
    private readonly Button _tabPerfBtn;
    private Tab _activeTab = Tab.WorldGen;

    // World Gen controls
    private readonly Button _worldTypeBtn;
    private readonly Button _generateTreesBtn;
    private readonly Button _generateOresBtn;
    private readonly Button _generateCavesBtn;
    private readonly Button _generateStructuresBtn;

    // Gameplay controls
    private readonly Button _enableHomesBtn;
    private Rectangle _homeSlotsInputRect;

    // Navigation
    private readonly Button _backBtn;

    // Settings
    private bool _generateOres;
    private bool _generateCaves;
    private bool _generateStructures;
    private bool _generateTrees;
    private string _worldType;

    private bool _enableHomes;
    private int _maxHomesPerPlayer = 2;
    private bool _unlimitedHomes;
    private int _maxHomesCap = 10;
    private bool _homeSlotsInputActive;
    private string _homeSlotsInputText = "2";
    private double _now;

    // Layout
    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _contentArea;
    private Rectangle _tabsArea;

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
        bool generateTrees,
        string worldType,
        bool enableHomes,
        int maxHomesPerPlayer,
        int maxHomesCap,
        Action<bool, bool, bool, bool, string, bool, int> onSettingsChanged)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;

        // Defaults to "everything on" unless the caller explicitly disables.
        _generateOres = generateOres;
        _generateCaves = generateCaves;
        _generateStructures = generateStructures;
        _generateTrees = generateTrees;
        _worldType = NormalizeWorldType(worldType);

        _enableHomes = enableHomes;
        _maxHomesCap = Math.Clamp(maxHomesCap, 1, 64);
        _maxHomesPerPlayer = Math.Clamp(maxHomesPerPlayer, 1, _maxHomesCap);
        _homeSlotsInputText = _maxHomesPerPlayer.ToString();

        _onSettingsChanged = onSettingsChanged;

        // Tabs
        _tabWorldGenBtn = new Button("WORLD GEN", () => _activeTab = Tab.WorldGen) { BoldText = true };
        _tabGameplayBtn = new Button("GAMEPLAY", () => _activeTab = Tab.Gameplay) { BoldText = true };
        _tabPerfBtn = new Button("PERFORMANCE", () => _activeTab = Tab.Performance) { BoldText = true };

        // World gen
        _worldTypeBtn = new Button(string.Empty, ToggleWorldType);
        _generateTreesBtn = new Button(string.Empty, ToggleGenerateTrees);
        _generateOresBtn = new Button(string.Empty, ToggleGenerateOres);
        _generateCavesBtn = new Button(string.Empty, ToggleGenerateCaves);
        _generateStructuresBtn = new Button(string.Empty, ToggleGenerateStructures);

        // Gameplay
        _enableHomesBtn = new Button(string.Empty, ToggleEnableHomes);
        _homeSlotsInputRect = Rectangle.Empty;

        // Nav
        _backBtn = new Button("← BACK", () => _menus.Pop());

        SyncButtonLabels();
        LoadAssets();
    }


    private static string NormalizeWorldType(string? worldType)
    {
        return WorldMeta.CanonicalWorldType(worldType);
    }

    private static bool IsFlatlands(string? worldType)
        => string.Equals(WorldMeta.CanonicalWorldType(worldType), "flatlands", StringComparison.OrdinalIgnoreCase);

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
        var shrinkAmount = 60;
        _contentArea = new Rectangle(
            _panelRect.X + innerPadX + shrinkAmount / 2,
            _panelRect.Y + innerPadTop + shrinkAmount / 2,
            _panelRect.Width - innerPadX * 2 - shrinkAmount,
            _panelRect.Height - innerPadTop - innerPadBottom - shrinkAmount
        );

        // Tabs row
        var tabsH = 42;
        _tabsArea = new Rectangle(_contentArea.X, _contentArea.Y + 80, _contentArea.Width, tabsH);
        var tabGap = 10;
        var tabW = (_tabsArea.Width - tabGap * 2) / 3;
        var tabY = _tabsArea.Y;
        _tabWorldGenBtn.Bounds = new Rectangle(_tabsArea.X, tabY, tabW, tabsH);
        _tabGameplayBtn.Bounds = new Rectangle(_tabsArea.X + tabW + tabGap, tabY, tabW, tabsH);
        _tabPerfBtn.Bounds = new Rectangle(_tabsArea.X + (tabW + tabGap) * 2, tabY, tabW, tabsH);

        // Content buttons (worldgen)
        var buttonY = _tabsArea.Bottom + 18;
        var buttonW = Math.Max(260, _contentArea.Width - 40);
        var buttonH = 40;
        var buttonX = _contentArea.X + (_contentArea.Width - buttonW) / 2;

        _worldTypeBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 56;

        _generateTreesBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 56;

        _generateOresBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 56;
        _generateCavesBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);
        buttonY += 56;
        _generateStructuresBtn.Bounds = new Rectangle(buttonX, buttonY, buttonW, buttonH);

        // Gameplay layout
        var gY = _tabsArea.Bottom + 18;
        _enableHomesBtn.Bounds = new Rectangle(buttonX, gY, buttonW, buttonH);
        gY += 62;
        _homeSlotsInputRect = new Rectangle(buttonX + 170, gY, 120, 30);

        // Back button (bottom-left like other menus)
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
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

        // Tabs
        _tabWorldGenBtn.Update(input);
        _tabGameplayBtn.Update(input);
        _tabPerfBtn.Update(input);

        // Per-tab controls
        if (_activeTab == Tab.WorldGen)
        {
            _worldTypeBtn.Update(input);
            if (IsFlatlands(_worldType))
                _generateTreesBtn.Update(input);
            _generateOresBtn.Update(input);
            _generateCavesBtn.Update(input);
            _generateStructuresBtn.Update(input);
        }
        else if (_activeTab == Tab.Gameplay)
        {
            if (input.IsNewLeftClick())
                _homeSlotsInputActive = _homeSlotsInputRect.Contains(input.MousePosition) && _enableHomes;

            if (_homeSlotsInputActive)
            {
                HandleHomeSlotsInput(input);
                if (input.IsNewKeyPress(Keys.Enter))
                {
                    _homeSlotsInputActive = false;
                    return;
                }
            }

            _enableHomesBtn.Update(input);
        }

        _backBtn.Update(input);

        // Refresh tab highlight colors
        SyncTabStyles();
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);

        if (_bgTexture != null)
            sb.Draw(_bgTexture, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 180));

        sb.End();
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        if (_guiTexture != null)
            sb.Draw(_guiTexture, _panelRect, Color.White);
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(25, 25, 25, 240));
            DrawBorder(sb, _panelRect, new Color(100, 100, 100));
        }

        // Title
        const int borderSize = 16;
        var title = "MORE WORLD OPTIONS";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16 + borderSize + 80);
        _font.DrawString(sb, title, titlePos, Color.White);

        // Tabs
        _tabWorldGenBtn.Draw(sb, _pixel, _font);
        _tabGameplayBtn.Draw(sb, _pixel, _font);
        _tabPerfBtn.Draw(sb, _pixel, _font);

        // Content
        if (_activeTab == Tab.WorldGen)
        {
            _worldTypeBtn.Draw(sb, _pixel, _font);
            _generateTreesBtn.Draw(sb, _pixel, _font);
            _generateOresBtn.Draw(sb, _pixel, _font);
            _generateCavesBtn.Draw(sb, _pixel, _font);
            _generateStructuresBtn.Draw(sb, _pixel, _font);
        }
        else if (_activeTab == Tab.Gameplay)
        {
            _enableHomesBtn.Draw(sb, _pixel, _font);
            DrawHomeSlotsInput(sb);
        }
        else
        {
            var msg = "PERFORMANCE OPTIONS (COMING SOON)";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_contentArea.Center.X - size.X / 2f, _tabsArea.Bottom + 60);
            _font.DrawString(sb, msg, pos, new Color(220, 220, 220));
        }

        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void SyncTabStyles()
    {
        _tabWorldGenBtn.BackgroundColor = _activeTab == Tab.WorldGen ? new Color(52, 82, 122, 230) : new Color(20, 20, 20, 230);
        _tabGameplayBtn.BackgroundColor = _activeTab == Tab.Gameplay ? new Color(52, 82, 122, 230) : new Color(20, 20, 20, 230);
        _tabPerfBtn.BackgroundColor = _activeTab == Tab.Performance ? new Color(52, 82, 122, 230) : new Color(20, 20, 20, 230);
    }

    private void DrawHomeSlotsInput(SpriteBatch sb)
    {
        // Label
        var labelX = _homeSlotsInputRect.X - _font.MeasureString("HOME SLOTS:").X - 10;
        _font.DrawString(sb, "HOME SLOTS:", new Vector2(labelX, _homeSlotsInputRect.Y + 6), Color.White);

        // Input field
        sb.Draw(_pixel, _homeSlotsInputRect, _homeSlotsInputActive ? new Color(35, 35, 35, 235) : new Color(20, 20, 20, 220));
        DrawBorder(sb, _homeSlotsInputRect, _enableHomes ? Color.White : new Color(120, 120, 120));

        // Text
        var displayText = string.IsNullOrWhiteSpace(_homeSlotsInputText) ? "2" : _homeSlotsInputText;
        var textColor = _enableHomes ? Color.White : new Color(120, 120, 120);
        var textPos = new Vector2(_homeSlotsInputRect.X + 8, _homeSlotsInputRect.Y + (_homeSlotsInputRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, displayText, textPos, textColor);

        // Cursor
        if (_homeSlotsInputActive && ((_now * 2.0) % 2.0) < 1.0 && _enableHomes)
        {
            var cursorX = textPos.X + _font.MeasureString(displayText).X + 2f;
            var cursorRect = new Rectangle((int)cursorX, _homeSlotsInputRect.Y + 6, 2, _homeSlotsInputRect.Height - 12);
            sb.Draw(_pixel, cursorRect, Color.White);
        }
    }

    private void ToggleWorldType()
    {
        _worldType = IsFlatlands(_worldType) ? "terrain" : "flatlands";
        if (!IsFlatlands(_worldType))
            _generateTrees = true;

        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void ToggleGenerateTrees()
    {
        if (!IsFlatlands(_worldType))
            return;
        _generateTrees = !_generateTrees;
        SyncButtonLabels();
        NotifySettingsChanged();
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

    private void ToggleEnableHomes()
    {
        _enableHomes = !_enableHomes;
        SyncButtonLabels();
        NotifySettingsChanged();
    }

    private void HandleHomeSlotsInput(InputState input)
    {
        if (!_homeSlotsInputActive || !_enableHomes)
            return;

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
        var isFlatlands = IsFlatlands(_worldType);

        _worldTypeBtn.Label = isFlatlands ? "FLATLANDS: Enabled" : "FLATLANDS: Disabled";
        _worldTypeBtn.Enabled = true;
        _worldTypeBtn.ForceDisabledStyle = false;

        if (isFlatlands)
        {
            _generateTreesBtn.Label = _generateTrees ? "TREES (FLATLANDS): Enabled" : "TREES (FLATLANDS): Disabled";
            _generateTreesBtn.Enabled = true;
            _generateTreesBtn.ForceDisabledStyle = false;
        }
        else
        {
            _generateTrees = true;
            _generateTreesBtn.Label = "TREES: ALWAYS ENABLED (TERRAIN)";
            _generateTreesBtn.Enabled = false;
            _generateTreesBtn.ForceDisabledStyle = true;
        }
        _generateOresBtn.Label = _generateOres ? "ORES: Enabled" : "ORES: Disabled";
        _generateCavesBtn.Label = _generateCaves ? "CAVES: Enabled" : "CAVES: Disabled";
        _generateStructuresBtn.Label = _generateStructures ? "STRUCTURES: Enabled" : "STRUCTURES: Disabled";

        _enableHomesBtn.Label = _enableHomes ? "HOMES: Enabled" : "HOMES: Disabled";
    }

    private void NotifySettingsChanged()
    {
        _onSettingsChanged?.Invoke(
            _generateOres,
            _generateCaves,
            _generateStructures,
            _generateTrees,
            _worldType,
            _enableHomes,
            _unlimitedHomes ? -1 : _maxHomesPerPlayer);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}
