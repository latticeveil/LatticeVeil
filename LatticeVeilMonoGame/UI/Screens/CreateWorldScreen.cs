using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class CreateWorldScreen : IScreen
{
    private const int MaxWorldNameLength = 32;
    private const int PanelMaxWidth = 1248;
    private const int PanelMaxHeight = 680;
    private const int ControlShrinkPixels = 60; // ~2 inches at 30 px/in
    private const int ContentDownShiftPixels = 30; // ~1 inch at 30 px/in

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly Action<string> _onWorldCreated;

    private Texture2D? _bg;
    private Texture2D? _panel;
    private Texture2D? _artificerTexture;
    private Texture2D? _artificerSelectedTexture;
    private Texture2D? _veilwalkerTexture;
    private Texture2D? _veilwalkerSelectedTexture;
    private Texture2D? _veilseerTexture;
    private Texture2D? _veilseerSelectedTexture;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _contentArea;
    private Rectangle _worldNameRect;

    // UI Elements
    private readonly Button _createBtn;
    private readonly Button _cancelBtn;
    private readonly Button _artificerBtn;
    private readonly Button _veilwalkerBtn;
    private readonly Button _veilseerBtn;
    private readonly Button _moreWorldOptionsBtn;
    private readonly Button _enableCheatsBtn;
    private readonly Button _difficultyBtn;
    private readonly Button _enableHomesBtn;
    private readonly Button _resumeGenerationBtn;
    private readonly Button _abortGenerationBtn;
    private readonly List<Rectangle> _difficultyOptionRects = new();
    private bool _moreWorldOptionsOpen;

    // World settings
    private string _worldName = "New World";
    private Core.GameMode _selectedGameMode = Core.GameMode.Artificer;
    private int _difficulty = 1; // 0=Peaceful, 1=Easy, 2=Normal, 3=Hard
    private bool _structuresEnabled = true;
    private bool _cavesEnabled = true;
    private bool _oresEnabled = true;
    private bool _treesEnabled = true;
    private string _worldType = "terrain";
    private bool _cheatsEnabled = false;
    private bool _multipleHomesEnabled = true;
    private int _maxHomesPerPlayer = 2;
    private bool _unlimitedHomes = false;
    private int _maxHomesCap = 10;
    private bool _playerCollisionEnabled = true;
    private bool _difficultyDropdownOpen;
    private string _modeDescriptionText = string.Empty;
    private bool _worldNameActive = true;
    private string _statusMessage = string.Empty;
    private double _statusUntil;
    private double _now;
    private bool _isGeneratingWorld;
    private float _generationProgress;
    private string _generationStage = string.Empty;
    private Task<WorldCreateResult>? _createTask;
    private CancellationTokenSource? _generationCts;
    private DateTime _lastHeartbeatUtc = DateTime.UtcNow;
    private DateTime _lastProgressMovementUtc = DateTime.UtcNow;
    private float _lastWatchdogProgress = -1f;
    private string _lastWatchdogStage = string.Empty;
    private bool _generationPausePromptOpen;
    private string _generationPauseReason = string.Empty;
    private Rectangle _generationPausePromptRect;
    private string _activeWorldName = string.Empty;
    private string _activeWorldPath = string.Empty;
    private WorldMeta? _activeWorldMeta;
    private bool _discardCreateResult;
    private volatile bool _generationStateActive;
    private bool _resumeBootstrapPending;
    private readonly string? _resumeWorldPath;
    private readonly object _generationLock = new();

    public CreateWorldScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, Action<string> onWorldCreated, string? resumeWorldPath = null)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _onWorldCreated = onWorldCreated;

        // Create UI elements
        _createBtn = new Button("CREATE WORLD", CreateWorld);
        _cancelBtn = new Button("CANCEL", () => _menus.Pop());
        _artificerBtn = new Button("ARTIFICER", () => SetGameMode(Core.GameMode.Artificer));
        _veilwalkerBtn = new Button("VEILWALKER", () => SetGameMode(Core.GameMode.Veilwalker));
        _veilseerBtn = new Button("VEILSEER", () => SetGameMode(Core.GameMode.Veilseer));
        _enableCheatsBtn = new Button(string.Empty, ToggleEnableCheats);
        _difficultyBtn = new Button(string.Empty, ToggleDifficultyDropdown);
        _enableHomesBtn = new Button(string.Empty, ToggleEnableHomes);
        _moreWorldOptionsBtn = new Button("MORE WORLD OPTIONS", OpenMoreWorldOptions);
        _resumeGenerationBtn = new Button("RESUME GENERATION", ResumeGeneration) { BoldText = true };
        _abortGenerationBtn = new Button("ABORT + DELETE WORLD", AbortAndDeleteGeneration) { BoldText = true, BackgroundColor = new Color(100, 26, 26) };
        _resumeWorldPath = string.IsNullOrWhiteSpace(resumeWorldPath) ? null : resumeWorldPath;
        _resumeBootstrapPending = !string.IsNullOrWhiteSpace(_resumeWorldPath);

        var settings = GameSettings.LoadOrCreate(_log);
        _maxHomesCap = Math.Clamp(settings.CreateWorldHomesCap, 1, 64);
        _maxHomesPerPlayer = Math.Clamp(_maxHomesPerPlayer, 1, _maxHomesCap);
        SyncDifficultyLabel();
        SyncEnableCheatsLabel();
        SyncEnableHomesLabel();

        LoadAssets();
        RefreshGameModeButtonTextures();

        if (_resumeBootstrapPending && !string.IsNullOrWhiteSpace(_resumeWorldPath))
        {
            _worldNameActive = false;
            _worldName = Path.GetFileName(_resumeWorldPath) ?? "RECOVERING WORLD";
            _statusMessage = "RESUMING INCOMPLETE GENERATION...";
            _statusUntil = 0d;
        }
    }

    private void OpenMoreWorldOptions()
    {
        var moreOptionsScreen = new MoreWorldOptionsScreen(
            _menus, _assets, _font, _pixel, _log,
            _oresEnabled, _cavesEnabled, _structuresEnabled, _treesEnabled, _worldType,
            _multipleHomesEnabled, _maxHomesPerPlayer, _playerCollisionEnabled, _maxHomesCap,
            (generateOres, generateCaves, generateStructures, generateTrees, worldType, enableHomes, homeSlots, playerCollision) =>
            {
                _oresEnabled = generateOres;
                _cavesEnabled = generateCaves;
                _structuresEnabled = generateStructures;
                _worldType = WorldMeta.CanonicalWorldType(worldType);
                _treesEnabled = string.Equals(_worldType, "flatlands", StringComparison.OrdinalIgnoreCase)
                    ? generateTrees
                    : true;
                _multipleHomesEnabled = enableHomes;
                _maxHomesPerPlayer = homeSlots == -1 ? _maxHomesCap : Math.Clamp(homeSlots, 1, _maxHomesCap);
                _unlimitedHomes = homeSlots == -1;
                _playerCollisionEnabled = playerCollision;
                SyncEnableCheatsLabel();
                SyncEnableHomesLabel();
            });
        _menus.Push(moreOptionsScreen, _viewport);
    }

    private void ToggleEnableHomes()
    {
        _multipleHomesEnabled = !_multipleHomesEnabled;
        SyncEnableHomesLabel();
    }

    private void ToggleEnableCheats()
    {
        _cheatsEnabled = !_cheatsEnabled;
        SyncEnableCheatsLabel();
    }

    private void SyncEnableHomesLabel()
    {
        _enableHomesBtn.Label = _multipleHomesEnabled ? "HOMES: Enabled" : "HOMES: Disabled";
    }

    private void SyncEnableCheatsLabel()
    {
        _enableCheatsBtn.Label = _cheatsEnabled ? "CHEATS: Enabled" : "CHEATS: Disabled";
    }

    private void LoadAssets()
    {
        _log.Info("CreateWorldScreen - Loading assets...");
        _bg = TryLoadTexture("textures/menu/backgrounds/CreateWorld_bg.png");
        _panel = TryLoadTexture("textures/menu/GUIS/CreateWorld_GUI.png");
        _createBtn.Texture = TryLoadTexture("textures/menu/buttons/CreateWorld.png");
        _cancelBtn.Texture = TryLoadTexture("textures/menu/buttons/Back.png");

        _artificerTexture = TryLoadTexture("textures/menu/buttons/Artificer.png");
        _artificerSelectedTexture = TryLoadTexture("textures/menu/buttons/ArtificerSelected.png");
        _veilwalkerTexture = TryLoadTexture("textures/menu/buttons/Veilwalker.png");
        _veilwalkerSelectedTexture = TryLoadTexture("textures/menu/buttons/VeilwalkerSelected.png");
        _veilseerTexture = TryLoadTexture("textures/menu/buttons/Veilseer.png");
        _veilseerSelectedTexture = TryLoadTexture("textures/menu/buttons/VeilseerSelected.png");

        RefreshGameModeButtonTextures();
        _log.Info("CreateWorldScreen - Assets loaded");
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

        // Keep interactive controls inside the visible center of the ornate panel texture.
        var innerPadX = Math.Clamp(_panelRect.Width / 16, 38, 72);
        var innerPadTop = Math.Clamp(_panelRect.Height / 8, 44, 78);
        var innerPadBottom = Math.Clamp(_panelRect.Height / 10, 34, 60);
        _contentArea = new Rectangle(
            _panelRect.X + innerPadX,
            _panelRect.Y + innerPadTop,
            _panelRect.Width - innerPadX * 2,
            _panelRect.Height - innerPadTop - innerPadBottom
        );

        var worldNameY = _contentArea.Y + 8 + ContentDownShiftPixels;
        var worldNameAreaX = _contentArea.X;
        var worldNameAreaW = _contentArea.Width;
        var worldNameW = Math.Clamp(worldNameAreaW - ControlShrinkPixels, 280, 980);
        _worldNameRect = new Rectangle(
            worldNameAreaX + (worldNameAreaW - worldNameW) / 2,
            worldNameY + _font.LineHeight + 8,
            worldNameW,
            _font.LineHeight + 18);

        var modeButtonGap = 20;
        var baseModeButtonW = Math.Clamp((_contentArea.Width - modeButtonGap * 4) / 3, 140, 260);
        var modeButtonW = Math.Max(100, baseModeButtonW - ControlShrinkPixels);
        var modeButtonH = Math.Clamp((int)(modeButtonW * 0.34f), 44, 96);
        var modeY = _worldNameRect.Bottom + 36;
        var modeStartX = _contentArea.X + (_contentArea.Width - (modeButtonW * 3 + modeButtonGap * 2)) / 2;

        _artificerBtn.Bounds = new Rectangle(modeStartX, modeY, modeButtonW, modeButtonH);
        _veilwalkerBtn.Bounds = new Rectangle(modeStartX + modeButtonW + modeButtonGap, modeY, modeButtonW, modeButtonH);
        _veilseerBtn.Bounds = new Rectangle(modeStartX + (modeButtonW + modeButtonGap) * 2, modeY, modeButtonW, modeButtonH);

        var difficultyHeaderY = _artificerBtn.Bounds.Bottom + 52;
        const int buttonH = 30;
        const int buttonSpacing = 20;
        const int toggleCount = 2;
        var availableWidth = Math.Max(420, _contentArea.Width - 24);
        var buttonW = Math.Clamp((availableWidth - (buttonSpacing * (toggleCount - 1))) / toggleCount, 140, 220);
        var totalWidth = buttonW * toggleCount + buttonSpacing * (toggleCount - 1);
        var startX = _contentArea.Center.X - totalWidth / 2;

        _difficultyBtn.Bounds = new Rectangle(_contentArea.Center.X - 260, difficultyHeaderY + 24, 520, 34);

        var coreRulesY = _difficultyBtn.Bounds.Bottom + 92;
        _enableHomesBtn.Bounds = new Rectangle(startX, coreRulesY + 24, buttonW, buttonH);
        _enableCheatsBtn.Bounds = new Rectangle(startX + buttonW + buttonSpacing, coreRulesY + 24, buttonW, buttonH);
        
        // More World Options below them
        var moreOptionsY = _enableHomesBtn.Bounds.Bottom + 42;
        _moreWorldOptionsBtn.Bounds = new Rectangle(
            _contentArea.Center.X - 130,
            moreOptionsY,
            260,
            30);
            
        RebuildDropdownLayouts();

        // Keep create anchored to the bottom-center of the full screen so world options remain visible.
        var createBtnW = Math.Min(320, panelW / 4);
        var createBtnH = Math.Max(44, (int)(createBtnW * 0.22f));
        var createBtnX = viewport.Center.X - createBtnW / 2;
        var buttonY = viewport.Bottom - 20 - createBtnH;
        _createBtn.Bounds = new Rectangle(createBtnX, buttonY, createBtnW, createBtnH);

        // Match Options screen back button position (bottom-left of full screen) - use same logic as SingleplayerScreen
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_cancelBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_cancelBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH)); // Match options screen
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _cancelBtn.Bounds = new Rectangle(
            viewport.X + backBtnMargin,
            viewport.Bottom - backBtnMargin - backBtnH,
            backBtnW,
            backBtnH);

        var modalW = Math.Clamp(_panelRect.Width - 160, 360, 640);
        var modalH = 200;
        _generationPausePromptRect = new Rectangle(
            _panelRect.Center.X - modalW / 2,
            _panelRect.Center.Y - modalH / 2,
            modalW,
            modalH);
        var buttonW2 = Math.Clamp((modalW - 48) / 2, 140, 280);
        var buttonH2 = 44;
        var buttonsY = _generationPausePromptRect.Bottom - buttonH2 - 20;
        _resumeGenerationBtn.Bounds = new Rectangle(_generationPausePromptRect.X + 16, buttonsY, buttonW2, buttonH2);
        _abortGenerationBtn.Bounds = new Rectangle(_generationPausePromptRect.Right - 16 - buttonW2, buttonsY, buttonW2, buttonH2);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusUntil > 0 && _now >= _statusUntil)
            _statusMessage = string.Empty;

        if (_resumeBootstrapPending)
        {
            _resumeBootstrapPending = false;
            BeginResumeGeneration();
        }

        if (_createTask != null && _createTask.IsCompleted)
            CompleteCreateTask();

        if (_generationPausePromptOpen)
        {
            _resumeGenerationBtn.Update(input);
            _abortGenerationBtn.Update(input);
            return;
        }

        if (_isGeneratingWorld)
        {
            UpdateGenerationWatchdog();
            return;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            _worldNameActive = _worldNameRect.Contains(input.MousePosition);
        }

        if (_worldNameActive)
        {
            HandleTextInput(input, ref _worldName, MaxWorldNameLength);
            if (input.IsNewKeyPress(Keys.Enter))
            {
                CreateWorld();
                return;
            }
        }
        
        if (input.IsNewLeftClick() && HandleDropdownClick(input.MousePosition))
            return;

        _artificerBtn.Update(input);
        _veilwalkerBtn.Update(input);
        _veilseerBtn.Update(input);
        _modeDescriptionText = ResolveHoveredModeDescription(input.MousePosition);

        if (!_worldNameActive && input.IsNewKeyPress(Keys.Left))
        {
            SetGameMode(_selectedGameMode switch
            {
                Core.GameMode.Artificer => Core.GameMode.Veilseer,
                Core.GameMode.Veilwalker => Core.GameMode.Artificer,
                _ => Core.GameMode.Veilwalker
            });
        }
        else if (!_worldNameActive && input.IsNewKeyPress(Keys.Right))
        {
            SetGameMode(_selectedGameMode switch
            {
                Core.GameMode.Artificer => Core.GameMode.Veilwalker,
                Core.GameMode.Veilwalker => Core.GameMode.Veilseer,
                _ => Core.GameMode.Artificer
            });
        }

        _enableHomesBtn.Update(input);
        _enableCheatsBtn.Update(input);

        _difficultyBtn.Update(input);
        _moreWorldOptionsBtn.Update(input);

        _createBtn.Update(input);
        _cancelBtn.Update(input);
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

        // While generating, show only the loading overlay to avoid the create UI sitting behind it.
        if (_isGeneratingWorld)
        {
            DrawGeneratingOverlay(sb);
            if (_generationPausePromptOpen)
                DrawGenerationPausePrompt(sb);
            sb.End();
            return;
        }

        // Draw panel
        if (_panel != null)
        {
            DrawNinePatch(sb, _panel, _panelRect);
        }
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(25, 25, 25));
        }

        // Draw title
        const int borderSize = 16;
        var title = "CREATE NEW WORLD";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16 + borderSize + 80);  // Move down 80 pixels total
        _font.DrawString(sb, title, titlePos, Color.White);

        // Draw world info
        var x = _contentArea.X + 24;
        _font.DrawString(sb, "WORLD NAME", new Vector2(_worldNameRect.X, _worldNameRect.Y - _font.LineHeight - 6), Color.White);
        sb.Draw(_pixel, _worldNameRect, _worldNameActive ? new Color(35, 35, 35, 235) : new Color(20, 20, 20, 220));
        DrawBorder(sb, _worldNameRect, Color.White);

        var worldNameText = string.IsNullOrWhiteSpace(_worldName) ? "(enter world name)" : _worldName;
        var worldNameColor = string.IsNullOrWhiteSpace(_worldName) ? new Color(180, 180, 180) : Color.White;
        var worldNamePos = new Vector2(_worldNameRect.X + 8, _worldNameRect.Y + (_worldNameRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, worldNameText, worldNamePos, worldNameColor);

        if (_worldNameActive && ((_now * 2.0) % 2.0) < 1.0)
        {
            var cursorText = _worldName;
            var cursorX = worldNamePos.X + _font.MeasureString(cursorText).X + 2f;
            var cursorRect = new Rectangle((int)cursorX, _worldNameRect.Y + 6, 2, _worldNameRect.Height - 12);
            sb.Draw(_pixel, cursorRect, Color.White);
        }

        _font.DrawString(sb, "GAME MODE", new Vector2(x, _worldNameRect.Bottom + 18), Color.White);

        _artificerBtn.Draw(sb, _pixel, _font);
        _veilwalkerBtn.Draw(sb, _pixel, _font);
        _veilseerBtn.Draw(sb, _pixel, _font);

        DrawModeDescriptionBox(sb);
        DrawSectionHeader(sb, "DIFFICULTY", new Vector2(_difficultyBtn.Bounds.X, _difficultyBtn.Bounds.Y - _font.LineHeight - 6));
        _difficultyBtn.Draw(sb, _pixel, _font);
        DrawDifficultyInfoBox(sb);
        var coreRulesLabel = "CORE RULES";
        var coreRulesSize = _font.MeasureString(coreRulesLabel);
        var coreRulesX = _contentArea.Center.X - coreRulesSize.X / 2f;
        DrawSectionHeader(sb, coreRulesLabel, new Vector2(coreRulesX, _enableHomesBtn.Bounds.Y - _font.LineHeight - 18));
        _enableHomesBtn.Draw(sb, _pixel, _font);
        _enableCheatsBtn.Draw(sb, _pixel, _font);
        _moreWorldOptionsBtn.Draw(sb, _pixel, _font);

        // Draw buttons
        _createBtn.Draw(sb, _pixel, _font);
        _cancelBtn.Draw(sb, _pixel, _font);
        
        // Draw dropdowns and panels last (highest z-order)
        if (_difficultyDropdownOpen)
            DrawDifficultyDropdown(sb);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var statusSize = _font.MeasureString(_statusMessage);
            var statusPos = new Vector2(_panelRect.Center.X - statusSize.X / 2f, _createBtn.Bounds.Top - _font.LineHeight - 8);
            _font.DrawString(sb, _statusMessage, statusPos, new Color(230, 210, 90));
        }

        if (_generationPausePromptOpen)
        {
            DrawGenerationPausePrompt(sb);
        }

        sb.End();
    }

    private string GetDifficultyLabel() => GetDifficultyLabel(_difficulty);

    private string GetDifficultyDescription() => WorldDifficulty.GetDescription(_difficulty);

    private static string GetDifficultyLabel(int difficulty)
    {
        return WorldDifficulty.GetDisplayLabel(difficulty);
    }

    private void SetGameMode(Core.GameMode mode)
    {
        if (_selectedGameMode == mode)
            return;

        _selectedGameMode = mode;
        RefreshGameModeButtonTextures();
    }

    private void RefreshGameModeButtonTextures()
    {
        _artificerBtn.Texture = _selectedGameMode == Core.GameMode.Artificer
            ? _artificerSelectedTexture ?? _artificerTexture
            : _artificerTexture;

        _veilwalkerBtn.Texture = _selectedGameMode == Core.GameMode.Veilwalker
            ? _veilwalkerSelectedTexture ?? _veilwalkerTexture
            : _veilwalkerTexture;

        _veilseerBtn.Texture = _selectedGameMode == Core.GameMode.Veilseer
            ? _veilseerSelectedTexture ?? _veilseerTexture
            : _veilseerTexture;
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

    private void DrawNinePatch(SpriteBatch sb, Texture2D texture, Rectangle destination)
    {
        if (texture == null) return;
        
        const int borderSize = 16;
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        
        var patches = new[]
        {
            new Rectangle(source.X, source.Y, borderSize, borderSize),
            new Rectangle(source.X + borderSize, source.Y, source.Width - borderSize * 2, borderSize),
            new Rectangle(source.Right - borderSize, source.Y, borderSize, borderSize),
            new Rectangle(source.X, source.Y + borderSize, borderSize, source.Height - borderSize * 2),
            new Rectangle(source.X + borderSize, source.Y + borderSize, source.Width - borderSize * 2, source.Height - borderSize * 2),
            new Rectangle(source.Right - borderSize, source.Y + borderSize, borderSize, source.Height - borderSize * 2),
            new Rectangle(source.X, source.Bottom - borderSize, borderSize, borderSize),
            new Rectangle(source.X + borderSize, source.Bottom - borderSize, source.Width - borderSize * 2, borderSize),
            new Rectangle(source.Right - borderSize, source.Bottom - borderSize, borderSize, borderSize)
        };
        
        var destPatches = new[]
        {
            new Rectangle(destination.X, destination.Y, borderSize, borderSize),
            new Rectangle(destination.X + borderSize, destination.Y, destination.Width - borderSize * 2, borderSize),
            new Rectangle(destination.Right - borderSize, destination.Y, borderSize, borderSize),
            new Rectangle(destination.X, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2),
            new Rectangle(destination.X + borderSize, destination.Y + borderSize, destination.Width - borderSize * 2, destination.Height - borderSize * 2),
            new Rectangle(destination.Right - borderSize, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2),
            new Rectangle(destination.X, destination.Bottom - borderSize, borderSize, borderSize),
            new Rectangle(destination.X + borderSize, destination.Bottom - borderSize, destination.Width - borderSize * 2, borderSize),
            new Rectangle(destination.Right - borderSize, destination.Bottom - borderSize, borderSize, borderSize)
        };
        
        for (int i = 0; i < 9; i++)
        {
            sb.Draw(texture, destPatches[i], patches[i], Color.White);
        }
    }

    private void CreateWorld()
    {
        if (_isGeneratingWorld)
            return;

        var worldName = NormalizeWorldName(_worldName);
        _worldName = worldName;
        if (string.IsNullOrWhiteSpace(worldName))
        {
            _log.Warn("Cannot create world: empty name");
            SetStatus("ENTER A WORLD NAME");
            return;
        }

        var worldPath = Path.Combine(Paths.WorldsDir, worldName);
        if (Directory.Exists(worldPath))
        {
            // If this folder was created by a failed pre-bootstrap heartbeat and has no world manifest,
            // treat it as transient state and clear it so the user can create the world normally.
            if (!TryDeleteGhostPreGenerationFolder(worldPath))
            {
                _log.Warn($"World directory already exists: {worldPath}");
                SetStatus("WORLD NAME ALREADY EXISTS");
                return;
            }
        }

        var (width, height, depth) = GetWorldDimensions();
        var seed = Environment.TickCount;
        var meta = WorldMeta.CreateTerrain(worldName, _selectedGameMode, width, height, depth, seed);
        meta.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
        meta.PlayerCollision = _playerCollisionEnabled;
        meta.Player.PlayerCollision = _playerCollisionEnabled;
        meta.EnableMultipleHomes = _multipleHomesEnabled;
        meta.MaxHomesPerPlayer = _multipleHomesEnabled ? (_unlimitedHomes ? -1 : Math.Clamp(_maxHomesPerPlayer, 1, Math.Max(1, _maxHomesCap))) : 1;
        meta.EnableCheats = _cheatsEnabled;
        meta.DifficultyLevel = Math.Clamp(_difficulty, 0, 3);

        // World generation selections
        meta.WorldGeneration.GenerateStructures = _structuresEnabled;
        meta.WorldGeneration.GenerateCaves = _cavesEnabled;
        meta.WorldGeneration.GenerateOres = _oresEnabled;
        meta.WorldGeneration.WorldType = WorldMeta.CanonicalWorldType(_worldType);
        meta.WorldGeneration.GenerateTrees = string.Equals(meta.WorldGeneration.WorldType, "flatlands", StringComparison.OrdinalIgnoreCase)
            ? _treesEnabled
            : true;
        meta.Generator = WorldMeta.CanonicalGeneratorForWorldType(meta.WorldGeneration.WorldType);

        StartWorldGeneration(worldName, worldPath, meta, allowExistingWorld: false, resumeState: null);
    }

    private void BeginResumeGeneration()
    {
        var resumePath = !string.IsNullOrWhiteSpace(_activeWorldPath) ? _activeWorldPath : _resumeWorldPath;
        if (string.IsNullOrWhiteSpace(resumePath))
        {
            SetStatus("NO WORLD TO RESUME");
            return;
        }

        if (!Directory.Exists(resumePath))
        {
            SetStatus("PARTIAL WORLD MISSING");
            return;
        }

        var worldManifestPath = FileConventions.GetWorldManifestPath(resumePath);
        if (!File.Exists(worldManifestPath))
        {
            if (TryDeleteGhostPreGenerationFolder(resumePath))
                SetStatus("REMOVED STALE PARTIAL WORLD");
            else
                SetStatus("NO RESUMABLE GENERATION FOUND");
            return;
        }

        var metaPath = Paths.ResolveWorldMetaPath(resumePath);
        if (!File.Exists(metaPath))
        {
            SetStatus("PARTIAL WORLD METADATA MISSING");
            return;
        }

        var meta = WorldMeta.Load(metaPath, _log);
        if (meta == null)
        {
            SetStatus("FAILED TO LOAD WORLD METADATA");
            return;
        }

        _structuresEnabled = meta.WorldGeneration?.GenerateStructures ?? true;
        _cavesEnabled = meta.WorldGeneration?.GenerateCaves ?? true;
        _oresEnabled = meta.WorldGeneration?.GenerateOres ?? true;
        _worldType = WorldMeta.CanonicalWorldType(meta.WorldGeneration?.WorldType);
        _treesEnabled = string.Equals(_worldType, "flatlands", StringComparison.OrdinalIgnoreCase)
            ? (meta.WorldGeneration?.GenerateTrees ?? true)
            : true;
        _cheatsEnabled = meta.Gameplay?.EnableCheats ?? meta.EnableCheats;
        _multipleHomesEnabled = meta.Gameplay?.EnableMultipleHomes ?? meta.EnableMultipleHomes;
        _maxHomesPerPlayer = meta.Gameplay?.MaxHomesPerPlayer ?? meta.MaxHomesPerPlayer;
        _playerCollisionEnabled = meta.Player?.PlayerCollision ?? meta.PlayerCollision;

        WorldGenerationStateStore.TryLoadRecoverableState(resumePath, TimeSpan.FromSeconds(90), out var resumeState, _log);
        StartWorldGeneration(meta.Name, resumePath, meta, allowExistingWorld: true, resumeState: resumeState);
    }

    private void StartWorldGeneration(string worldName, string worldPath, WorldMeta meta, bool allowExistingWorld, WorldGenerationState? resumeState)
    {
        if (_isGeneratingWorld || (_createTask != null && !_createTask.IsCompleted))
            return;

        _activeWorldName = worldName;
        _activeWorldPath = worldPath;
        _activeWorldMeta = meta;

        _isGeneratingWorld = true;
        _generationPausePromptOpen = false;
        _generationPauseReason = string.Empty;
        SetGenerationProgress(0f, "PREPARING");
        _statusMessage = string.Empty;
        _statusUntil = 0d;
        _lastHeartbeatUtc = DateTime.UtcNow;
        _lastProgressMovementUtc = DateTime.UtcNow;
        _lastWatchdogProgress = -1f;
        _lastWatchdogStage = string.Empty;
        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        _discardCreateResult = false;
        _generationStateActive = allowExistingWorld && File.Exists(FileConventions.GetWorldManifestPath(worldPath));

        var worldGeneration = meta.WorldGeneration ?? new WorldGenerationSettings();
        meta.WorldGeneration = worldGeneration;
        var worldType = WorldMeta.CanonicalWorldType(worldGeneration.WorldType);
        var treesEnabled = string.Equals(worldType, "flatlands", StringComparison.OrdinalIgnoreCase)
            ? worldGeneration.GenerateTrees
            : true;
        _treesEnabled = treesEnabled;
        _worldType = worldType;
        worldGeneration.WorldType = worldType;
        worldGeneration.GenerateTrees = treesEnabled;
        meta.Generator = WorldMeta.CanonicalGeneratorForWorldType(worldType);

        _createTask = Task.Run(() => CreateWorldTask(worldName, worldPath, meta, allowExistingWorld, resumeState, _generationCts.Token));
    }

    private void UpdateGenerationWatchdog()
    {
        const int stallSeconds = 45;

        float progress;
        string stage;
        DateTime heartbeatUtc;
        lock (_generationLock)
        {
            progress = _generationProgress;
            stage = _generationStage;
            heartbeatUtc = _lastHeartbeatUtc;
        }

        if (Math.Abs(progress - _lastWatchdogProgress) > 0.0001f
            || !string.Equals(stage, _lastWatchdogStage, StringComparison.Ordinal))
        {
            _lastWatchdogProgress = progress;
            _lastWatchdogStage = stage;
            _lastProgressMovementUtc = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var staleByHeartbeat = now - heartbeatUtc > TimeSpan.FromSeconds(stallSeconds);
        var staleByProgress = now - _lastProgressMovementUtc > TimeSpan.FromSeconds(stallSeconds);
        if (staleByHeartbeat && staleByProgress)
        {
            PauseGeneration("Generation stalled. Resume or abort this partial world.");
        }
    }

    private void PauseGeneration(string reason)
    {
        if (_generationPausePromptOpen)
            return;

        _generationCts?.Cancel();
        _isGeneratingWorld = false;
        _generationPausePromptOpen = true;
        _generationPauseReason = reason;

        // Include current stage/progress in logs so stalls are diagnosable from current.lvlog.
        try
        {
            _log.Warn($"World generation paused: stage=\"{_generationStage}\" progress={_generationProgress:0.000} reason=\"{reason}\"");
        }
        catch { }
        PersistGenerationState(WorldGenerationState.StatusPaused, _generationStage, _generationProgress, reason);
    }

    private void ResumeGeneration()
    {
        if (!_generationPausePromptOpen)
            return;

        if (_createTask != null && !_createTask.IsCompleted)
        {
            _generationPauseReason = "Waiting for active generation task to pause...";
            return;
        }

        _generationPausePromptOpen = false;
        _generationPauseReason = string.Empty;
        BeginResumeGeneration();
    }

    private void AbortAndDeleteGeneration()
    {
        _generationCts?.Cancel();
        _isGeneratingWorld = false;
        _generationPausePromptOpen = false;
        _discardCreateResult = true;
        _generationStateActive = false;

        if (string.IsNullOrWhiteSpace(_activeWorldPath) || !Directory.Exists(_activeWorldPath))
        {
            SetStatus("PARTIAL WORLD NOT FOUND");
            return;
        }

        try
        {
            ClearReadOnlyAttributes(_activeWorldPath);
            Directory.Delete(_activeWorldPath, true);
            _activeWorldPath = string.Empty;
            _activeWorldName = string.Empty;
            _activeWorldMeta = null;
            _onWorldCreated?.Invoke(string.Empty);
            _menus.Pop();
            SetStatus("PARTIAL WORLD DELETED");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete partial world {_activeWorldPath}: {ex.Message}");
            SetStatus("FAILED TO DELETE PARTIAL WORLD");
        }
    }

    private WorldCreateResult CreateWorldTask(
        string worldName,
        string worldPath,
        WorldMeta meta,
        bool allowExistingWorld,
        WorldGenerationState? resumeState,
        CancellationToken cancellationToken)
    {
        DateTime lastHeartbeatWriteUtc = DateTime.MinValue;
        float lastPersistedProgress = -1f;
        string lastPersistedStage = string.Empty;

        void Report(float progress, string stage, bool forcePersist = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetGenerationProgress(progress, stage);

            var now = DateTime.UtcNow;
            var stageChanged = !string.Equals(stage, lastPersistedStage, StringComparison.Ordinal);
            var progressJump = Math.Abs(progress - lastPersistedProgress) >= 0.02f;
            if (!forcePersist && !stageChanged && !progressJump && now - lastHeartbeatWriteUtc < TimeSpan.FromSeconds(2))
                return;

            if (_generationStateActive)
                PersistGenerationState(WorldGenerationState.StatusInProgress, stage, progress, string.Empty);
            lastHeartbeatWriteUtc = now;
            lastPersistedProgress = progress;
            lastPersistedStage = stage;
        }

        try
        {
            if (!allowExistingWorld && Directory.Exists(worldPath))
                return WorldCreateResult.Failure("WORLD NAME ALREADY EXISTS");

            meta.WorldGeneration.WorldType = WorldMeta.CanonicalWorldType(meta.WorldGeneration.WorldType);
            meta.WorldGeneration.GenerateTrees = string.Equals(meta.WorldGeneration.WorldType, "flatlands", StringComparison.OrdinalIgnoreCase)
                ? meta.WorldGeneration.GenerateTrees
                : true;
            meta.CanonicalizeWorldGenerationContract();

            _log.Info($"Creating world transaction: {worldName} seed={meta.Seed} type={meta.WorldGeneration.WorldType} generator={meta.Generator}");

            BiomeIndexStore? biomeIndex = null;
            if (resumeState != null && resumeState.Progress >= 0.30f)
            {
                biomeIndex = BiomeIndexStore.Load(worldPath, _log);
                if (biomeIndex != null && !biomeIndex.IsCompatible(meta))
                    biomeIndex = null;
            }

            Report(0.06f, "BIOME INDEX", forcePersist: true);
            biomeIndex ??= BiomeLocateService.BuildCoverageIndexWithReseed(meta, _log, maxAttempts: 64, stride: 64);
            cancellationToken.ThrowIfCancellationRequested();
            meta.CanonicalizeWorldGenerationContract();

            Report(0.12f, "METADATA", forcePersist: true);
            if (!WorldCreator.CreateNewWorld(worldPath, worldName, meta.Seed, meta.CurrentWorldGameMode))
                return WorldCreateResult.Failure("FAILED TO CREATE NEW WORLD STRUCTURE");

            var metaPath = Paths.GetWorldMetaPath(worldPath);
            meta.Save(metaPath, _log);
            _generationStateActive = true;

            Report(0.20f, "CONFIGURATION", forcePersist: true);
            var worldConfig = WorldConfig.FromWorldMeta(meta);
            worldConfig.WorldGeneration.GenerateStructures = _structuresEnabled;
            worldConfig.WorldGeneration.GenerateCaves = _cavesEnabled;
            worldConfig.WorldGeneration.GenerateOres = _oresEnabled;
            worldConfig.WorldGeneration.GenerateTrees = string.Equals(meta.WorldGeneration.WorldType, "flatlands", StringComparison.OrdinalIgnoreCase)
                ? _treesEnabled
                : true;
            worldConfig.WorldGeneration.WorldType = meta.WorldGeneration.WorldType;
            worldConfig.Gameplay.EnableCheats = _cheatsEnabled;
            worldConfig.Gameplay.EnableMultipleHomes = _multipleHomesEnabled;
            worldConfig.Gameplay.MaxHomesPerPlayer = _unlimitedHomes ? -1 : _maxHomesPerPlayer;
            meta.WorldGeneration = worldConfig.WorldGeneration;
            meta.Gameplay = worldConfig.Gameplay;
            meta.Player = worldConfig.Player;
            meta.Performance = worldConfig.Performance;
            meta.Save(metaPath, _log);

            Report(0.30f, "INDEX SAVE", forcePersist: true);
            biomeIndex.Save(worldPath, _log);
            Directory.CreateDirectory(worldPath);
            Directory.CreateDirectory(Path.Combine(worldPath, "regions"));

            Report(0.42f, "PREWARM", forcePersist: true);
            var spawn = GetSpawnPoint(meta);
            // Raise spawn Y well above terrain to avoid spawning underground
            const int spawnYOffset = 140; // Well above max terrain height (SeaLevel + 120)
            const int seaLevel = 64;
            var spawnY = seaLevel + spawnYOffset;
            VoxelWorld.WorldToChunk((int)spawn.X, spawnY, (int)spawn.Y, out var spawnChunkX, out _, out var spawnChunkZ);
            var spawnRegionX = (int)Math.Floor(spawnChunkX / 32.0);
            var spawnRegionZ = (int)Math.Floor(spawnChunkZ / 32.0);
            RegionChunkStore.EnsureRegionExists(worldPath, spawnRegionX, spawnRegionZ);

            var bakedChunks = new HashSet<ChunkCoord>();
            var gateChunks = new List<ChunkCoord>();
            var biomeAnchorChunks = new HashSet<ChunkCoord>();

            const int bakeRadius = 6;
            const int gateRadius = 3;
            using (var world = VoxelWorld.Load(worldPath, metaPath, _log))
            {
                if (world == null)
                    return WorldCreateResult.Failure("FAILED TO PREWARM WORLD");

                var maxCy = Math.Min(world.MaxChunkY, 5);
                if (maxCy < 0)
                    maxCy = 0;

                void BakeColumn(int cx, int cz, bool includeInGate)
                {
                    for (var cy = 0; cy <= maxCy; cy++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var coord = new ChunkCoord(cx, cy, cz);
                        if (bakedChunks.Add(coord))
                        {
                            var chunk = world.GetOrCreateChunkData(cx, cy, cz);
                            chunk.NeedsSave = true;
                            world.SaveChunk(coord);
                            chunk.NeedsSave = false;
                        }

                        if (includeInGate)
                            gateChunks.Add(coord);
                    }
                }

                var totalColumns = (bakeRadius * 2 + 1) * (bakeRadius * 2 + 1);
                var bakedColumnCount = 0;
                for (int dz = -bakeRadius; dz <= bakeRadius; dz++)
                {
                    for (int dx = -bakeRadius; dx <= bakeRadius; dx++)
                    {
                        var cx = spawnChunkX + dx;
                        var cz = spawnChunkZ + dz;
                        var includeInGate = Math.Abs(dx) <= gateRadius && Math.Abs(dz) <= gateRadius;
                        BakeColumn(cx, cz, includeInGate);
                        bakedColumnCount++;

                        // Heartbeat frequently while prewarming. A single column can take long
                        // with heavy generators, and the UI watchdog will pause if it sees no
                        // heartbeat/progress updates for too long.
                        var bakeProgressColumn = 0.42f + 0.18f * (bakedColumnCount / (float)Math.Max(1, totalColumns));
                        Report(bakeProgressColumn, "PREWARM");
                    }

                    var bakeProgress = 0.42f + 0.18f * (bakedColumnCount / (float)Math.Max(1, totalColumns));
                    Report(bakeProgress, "PREWARM");
                }

                foreach (var biome in new[] { BiomeId.Grasslands, BiomeId.Forest, BiomeId.Hills, BiomeId.Desert, BiomeId.Ocean })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!BiomeLocateService.TryGetAnchor(biomeIndex, biome, out var anchor))
                        continue;

                    VoxelWorld.WorldToChunk(anchor.X, 64, anchor.Z, out var anchorChunkX, out _, out var anchorChunkZ);
                    var anchorSurface = new ChunkCoord(anchorChunkX, Math.Min(maxCy, 4), anchorChunkZ);
                    biomeAnchorChunks.Add(anchorSurface);
                    var outOfSpawnBake = Math.Abs(anchorChunkX - spawnChunkX) > bakeRadius
                        || Math.Abs(anchorChunkZ - spawnChunkZ) > bakeRadius;
                    if (outOfSpawnBake)
                        BakeColumn(anchorChunkX, anchorChunkZ, includeInGate: false);
                }

                world.SaveAllLoadedChunks();
            }

            var manifest = new SpawnPrewarmManifest
            {
                SpawnChunkX = spawnChunkX,
                SpawnChunkZ = spawnChunkZ,
                BakeRadius = bakeRadius,
                GateRadius = gateRadius,
                BakedChunks = new List<ChunkCoord>(bakedChunks),
                GateChunks = gateChunks,
                BiomeAnchorChunks = new List<ChunkCoord>(biomeAnchorChunks)
            };
            SpawnPrewarmStore.Save(worldPath, manifest, _log);

            Report(0.86f, "WORLD PREVIEW", forcePersist: true);
            if (WorldStorageBudgetService.ShouldSkipNonCriticalWrites(worldPath, out var storageBudget))
            {
                _log.Warn($"World preview skipped due to storage-conserve mode. size={storageBudget.SizeBytes} bytes");
            }
            else
            {
                var previewTask = Task.Run(() =>
                {
                    WorldPreviewGenerator.GenerateAndSave(meta, worldPath, _log, cancellationToken: cancellationToken);
                }, cancellationToken);

                var previewDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(90);
                while (!previewTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Heartbeat while preview generation is running so the UI watchdog doesn't
                    // treat long previews as a stall.
                    Report(0.86f, "WORLD PREVIEW");

                    if (DateTime.UtcNow >= previewDeadlineUtc)
                    {
                        _log.Warn("World preview generation timed out; skipping preview to finish world creation.");
                        break;
                    }

                    Thread.Sleep(200);
                }

                // Observe exceptions if it finished.
                if (previewTask.IsCompleted)
                    previewTask.GetAwaiter().GetResult();
            }

            Report(1.0f, "FINALIZING", forcePersist: true);
            PersistGenerationState(WorldGenerationState.StatusCompleted, "DONE", 1f, string.Empty);
            _log.Info($"World generation complete: {worldName}.");
            return WorldCreateResult.Success(worldName, worldPath, meta);
        }
        catch (OperationCanceledException)
        {
            if (_generationStateActive)
            {
                PersistGenerationState(WorldGenerationState.StatusPaused, _generationStage, _generationProgress, "Generation paused.");
                return WorldCreateResult.Paused();
            }
            return WorldCreateResult.Failure("GENERATION CANCELED");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to create world: {ex.Message}");
            _log.Error($"Stack trace: {ex.StackTrace}");
            PersistGenerationState(WorldGenerationState.StatusFailed, _generationStage, _generationProgress, ex.Message);
            return WorldCreateResult.Failure("FAILED TO CREATE WORLD");
        }
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetTextInputKeys())
        {
            if (key == Keys.Back)
            {
                if (value.Length > 0)
                    value = value.Substring(0, value.Length - 1);
                continue;
            }

            if (key == Keys.Space)
            {
                Append(ref value, ' ', maxLen);
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                Append(ref value, shift ? '_' : '-', maxLen);
                continue;
            }

            if (key == Keys.OemPeriod || key == Keys.Decimal)
            {
                Append(ref value, '.', maxLen);
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                Append(ref value, (char)('0' + (key - Keys.D0)), maxLen);
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                Append(ref value, (char)('0' + (key - Keys.NumPad0)), maxLen);
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                var c = (char)('A' + (key - Keys.A));
                if (!shift)
                    c = char.ToLowerInvariant(c);
                Append(ref value, c, maxLen);
            }
        }
    }

    private static void Append(ref string value, char c, int maxLen)
    {
        if (value.Length >= maxLen)
            return;
        value += c;
    }

    private static string NormalizeWorldName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        var chars = value.ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        return new string(chars).Trim();
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
                return;

            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                file.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var dir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
                dir.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch
        {
            // Best effort only.
        }
    }

    private void SetStatus(string message, double seconds = 2.5)
    {
        _statusMessage = message;
        _statusUntil = seconds <= 0 ? 0 : _now + seconds;
    }

    private void SetGenerationProgress(float progress, string stage)
    {
        lock (_generationLock)
        {
            _generationProgress = Math.Clamp(progress, 0f, 1f);
            _generationStage = stage ?? string.Empty;
            _lastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    private void PersistGenerationState(string status, string stage, float progress, string errorReason)
    {
        if (!_generationStateActive || string.IsNullOrWhiteSpace(_activeWorldPath) || _activeWorldMeta == null)
            return;

        WorldGenerationStateStore.UpdateHeartbeat(
            _activeWorldPath,
            status,
            stage,
            progress,
            _activeWorldMeta.Seed,
            _activeWorldMeta.WorldGeneration.WorldType,
            _activeWorldMeta.Generator,
            errorReason,
            _activeWorldMeta.Name,
            _log);
    }

    private void CompleteCreateTask()
    {
        if (_createTask == null)
            return;

        WorldCreateResult result;
        try
        {
            result = _createTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error($"Create world task failed: {ex.Message}");
            result = WorldCreateResult.Failure("FAILED TO CREATE WORLD");
        }

        _createTask = null;
        _generationCts?.Dispose();
        _generationCts = null;

        if (_discardCreateResult)
        {
            _discardCreateResult = false;
            _generationStateActive = false;
            return;
        }

        if (result.IsPaused)
        {
            _isGeneratingWorld = false;
            _generationPausePromptOpen = true;
            if (string.IsNullOrWhiteSpace(_generationPauseReason))
                _generationPauseReason = "Generation paused.";
            return;
        }

        if (result.IsSuccess)
        {
            _isGeneratingWorld = false;
            _generationPausePromptOpen = false;
            _generationPauseReason = string.Empty;
            WorldGenerationStateStore.Delete(result.WorldPath, _log);
            _generationStateActive = false;
            _log.Info($"World created successfully: {result.WorldName}.");
            _onWorldCreated?.Invoke(result.WorldName);
            var metaPath = Paths.ResolveWorldMetaPath(result.WorldPath);
            var startPaused = !global::LatticeVeilMonoGame.Game1.WindowIsActive;
            _menus.Pop();
            _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, result.WorldPath, metaPath, startPaused: startPaused, showAttunementWipPopup: _selectedGameMode == Core.GameMode.Veilwalker), _viewport);
            return;
        }

        _isGeneratingWorld = false;
        _generationPausePromptOpen = false;
        _generationPauseReason = string.Empty;
        _generationStateActive = false;
        SetStatus(result.ErrorMessage ?? "FAILED TO CREATE WORLD");
    }

    private bool TryDeleteGhostPreGenerationFolder(string worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
            return false;

        var worldManifestPath = FileConventions.GetWorldManifestPath(worldPath);
        if (File.Exists(worldManifestPath))
            return false;

        var statePath = WorldGenerationStateStore.GetPath(worldPath);
        var tempStatePath = statePath + ".tmp";
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(worldPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to inspect world folder {worldPath}: {ex.Message}");
            return false;
        }

        var deletable = entries.Length == 0;
        if (!deletable)
        {
            deletable = true;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (string.Equals(entry, statePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, tempStatePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                deletable = false;
                break;
            }
        }

        if (!deletable)
            return false;

        try
        {
            ClearReadOnlyAttributes(worldPath);
            Directory.Delete(worldPath, true);
            _log.Info($"Removed stale pre-generation world folder: {worldPath}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to remove stale pre-generation world folder {worldPath}: {ex.Message}");
            return false;
        }
    }

    private void DrawGeneratingOverlay(SpriteBatch sb)
    {
        float progress;
        string stageValue;
        lock (_generationLock)
        {
            progress = _generationProgress;
            stageValue = _generationStage;
        }

        var overlay = new Rectangle(_panelRect.X + 40, _panelRect.Center.Y - 72, _panelRect.Width - 80, 144);
        sb.Draw(_pixel, overlay, new Color(0, 0, 0, 200));
        DrawBorder(sb, overlay, new Color(190, 190, 190));

        var title = "GENERATING WORLD";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(overlay.Center.X - titleSize.X / 2f, overlay.Y + 18), Color.White);

        var stage = string.IsNullOrWhiteSpace(stageValue) ? "PREPARING" : stageValue.ToUpperInvariant();
        var stageText = $"STAGE: {stage}";
        var stageSize = _font.MeasureString(stageText);
        _font.DrawString(sb, stageText, new Vector2(overlay.Center.X - stageSize.X / 2f, overlay.Y + 18 + _font.LineHeight + 8), new Color(220, 220, 220));

        var barRect = new Rectangle(overlay.X + 24, overlay.Bottom - 34, overlay.Width - 48, 14);
        sb.Draw(_pixel, barRect, new Color(22, 22, 22, 220));
        DrawBorder(sb, barRect, new Color(170, 170, 170));
        var fill = new Rectangle(barRect.X + 2, barRect.Y + 2, Math.Max(0, (int)MathF.Round((barRect.Width - 4) * progress)), Math.Max(1, barRect.Height - 4));
        sb.Draw(_pixel, fill, new Color(84, 174, 255, 220));
    }

    private void DrawGenerationPausePrompt(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewport, new Color(0, 0, 0, 180));
        sb.Draw(_pixel, _generationPausePromptRect, new Color(20, 20, 20, 240));
        DrawBorder(sb, _generationPausePromptRect, new Color(230, 230, 230));

        var title = "WORLD GENERATION PAUSED";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_generationPausePromptRect.Center.X - titleSize.X / 2f, _generationPausePromptRect.Y + 14), Color.White);

        var reason = string.IsNullOrWhiteSpace(_generationPauseReason) ? "Generation paused." : _generationPauseReason;
        var reasonSize = _font.MeasureString(reason);
        _font.DrawString(
            sb,
            reason,
            new Vector2(_generationPausePromptRect.Center.X - reasonSize.X / 2f, _generationPausePromptRect.Y + 14 + _font.LineHeight + 10),
            new Color(220, 220, 220));

        _resumeGenerationBtn.Draw(sb, _pixel, _font);
        _abortGenerationBtn.Draw(sb, _pixel, _font);
    }

    private void LayoutCenteredToggle(Checkbox checkbox, int y)
    {
        const int boxSize = 25;
        const int labelGap = 10;
        var labelWidth = (int)MathF.Ceiling(_font.MeasureString(checkbox.Label).X);
        var rowWidth = Math.Max(200, Math.Max(boxSize + labelGap + labelWidth, 280 - ControlShrinkPixels));
        var x = _contentArea.Center.X - rowWidth / 2;
        checkbox.Bounds = new Rectangle(x, y, rowWidth, boxSize);
    }

    private void ToggleDifficultyDropdown()
    {
        _difficultyDropdownOpen = !_difficultyDropdownOpen;
        if (_difficultyDropdownOpen)
            _moreWorldOptionsOpen = false;
    }

    private bool HandleMoreWorldOptionsClick(Point mousePos)
    {
        // Handle clicks within the more world options panel
        // This will be implemented when we add the panel
        return false;
    }

    private bool HandleDropdownClick(Point mousePos)
    {
        if (_difficultyDropdownOpen)
        {
            for (var i = 0; i < _difficultyOptionRects.Count; i++)
            {
                if (!_difficultyOptionRects[i].Contains(mousePos))
                    continue;

                _difficulty = i;
                SyncDifficultyLabel();
                _difficultyDropdownOpen = false;
                return true;
            }
        }

        var isDropdownOpen = _difficultyDropdownOpen;
        if (!isDropdownOpen)
            return false;

        var insideDifficulty = _difficultyBtn.Bounds.Contains(mousePos);
        var insideHomes = _enableHomesBtn.Bounds.Contains(mousePos);
        var insideCheats = _enableCheatsBtn.Bounds.Contains(mousePos);
        var insideMoreOptions = _moreWorldOptionsBtn.Bounds.Contains(mousePos);
        
        if (insideDifficulty || insideHomes || insideCheats || insideMoreOptions)
            return false;

        _difficultyDropdownOpen = false;
        return true;
    }

    private void RebuildDropdownLayouts()
    {
        _difficultyOptionRects.Clear();
        const int optionHeight = 26;
        const int optionGap = 2;
        for (var i = 0; i < 4; i++)
        {
            _difficultyOptionRects.Add(new Rectangle(
                _difficultyBtn.Bounds.X,
                _difficultyBtn.Bounds.Bottom + 4 + i * (optionHeight + optionGap),
                _difficultyBtn.Bounds.Width,
                optionHeight));
        }
    }

    private void SyncDifficultyLabel()
    {
        _difficulty = Math.Clamp(_difficulty, 0, 3);
        _difficultyBtn.Label = GetDifficultyLabel().ToUpperInvariant();
    }

    private string ResolveHoveredModeDescription(Point mousePos)
    {
        var mode = _selectedGameMode;
        if (_artificerBtn.Bounds.Contains(mousePos))
            mode = Core.GameMode.Artificer;
        else if (_veilwalkerBtn.Bounds.Contains(mousePos))
            mode = Core.GameMode.Veilwalker;
        else if (_veilseerBtn.Bounds.Contains(mousePos))
            mode = Core.GameMode.Veilseer;

        return GetModeDescription(mode);
    }

    private static string GetModeDescription(Core.GameMode mode)
    {
        return mode switch
        {
            Core.GameMode.Artificer => "ARTIFICER: BUILD FREELY, FLY, AND SHAPE THE WORLD.",
            Core.GameMode.Veilwalker => "VEILWALKER: SURVIVAL RULES WITH RESOURCE PRESSURE.",
            Core.GameMode.Veilseer => "VEILSEER: SPECTATE, SCOUT, AND PLAN ROUTES.",
            _ => string.Empty
        };
    }

    private void DrawModeDescriptionBox(SpriteBatch sb)
    {
        var text = string.IsNullOrWhiteSpace(_modeDescriptionText)
            ? GetModeDescription(_selectedGameMode)
            : _modeDescriptionText;
        var descRect = new Rectangle(
            _contentArea.X + 12,
            _artificerBtn.Bounds.Bottom + 8,
            _contentArea.Width - 24,
            28);

        sb.Draw(_pixel, descRect, new Color(14, 14, 14, 220));
        DrawBorder(sb, descRect, new Color(110, 110, 110));
        _font.DrawString(sb, text, new Vector2(descRect.X + 8, descRect.Y + 6), new Color(220, 220, 220));
    }

    private void DrawDifficultyDropdown(SpriteBatch sb)
    {
        for (var i = 0; i < _difficultyOptionRects.Count; i++)
        {
            var rect = _difficultyOptionRects[i];
            var selected = i == _difficulty;
            sb.Draw(_pixel, rect, selected ? new Color(52, 82, 122, 230) : new Color(20, 20, 20, 230));
            DrawBorder(sb, rect, selected ? new Color(150, 220, 255) : new Color(120, 120, 120));
            _font.DrawString(sb, GetDifficultyLabel(i).ToUpperInvariant(), new Vector2(rect.X + 8, rect.Y + 6), Color.White);
        }
    }

    private void DrawDifficultyInfoBox(SpriteBatch sb)
    {
        var rect = new Rectangle(
            _difficultyBtn.Bounds.X,
            _difficultyBtn.Bounds.Bottom + 10,
            _difficultyBtn.Bounds.Width,
            56);
        sb.Draw(_pixel, rect, new Color(14, 14, 14, 220));
        DrawBorder(sb, rect, new Color(110, 110, 110));
        var wrapped = WrapText(GetDifficultyDescription().ToUpperInvariant(), rect.Width - 16);
        var y = rect.Y + 6;
        for (var i = 0; i < wrapped.Count; i++)
        {
            if (y + _font.LineHeight > rect.Bottom - 4)
                break;
            _font.DrawString(sb, wrapped[i], new Vector2(rect.X + 8, y), new Color(220, 220, 220));
            y += _font.LineHeight + 2;
        }
    }

    private void DrawSectionHeader(SpriteBatch sb, string text, Vector2 position)
    {
        _font.DrawString(sb, text, position, new Color(235, 235, 235));
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

        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = string.Empty;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
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

        return lines;
    }

    private (int width, int height, int depth) GetWorldDimensions()
    {
        return (4096, 256, 4096);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private static Vector2 GetSpawnPoint(WorldMeta meta)
    {
        var spawnX = meta.Size.Width * 0.25f;
        var spawnZ = meta.Size.Depth * 0.25f;
        spawnX = Math.Max(16f, Math.Min(spawnX, meta.Size.Width - 16f));
        spawnZ = Math.Max(16f, Math.Min(spawnZ, meta.Size.Depth - 16f));
        return new Vector2(spawnX, spawnZ);
    }

    private readonly struct WorldCreateResult
    {
        private WorldCreateResult(bool success, bool paused, string worldName, string worldPath, WorldMeta? meta, string? errorMessage)
        {
            IsSuccess = success;
            IsPaused = paused;
            WorldName = worldName;
            WorldPath = worldPath;
            Meta = meta;
            ErrorMessage = errorMessage;
        }

        public bool IsSuccess { get; }
        public bool IsPaused { get; }
        public string WorldName { get; }
        public string WorldPath { get; }
        public WorldMeta? Meta { get; }
        public string? ErrorMessage { get; }

        public static WorldCreateResult Success(string worldName, string worldPath, WorldMeta meta)
            => new(true, false, worldName, worldPath, meta, null);
        public static WorldCreateResult Paused()
            => new(false, true, string.Empty, string.Empty, null, null);
        public static WorldCreateResult Failure(string errorMessage)
            => new(false, false, string.Empty, string.Empty, null, errorMessage);
    }
}
