using System;
using System.Collections.Generic;
using System.IO;
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
    private const int PanelMaxWidth = 1300;
    private const int PanelMaxHeight = 700;
    private const int ControlShrinkPixels = 60; // ~2 inches at 30 px/in
    private const int ContentDownShiftPixels = 30; // ~1 inch at 30 px/in
    private const int SpawnChunkPregenerationRadius = 8;
    private const int SpawnMeshPregenerationRadius = 4;
    private const int SpawnVerticalPregenerationChunkTop = 5;

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
    private readonly Checkbox _generateStructures;
    private readonly Checkbox _generateCaves;
    private readonly Checkbox _generateOres;
    private readonly Checkbox _multipleHomes;
    private readonly Button _homeSlotsBtn;

    // World settings
    private string _worldName = "New World";
    private Core.GameMode _selectedGameMode = Core.GameMode.Artificer;
    private int _difficulty = 1; // 0=Peaceful, 1=Easy, 2=Normal, 3=Hard
    private bool _structuresEnabled = true;
    private bool _cavesEnabled = true;
    private bool _oresEnabled = true;
    private bool _multipleHomesEnabled = true;
    private int _maxHomesPerPlayer = 8;
    private bool _worldNameActive = true;
    private string _statusMessage = string.Empty;
    private double _statusUntil;
    private double _now;
    private bool _isGeneratingWorld;
    private float _generationProgress;
    private string _generationStage = string.Empty;
    private Task<WorldCreateResult>? _createTask;
    private bool _meshPrebakeInProgress;
    private string _pendingWorldName = string.Empty;
    private string _pendingWorldPath = string.Empty;
    private WorldMeta? _pendingWorldMeta;
    private List<ChunkCoord> _pendingMeshCoords = new();
    private int _pendingMeshIndex;
    private int _cachedMeshCount;
    private VoxelWorld? _meshPrebakeWorld;
    private CubeNetAtlas? _meshPrebakeAtlas;

    public CreateWorldScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, Action<string> onWorldCreated)
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
        _generateStructures = new Checkbox("Generate Structures", _structuresEnabled);
        _generateCaves = new Checkbox("Generate Caves", _cavesEnabled);
        _generateOres = new Checkbox("Generate Ores", _oresEnabled);
        _multipleHomes = new Checkbox("Enable Multiple Homes", _multipleHomesEnabled);
        _homeSlotsBtn = new Button(string.Empty, CycleMaxHomesPerPlayer);
        SyncHomeSlotsLabel();

        LoadAssets();
        RefreshGameModeButtonTextures();
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

        var checkboxY = _artificerBtn.Bounds.Bottom + 36;
        LayoutCenteredToggle(_generateStructures, checkboxY);
        LayoutCenteredToggle(_generateCaves, checkboxY + 32);
        LayoutCenteredToggle(_generateOres, checkboxY + 64);
        LayoutCenteredToggle(_multipleHomes, checkboxY + 96);
        _homeSlotsBtn.Bounds = new Rectangle(
            _contentArea.Center.X - 110,
            checkboxY + 132,
            220,
            30);
        _homeSlotsBtn.Enabled = _multipleHomesEnabled;
        _homeSlotsBtn.ForceDisabledStyle = !_multipleHomesEnabled;

        // Match Singleplayer world-list screen create button sizing/placement.
        var rowButtonW = Math.Clamp((int)(_panelRect.Width * 0.28f), 160, 260);
        var rowButtonH = Math.Clamp((int)(rowButtonW * 0.28f), 40, 70);
        var buttonY = _panelRect.Bottom - 24 - rowButtonH - 90;
        var createBtnW = panelW / 3 - 15;
        var createBtnH = (int)(createBtnW * 0.25f);
        var createBtnX = _panelRect.X + (_panelRect.Width - createBtnW) / 2;
        _createBtn.Bounds = new Rectangle(createBtnX, buttonY, createBtnW, createBtnH);

        // Match Options screen back button position (bottom-left of full screen).
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_cancelBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_cancelBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _cancelBtn.Bounds = new Rectangle(
            viewport.X + backBtnMargin,
            viewport.Bottom - backBtnMargin - backBtnH,
            backBtnW,
            backBtnH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusUntil > 0 && _now >= _statusUntil)
            _statusMessage = string.Empty;

        if (_isGeneratingWorld)
        {
            if (_createTask != null && _createTask.IsCompleted)
                CompleteCreateTask();
            if (_meshPrebakeInProgress)
                ProcessMeshPrebakeStep();
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

        _artificerBtn.Update(input);
        _veilwalkerBtn.Update(input);
        _veilseerBtn.Update(input);

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

        _generateStructures.Update(input);
        _structuresEnabled = _generateStructures.Value;
        
        _generateCaves.Update(input);
        _cavesEnabled = _generateCaves.Value;
        
        _generateOres.Update(input);
        _oresEnabled = _generateOres.Value;

        _multipleHomes.Update(input);
        _multipleHomesEnabled = _multipleHomes.Value;
        _homeSlotsBtn.Enabled = _multipleHomesEnabled;
        _homeSlotsBtn.ForceDisabledStyle = !_multipleHomesEnabled;
        _homeSlotsBtn.Update(input);

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
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16 + borderSize);
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

        var detailsY = _artificerBtn.Bounds.Bottom + 10;
        var detailColumn = Math.Max(200, _contentArea.Width / 4);
        _font.DrawString(sb, $"Difficulty: {GetDifficultyLabel()}", new Vector2(x + detailColumn, detailsY), Color.White);
        _font.DrawString(sb, $"Selected: {_selectedGameMode.ToString().ToUpperInvariant()}", new Vector2(x + detailColumn * 2, detailsY), Color.White);

        // Draw checkboxes
        _generateStructures.Draw(sb, _pixel, _font);
        _generateCaves.Draw(sb, _pixel, _font);
        _generateOres.Draw(sb, _pixel, _font);
        _multipleHomes.Draw(sb, _pixel, _font);
        _homeSlotsBtn.Draw(sb, _pixel, _font);

        // Draw buttons
        _createBtn.Draw(sb, _pixel, _font);
        _cancelBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var statusSize = _font.MeasureString(_statusMessage);
            var statusPos = new Vector2(_panelRect.Center.X - statusSize.X / 2f, _createBtn.Bounds.Top - _font.LineHeight - 8);
            _font.DrawString(sb, _statusMessage, statusPos, new Color(230, 210, 90));
        }

        if (_isGeneratingWorld)
        {
            DrawGeneratingOverlay(sb);
        }

        sb.End();
    }

    private string GetDifficultyLabel()
    {
        return _difficulty switch
        {
            0 => "Peaceful",
            1 => "Easy",
            2 => "Normal", 
            3 => "Hard",
            _ => "Normal"
        };
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
            _log.Warn($"World directory already exists: {worldPath}");
            SetStatus("WORLD NAME ALREADY EXISTS");
            return;
        }

        var (width, height, depth) = GetWorldDimensions();
        var seed = Environment.TickCount;
        var meta = WorldMeta.CreateFlat(worldName, _selectedGameMode, width, height, depth, seed);
        meta.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
        meta.PlayerCollision = true;
        meta.EnableMultipleHomes = _multipleHomesEnabled;
        meta.MaxHomesPerPlayer = _multipleHomesEnabled ? Math.Clamp(_maxHomesPerPlayer, 1, 32) : 1;

        _isGeneratingWorld = true;
        _generationProgress = 0f;
        _generationStage = "PREPARING";
        _statusMessage = string.Empty;
        _statusUntil = 0d;
        _createTask = Task.Run(() => CreateWorldTask(worldName, worldPath, meta));
    }

    private WorldCreateResult CreateWorldTask(string worldName, string worldPath, WorldMeta meta)
    {
        try
        {
            _log.Info($"Creating new world: {worldName}");
            _log.Info($"Settings: GameMode={_selectedGameMode}, Difficulty={_difficulty}");
            _log.Info($"Features: Structures={_structuresEnabled}, Caves={_cavesEnabled}, Ores={_oresEnabled}");

            SetGenerationProgress(0.08f, "METADATA");
            Directory.CreateDirectory(worldPath);
            var metaPath = Path.Combine(worldPath, "world.json");
            meta.Save(metaPath, _log);
            SetGenerationProgress(0.22f, "BIOME CATALOG");
            BiomeCatalog.BuildAndSave(meta, worldPath, _log);

            SetGenerationProgress(0.40f, "SPAWN CHUNKS");
            var meshCoords = PregenerateSpawnChunks(meta, worldPath, progress =>
            {
                var stageProgress = 0.40f + progress * 0.35f;
                SetGenerationProgress(stageProgress, "SPAWN CHUNKS");
            });

            SetGenerationProgress(0.78f, "MESH CACHE");
            _log.Info($"World generation base stages complete: {worldName} (mesh candidates: {meshCoords.Count})");
            return WorldCreateResult.Success(worldName, worldPath, meta, meshCoords);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to create world: {ex.Message}");
            _log.Error($"Stack trace: {ex.StackTrace}");
            return WorldCreateResult.Failure("FAILED TO CREATE WORLD");
        }
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetNewKeys())
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

    private void SetStatus(string message, double seconds = 2.5)
    {
        _statusMessage = message;
        _statusUntil = seconds <= 0 ? 0 : _now + seconds;
    }

    private void SetGenerationProgress(float progress, string stage)
    {
        _generationProgress = Math.Clamp(progress, 0f, 1f);
        _generationStage = stage ?? string.Empty;
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

        if (result.IsSuccess)
        {
            BeginMeshPrebake(result);
            return;
        }

        _isGeneratingWorld = false;
        SetStatus(result.ErrorMessage ?? "FAILED TO CREATE WORLD");
    }

    private void BeginMeshPrebake(WorldCreateResult result)
    {
        _pendingWorldName = result.WorldName;
        _pendingWorldPath = result.WorldPath;
        _pendingWorldMeta = result.Meta;
        _pendingMeshCoords = result.MeshCoords ?? new List<ChunkCoord>();
        _pendingMeshIndex = 0;
        _cachedMeshCount = 0;

        if (_pendingWorldMeta == null || string.IsNullOrWhiteSpace(_pendingWorldPath))
        {
            FailGeneration("FAILED TO PREPARE MESH CACHE");
            return;
        }

        try
        {
            _meshPrebakeWorld = new VoxelWorld(_pendingWorldMeta, _pendingWorldPath, _log);
            var settings = GameSettings.LoadOrCreate(_log);
            _meshPrebakeAtlas = CubeNetAtlas.Build(_assets, _log, settings.QualityPreset);
            _meshPrebakeInProgress = true;
            SetGenerationProgress(0.78f, "MESH CACHE");
        }
        catch (Exception ex)
        {
            _log.Warn($"Mesh cache bootstrap failed: {ex.Message}");
            _meshPrebakeInProgress = false;
            FinalizeWorldCreation();
        }
    }

    private void ProcessMeshPrebakeStep()
    {
        if (!_meshPrebakeInProgress || _meshPrebakeWorld == null || _meshPrebakeAtlas == null)
        {
            _meshPrebakeInProgress = false;
            FinalizeWorldCreation();
            return;
        }

        var total = Math.Max(1, _pendingMeshCoords.Count);
        const int chunksPerFrame = 2;
        var processed = 0;
        while (_pendingMeshIndex < _pendingMeshCoords.Count && processed < chunksPerFrame)
        {
            var coord = _pendingMeshCoords[_pendingMeshIndex];
            var chunk = _meshPrebakeWorld.GetOrCreateChunk(coord);
            var mesh = VoxelMesherGreedy.BuildChunkMeshFast(_meshPrebakeWorld, chunk, _meshPrebakeAtlas, _log);
            ChunkMeshCache.Save(_pendingWorldPath, mesh);
            _cachedMeshCount++;
            _pendingMeshIndex++;
            processed++;
        }

        var progress = 0.78f + ((_pendingMeshIndex / (float)total) * 0.12f);
        SetGenerationProgress(progress, "MESH CACHE");

        if (_pendingMeshIndex >= _pendingMeshCoords.Count)
        {
            _meshPrebakeInProgress = false;
            FinalizeWorldCreation();
        }
    }

    private void FinalizeWorldCreation()
    {
        try
        {
            SetGenerationProgress(0.90f, "WORLD PREVIEW");
            if (_pendingWorldMeta != null && !string.IsNullOrWhiteSpace(_pendingWorldPath))
                WorldPreviewGenerator.GenerateAndSave(_pendingWorldMeta, _pendingWorldPath, _log);

            SetGenerationProgress(1.0f, "FINALIZING");
            _log.Info($"World created successfully: {_pendingWorldName} (cachedMeshes={_cachedMeshCount}).");
            _isGeneratingWorld = false;
            _onWorldCreated?.Invoke(_pendingWorldName);
            _menus.Pop();
        }
        catch (Exception ex)
        {
            _log.Error($"Finalization failed: {ex.Message}");
            FailGeneration("FAILED TO FINALIZE WORLD");
        }
        finally
        {
            DisposeMeshPrebakeResources();
        }
    }

    private void DisposeMeshPrebakeResources()
    {
        if (_meshPrebakeAtlas != null)
        {
            _meshPrebakeAtlas.Texture.Dispose();
            _meshPrebakeAtlas = null;
        }

        _meshPrebakeWorld = null;
        _pendingMeshCoords.Clear();
        _pendingMeshIndex = 0;
        _cachedMeshCount = 0;
    }

    private void FailGeneration(string message)
    {
        _isGeneratingWorld = false;
        _meshPrebakeInProgress = false;
        DisposeMeshPrebakeResources();
        SetStatus(message);
    }

    private void DrawGeneratingOverlay(SpriteBatch sb)
    {
        var overlay = new Rectangle(_panelRect.X + 40, _panelRect.Center.Y - 72, _panelRect.Width - 80, 144);
        sb.Draw(_pixel, overlay, new Color(0, 0, 0, 200));
        DrawBorder(sb, overlay, new Color(190, 190, 190));

        var title = "GENERATING WORLD";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(overlay.Center.X - titleSize.X / 2f, overlay.Y + 18), Color.White);

        var stage = string.IsNullOrWhiteSpace(_generationStage) ? "PREPARING" : _generationStage.ToUpperInvariant();
        var stageText = $"STAGE: {stage}";
        var stageSize = _font.MeasureString(stageText);
        _font.DrawString(sb, stageText, new Vector2(overlay.Center.X - stageSize.X / 2f, overlay.Y + 18 + _font.LineHeight + 8), new Color(220, 220, 220));

        var barRect = new Rectangle(overlay.X + 24, overlay.Bottom - 34, overlay.Width - 48, 14);
        sb.Draw(_pixel, barRect, new Color(22, 22, 22, 220));
        DrawBorder(sb, barRect, new Color(170, 170, 170));
        var fill = new Rectangle(barRect.X + 2, barRect.Y + 2, Math.Max(0, (int)MathF.Round((barRect.Width - 4) * _generationProgress)), Math.Max(1, barRect.Height - 4));
        sb.Draw(_pixel, fill, new Color(84, 174, 255, 220));
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

    private void CycleMaxHomesPerPlayer()
    {
        if (!_multipleHomesEnabled)
            return;

        _maxHomesPerPlayer++;
        if (_maxHomesPerPlayer > 20)
            _maxHomesPerPlayer = 2;
        SyncHomeSlotsLabel();
    }

    private void SyncHomeSlotsLabel()
    {
        _homeSlotsBtn.Label = $"HOME SLOTS: {_maxHomesPerPlayer}";
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

    private List<ChunkCoord> PregenerateSpawnChunks(WorldMeta meta, string worldPath, Action<float>? onProgress = null)
    {
        try
        {
            var world = new VoxelWorld(meta, worldPath, _log);
            Directory.CreateDirectory(world.ChunksDir);

            var spawn = GetSpawnPoint(meta);
            var spawnChunk = VoxelWorld.WorldToChunk((int)spawn.X, 0, (int)spawn.Y, out _, out _, out _);
            var chunkRadius = Math.Max(0, SpawnChunkPregenerationRadius);
            var chunkRadiusSq = chunkRadius * chunkRadius;
            var maxCy = Math.Min(Math.Max(0, world.MaxChunkY), SpawnVerticalPregenerationChunkTop);
            var generatedCoords = new List<ChunkCoord>();
            var meshCoords = new List<ChunkCoord>();

            for (var dz = -chunkRadius; dz <= chunkRadius; dz++)
            {
                for (var dx = -chunkRadius; dx <= chunkRadius; dx++)
                {
                    var distSq = dx * dx + dz * dz;
                    if (distSq > chunkRadiusSq)
                        continue;

                    for (var cy = 0; cy <= maxCy; cy++)
                    {
                        var coord = new ChunkCoord(spawnChunk.X + dx, cy, spawnChunk.Z + dz);
                        world.GetOrCreateChunk(coord);
                        generatedCoords.Add(coord);

                        if (distSq <= SpawnMeshPregenerationRadius * SpawnMeshPregenerationRadius)
                            meshCoords.Add(coord);
                    }
                }
            }

            // Save all pregenerated chunks now (do not use batched SaveModifiedChunks limit).
            for (int i = 0; i < generatedCoords.Count; i++)
            {
                var coord = generatedCoords[i];
                world.SaveChunk(coord);
                onProgress?.Invoke((i + 1f) / Math.Max(1, generatedCoords.Count));
            }

            _log.Info($"Spawn pregeneration complete: chunks={generatedCoords.Count}, meshCandidates={meshCoords.Count}, center={spawnChunk}, maxCy={maxCy}.");
            return meshCoords;
        }
        catch (Exception ex)
        {
            _log.Warn($"Spawn pregeneration failed: {ex.Message}");
            return new List<ChunkCoord>();
        }
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
        private WorldCreateResult(bool success, string worldName, string worldPath, WorldMeta? meta, List<ChunkCoord>? meshCoords, string? errorMessage)
        {
            IsSuccess = success;
            WorldName = worldName;
            WorldPath = worldPath;
            Meta = meta;
            MeshCoords = meshCoords;
            ErrorMessage = errorMessage;
        }

        public bool IsSuccess { get; }
        public string WorldName { get; }
        public string WorldPath { get; }
        public WorldMeta? Meta { get; }
        public List<ChunkCoord>? MeshCoords { get; }
        public string? ErrorMessage { get; }

        public static WorldCreateResult Success(string worldName, string worldPath, WorldMeta meta, List<ChunkCoord> meshCoords)
            => new(true, worldName, worldPath, meta, meshCoords, null);
        public static WorldCreateResult Failure(string errorMessage)
            => new(false, string.Empty, string.Empty, null, null, errorMessage);
    }
}
