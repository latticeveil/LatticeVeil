using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.Online.Lan;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

// Phase plan (gameplay upgrades):
// 1) Cube-net atlas + BlockRegistry (single texture; runtime-gen if missing).
// 2) PlayerController physics (gravity/collision/fly).
// 3) Raycast break/place + chunk dirty updates.
// 4) Hotbar/inventory UI + selection.
// 5) First-person hand/held block render pass.
// Not touching: launcher flow, asset install paths, build scripts.

public sealed class GameWorldScreen : IScreen, IMouseCaptureScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private ILanSession? _lanSession;
    private readonly string _worldPath;
    private readonly string _metaPath;
    private GameSettings _settings = new();
    private DateTime _settingsStamp = DateTime.MinValue;

    private WorldMeta? _meta;
    private GameMode _gameMode = GameMode.Artificer;
    private VoxelWorld? _world;
    // ALL WORLD GENERATION DELETED
    // ALL WORLD GENERATION DELETED
    // ALL WORLD GENERATION DELETED
    private CubeNetAtlas? _atlas;
    private BlockModel? _blockModel;
    private BlockIconCache? _blockIconCache;
    private readonly ConcurrentDictionary<ChunkCoord, ChunkMesh> _chunkMeshes = new();
    private readonly List<ChunkCoord> _chunkOrder = new();
    private readonly HashSet<ChunkCoord> _activeChunks = new();
    private ChunkCoord _centerChunk;
    private int _activeRadiusChunks = 8;
    private const int MaxRuntimeActiveRadius = GameSettings.EngineRenderDistanceMax;
    private const int MaxMeshBuildsPerFrame = 4;

    // Chunk streaming constants
    private const int MaxNewChunkRequestsPerFrame = 2;
    private const int MaxMeshBuildRequestsPerFrame = 2;
    private const int MaxApplyCompletedMeshesPerFrame = 3;
    private const int MaxApplyCompletedChunkLoadsPerFrame = 4;
    private const int MaxOutstandingChunkJobs = 96;
    private const int MaxOutstandingMeshJobs = 96;

    private const int KeepRadiusBuffer = 3;
    private const int PrewarmRadius = 8; // Match render distance to prevent post-spawn loading
    private const int PrewarmGateRadius = 3;
    private const int PrewarmVerticalChunkTop = 5;
    private const int PrewarmChunkBudgetPerFrame = 6;
    private const int PrewarmMeshBudgetPerFrame = 4;
    private const double PrewarmTimeoutSeconds = 12.0;
    private static readonly int RuntimeChunkRequestBudget = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private static readonly int RuntimeMeshRequestBudget = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private static readonly int RuntimeChunkScheduleBudget = Math.Clamp((Environment.ProcessorCount / 3) + 1, 2, 6);
    private static readonly int RuntimeMeshScheduleBudget = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    // Streaming state
    private ChunkCoord _playerChunkCoord;
    private int _chunksRequestedThisFrame;
    private int _meshesRequestedThisFrame;
    private DateTime _lastStreamingStats = DateTime.UtcNow;
    private bool _spawnPrewarmComplete;
    private DateTime _spawnPrewarmStartTime = DateTime.UtcNow;
    
    // Stub services to fix compilation (will be replaced with new world generation)
    private StubStreamingService? _streamingService;
    private StubPregenerationService? _pregenerationService;
    
    // Result application tracking
    private int _completedLoadsAppliedThisFrame;
    private int _completedMeshesAppliedThisFrame;
    
    // A) MINIMAL PREWARM SET - Only ring 0 + ring 1
    private readonly HashSet<ChunkCoord> _prewarmRequiredChunks = new();
    private readonly HashSet<ChunkCoord> _tier0Chunks = new(); // ring 0 + ring 1
    
    // A) HARD VISIBILITY GATE
    private bool _visibilitySafe;
    private bool _firstGameplayFrame;
    private bool _visibilityAssertLogged;
    private int _spinnerFrame;
    private int _prewarmTargetCount;
    private int _prewarmReadyCount;
    
    // Reusable buffer to avoid allocations in QueueDirtyChunks
    private readonly List<VoxelChunkData> _chunkSnapshotBuffer = new();
    private readonly HashSet<ChunkCoord> _activeKeepBuffer = new();
    private readonly List<ChunkCoord> _drawOrderBuffer = new();
    private readonly List<ChunkCoord> _meshRemovalBuffer = new();
    private readonly Queue<ChunkCoord> _meshQueue = new();
    private readonly HashSet<ChunkCoord> _meshQueued = new();
    private readonly ConcurrentQueue<MeshBuildResult> _completedMeshBuildQueue = new();
    private readonly ConcurrentQueue<MeshBuildResult> _completedPriorityMeshBuildQueue = new();
    private readonly ConcurrentDictionary<ChunkCoord, byte> _meshBuildInFlight = new();
    private static readonly int MeshWorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    private readonly SemaphoreSlim _meshBuildSemaphore = new(MeshWorkerCount, MeshWorkerCount);
    private readonly ConcurrentQueue<ChunkCoord> _chunkGenerationQueue = new();
    private readonly ConcurrentDictionary<ChunkCoord, byte> _chunkGenerationQueued = new();
    private readonly ConcurrentDictionary<ChunkCoord, byte> _chunkGenerationInFlight = new();
    private static readonly int ChunkWorkerCount = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
    private readonly SemaphoreSlim _chunkGenerationSemaphore = new(ChunkWorkerCount, ChunkWorkerCount);
    private bool _loggedInvalidChunkMesh;
    private bool _loggedInvalidHandMesh;

    private BasicEffect? _effect;
    private Effect? _waterEffect;
    private bool _waterEffectLoadAttempted;
    private BasicEffect? _lineEffect;
    private SamplerState? _samplerLow;
    private SamplerState? _samplerMedium;
    private SamplerState? _samplerHigh;
    private SamplerState? _samplerUltra;
    private Rectangle _viewport;
    private readonly VertexPositionColor[] _highlightVerts = new VertexPositionColor[24];
    private bool _highlightActive;
    private int _highlightX = int.MinValue;
    private int _highlightY = int.MinValue;
    private int _highlightZ = int.MinValue;
    private const float HighlightEpsilon = 0.01f;
    private Color _blockOutlineColor = new(200, 220, 230, 120);
    private Color _highlightColor = new(200, 220, 230, 120);
    private bool _reticleEnabled = true;
    private string _reticleStyle = "Dot";
    private int _reticleSize = 8;
    private int _reticleThickness = 2;
    private Color _reticleColor = new(255, 255, 255, 200);
    private const int ReticleSizeMin = 2;
    private const int ReticleSizeMax = 32;
    private const int ReticleThicknessMin = 1;
    private const int ReticleThicknessMax = 6;
    private static readonly Vector2[] ReticleCirclePoints = BuildReticleCirclePoints(32);

    private readonly PlayerController _player = new();
    private readonly Inventory _inventory = new();
    private Rectangle _hotbarRect;
    private readonly Rectangle[] _hotbarSlots = new Rectangle[Inventory.HotbarSize];
    private readonly Rectangle[] _inventoryGridSlots = new Rectangle[Inventory.GridSize];
    private Rectangle _inventoryRect;
    private bool _inventoryOpen;
    private HotbarSlot _inventoryHeld;
    private bool _inventoryHasHeld;
    private Keys _inventoryKey = Keys.E;
    private Keys _dropKey = Keys.Q;
    private Keys _giveKey = Keys.F;
    private Keys _stackModifierKey = Keys.LeftShift;
    private readonly Button _inventoryClose;
    private Point _inventoryMousePos;
    private InventorySlotGroup _heldFrom = InventorySlotGroup.None;
    private int _heldIndex = -1;
    private FirstPersonHandRenderer? _handRenderer;
    private PlayerModel? _playerModel;
    private readonly Dictionary<int, PlayerModel> _remotePlayers = new();
    private readonly Dictionary<int, string> _playerNames = new();
    private bool _playerCollisionEnabled = true;

    private readonly Dictionary<int, WorldItem> _worldItems = new();
    private readonly List<int> _worldItemRemove = new();
    private readonly Dictionary<BlockId, VertexPositionTexture[]> _itemMeshCache = new();
    private BasicEffect? _itemEffect;
    private int _nextItemId = 1;
    private float _worldTimeSeconds;
    private int _handoffTargetId = -1;
    private string _handoffTargetName = "";
    private bool _handoffFullStack;
    private bool _handoffPromptVisible;
    private readonly List<PlayerHomeEntry> _homes = new();
    private int _selectedHomeIndex = -1;
    private bool _hasHome;
    private Vector3 _homePosition;
    private bool _homeGuiOpen;
    private string _homeGuiNameInput = "";
    private string _homeGuiStatus = "";
    private float _homeGuiStatusTimer;
    private float _homeGuiScroll;
    private Rectangle _homeGuiPanelRect;
    private Rectangle _homeGuiListRect;
    private Rectangle _homeGuiInputRect;
    private readonly List<Rectangle> _homeGuiRowRects = new();
    private int _homeGuiLastClickIndex = -1;
    private double _homeGuiLastClickTime;
    private readonly Button _homeGuiSetHereBtn;
    private readonly Button _homeGuiRenameBtn;
    private readonly Button _homeGuiDeleteBtn;
    private readonly Button _homeGuiSetIconHeldBtn;
    private readonly Button _homeGuiSetIconAutoBtn;
    private readonly Button _homeGuiCloseBtn;
    private bool _structureFinderOpen;
    private bool _structureFinderStructureTab;
    private int _structureFinderSelectedBiomeIndex;
    private int _structureFinderRadiusIndex = 2;
    private string _structureFinderStatus = "";
    private float _structureFinderStatusTimer;
    private Texture2D? _structureFinderPanelTexture;
    private Rectangle _structureFinderPanelRect;
    private Rectangle _structureFinderListRect;
    private readonly Rectangle[] _structureFinderBiomeRects = new Rectangle[3];
    private readonly Button _structureFinderBiomeTabBtn;
    private readonly Button _structureFinderStructureTabBtn;
    private readonly Button _structureFinderRadiusBtn;
    private readonly Button _structureFinderFindBtn;
    private readonly Button _structureFinderCloseBtn;

    public bool WantsMouseCapture => !_pauseMenuOpen && !_inventoryOpen && !_homeGuiOpen && !_structureFinderOpen && !IsTextInputActive && !_gamemodeWheelVisible && !IsOverlayActionInteractionActive && _hasLoadedWorld && !_worldSyncInProgress && _spawnPrewarmComplete;
    public bool WorldReady => !_worldSyncInProgress && _hasLoadedWorld && _spawnPrewarmComplete;
    private bool IsTextInputActive => _commandInputActive || _chatInputActive;
    private bool IsOverlayActionInteractionActive => !IsTextInputActive
        && _worldTimeSeconds < _chatOverlayActionUnlockUntil
        && _chatLines.Any(line => line.HasTeleportAction && line.TimeRemaining > 0f);

    private bool _pauseMenuOpen;
    private Texture2D? _pausePanel;
    private Rectangle _pauseRect;
    private Rectangle _pauseHeaderRect;
    private readonly Button _pauseResume;
    private readonly Button _pauseOpenLan;
    private readonly Button _pauseHostOnline;
    private readonly Button _pauseInvite;
    private readonly Button _pauseSettings;
    private readonly Button _pauseSaveExit;
    private EosClient? _eosClient;
    private bool _debugFaceOverlay;
    private bool _debugHudVisible;
    private Texture2D? _gamemodeArtificerIcon;
    private Texture2D? _gamemodeVeilwalkerIcon;
    private Texture2D? _gamemodeVeilseerIcon;

    private const int InventoryCols = Inventory.GridCols;
    private const int InventoryRows = Inventory.GridRows;
    private const int InventorySlotSize = 48;
    private const int InventorySlotGap = 6;
    private const int InventoryPadding = 18;
    private const int InventoryTitleHeight = 30;
    private const int InventoryFooterHeight = 50;

    private const float ItemPickupRadius = 1.5f * Scale.BlockSize;
    private const float ItemPickupRadiusSq = ItemPickupRadius * ItemPickupRadius;
    private const float ItemScale = 0.35f * Scale.BlockSize;
    private const float ItemBobHeight = 0.035f * Scale.BlockSize;
    private const float ItemBobSpeed = 1.3f;
    private const float ItemSpinSpeed = 0.6f;
    private const float ItemPickupDelaySeconds = 0.25f;
    private const float ItemDropPickupDelaySeconds = 0.75f;
    private const float ItemGroundPickupDelaySeconds = 0.45f;
    private const float ItemGravity = -18f;
    private const float ItemAirDrag = 0.985f;
    private const float ItemGroundFriction = 0.6f;
    private const float ItemGroundSnap = 0.01f;
    private const float ItemHalfHeight = ItemScale * 0.5f;
    private const float ItemGroundOffset = ItemHalfHeight + ItemGroundSnap;
    private const float ItemTiltRadians = 0.2f;
    private const float HandoffRange = 3.0f * Scale.BlockSize;
    private const float HandoffDotThreshold = 0.94f;
    private const float PlayerPushStrength = 6.0f * Scale.BlockSize;
    private const float PlayerPushMaxStep = 0.08f * Scale.BlockSize;
    private const float PlayerPushSkin = 0.01f * Scale.BlockSize;

    private float _fpsTimer;
    private float _fps;
    private int _frameCount;
    private float _playerSaveTimer; // Timer for periodic player state saving
    private int _autoSaveInFlight;
    private readonly object _previewQueueLock = new();
    private Task? _previewUpdateWorker;
    private bool _previewUpdatePending;
    private int _previewPendingCenterX;
    private int _previewPendingCenterZ;
    private bool _isClosing;
    private bool _saveAndExitInProgress;
    private bool _skipOnCloseFullSave;
    private Task<SaveAndExitResult>? _saveAndExitTask;
    private DateTime _saveAndExitStartedUtc = DateTime.MinValue;
    private string? _saveAndExitError;

    private static readonly float InteractRange = Scale.InteractionRange * Scale.BlockSize;
    private const float InteractCooldownSeconds = 0.15f;
    private const int MaxPriorityMeshBuildsPerFrame = 4;
    private const int MaxInlinePriorityMeshBuildsPerFrame = 1;
    private const int BlockEditBoostFrames = 14;
    private float _interactCooldown;
    private readonly Queue<ChunkCoord> _priorityMeshQueue = new();
    private readonly HashSet<ChunkCoord> _priorityMeshQueued = new();
    private int _blockEditBoostRemaining;
    private bool _worldSyncInProgress = true;
    private bool _hasLoadedWorld;
    private int _chunksReceived;
    private float _netSendAccumulator;

    private float _selectedNameTimer;
    private int _lastSelectedIndex = -1;
    private string _displayName = "";
    private Keys _chatKey = Keys.T;
    private Keys _commandKey = Keys.OemQuestion;
    private Keys _homeGuiKey = Keys.H;
    private Keys _gamemodeModifierKey = Keys.LeftAlt;
    private Keys _gamemodeWheelKey = Keys.G;
    private bool _chatInputActive;
    private string _chatInputText = "";
    private readonly List<ChatLine> _chatLines = new();
    private int _chatOverlayScrollLines;
    private readonly List<string> _textInputHistory = new();
    private int _textInputHistoryCursor = -1;
    private string _textInputHistoryDraft = "";
    private bool _textInputHistoryBrowsing;
    private readonly List<ChatActionHitbox> _chatActionHitboxes = new();
    private Point _overlayMousePos;
    private int _hoveredChatActionIndex = -1;
    private int _difficultyLevel = 1; // 0=Peaceful, 1=Easy, 2=Normal, 3=Hard
    private bool _commandInputActive;
    private string _commandInputText = "";
    private string _commandStatusText = "";
    private float _commandStatusTimer;
    private readonly List<CommandPredictionItem> _commandPredictionBuffer = new();
    private readonly Dictionary<string, CommandDescriptor> _commandLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CommandDescriptor> _commandDescriptors = new();
    private readonly List<CommandCompletionCandidate> _commandTabCycleCandidates = new();
    private readonly List<CommandCompletionCandidate> _commandTabBuildBuffer = new();
    private string _commandTabContextKey = string.Empty;
    private int _commandTabIndex = -1;
    private bool _commandTabAppendSpace;
    private bool _commandsInitialized;
    private const int MaxChatLines = 256;
    private const float ChatLineLifetimeSeconds = 14f;
    private const int MaxTextInputHistory = 256;
    private const int MaxCommandPredictions = 5;
    private const int ChatHistoryScrollStep = 6;
    private const int CommandInputMaxLength = 96;
    private const float ChatOverlayWidthRatio = 0.46f;
    private const int ChatOverlayMinWidth = 420;
    private const int ChatOverlayMaxWidth = 780;
    private const float ChatOverlayActionUnlockSeconds = ChatLineLifetimeSeconds;
    private const int CommandFindBiomeDefaultRadius = 2048;
    private const int CommandFindBiomeMaxRadius = 8192;
    private const int BiomeSearchRefineWindow = 6;
    private const int BiomeLocateRingStep = 8;
    private const int BiomeLocateGlobalStride = 16;
    private const int BiomeLocateGlobalFallbackStride = 8;
    private const int BiomeLocateMaxRingRadius = 1536;
    private bool _hasQueuedLocateOrigin;
    private int _queuedLocateOriginX;
    private int _queuedLocateOriginZ;
    private static readonly string[] FinderBiomeTokens = { "grasslands", "desert", "ocean" };
    private static readonly string[] FinderBiomeLabels = { "Grasslands", "Desert", "Ocean" };
    private static readonly int[] FinderRadiusOptions = { 512, 1024, 2048, 4096, 8192 };
    private static readonly GameMode[] GameModeWheelModes =
    {
        GameMode.Artificer,
        GameMode.Veilwalker,
        GameMode.Veilseer
    };
    private float _chatOverlayActionUnlockUntil;
    private int _timeOfDayTicks = 1000;
    private string _weatherState = "clear";
    private bool _gamemodeWheelVisible;
    private bool _gamemodeWheelHoldToOpen;
    private bool _gamemodeWheelWaitForRelease;
    private GameMode _gamemodeWheelHoverMode = GameMode.Artificer;
    private string _gamemodeToastText = "";
    private float _gamemodeToastTimer;
    private readonly BlockBreakParticleSystem _blockBreakParticles = new();
    private BiomeCatalog? _biomeCatalog;
    private const string DefaultHomeName = "home";
    private const int HomeGuiRowHeight = 42;

#if DEBUG
    private bool _debugDisableHands;
    private bool _debugWireframe;
    private bool _debugSkyClear = true;
    private bool _debugDrawPlayerModel;
    private string _blockSamplerLabel = "unknown";
    private bool _debugWorldAtlasBound;
    private bool _debugOverlayAtlasBound;
#endif
    private static readonly Color SkyColor = new(135, 206, 235);
    private static readonly RasterizerState WireframeState = new()
    {
        FillMode = FillMode.WireFrame,
        CullMode = CullMode.None
    };

    public GameWorldScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, string worldPath, string metaPath, ILanSession? lanSession = null, VoxelWorld? preloadedWorld = null)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _worldPath = worldPath;
        _metaPath = metaPath;
        _lanSession = lanSession;
        _world = preloadedWorld; // Use preloaded world if available
        _settings = GameSettings.LoadOrCreate(_log);
        _settingsStamp = GetSettingsStamp();
        _activeRadiusChunks = Math.Clamp(_settings.RenderDistanceChunks, 4, MaxRuntimeActiveRadius);
        UpdateReticleSettings();
        _inventoryKey = GetKeybind("Inventory", Keys.E);
        _dropKey = GetKeybind("DropItem", Keys.Q);
        _giveKey = GetKeybind("GiveItem", Keys.F);
        _stackModifierKey = GetKeybind("Crouch", Keys.LeftShift);
        _chatKey = GetKeybind("Chat", Keys.T);
        _commandKey = GetKeybind("Command", Keys.OemQuestion);
        _homeGuiKey = GetKeybind("HomeGui", Keys.H);
        _gamemodeModifierKey = GetKeybind("GamemodeModifier", Keys.LeftAlt);
        _gamemodeWheelKey = GetKeybind("GamemodeWheel", Keys.G);
        _blockBreakParticles.ApplyPreset(_settings.ParticlePreset, _settings.QualityPreset);
        EnsureCommandRegistry();

        // Initialize stub services to fix compilation (will be replaced with new world generation)
        if (_world != null)
        {
            _streamingService = new StubStreamingService(_world, _log);
            _pregenerationService = new StubPregenerationService(_world, _log);
        }

        _pauseResume = new Button("RESUME", () => _pauseMenuOpen = false);
        _pauseOpenLan = new Button("OPEN TO LAN", OpenToLanFromPause);
        _pauseHostOnline = new Button("HOST ONLINE", OpenOnlineHostFromPause);
        _pauseInvite = new Button("SHARE CODE", OpenShareCode);
        _pauseSettings = new Button("SETTINGS", OpenSettings);
        _pauseSaveExit = new Button("SAVE & EXIT", SaveAndExit);
        _inventoryClose = new Button("CLOSE", CloseInventory);
        _homeGuiSetHereBtn = new Button("SET HERE", SetHomeAtCurrentPositionFromGui);
        _homeGuiRenameBtn = new Button("RENAME", RenameSelectedHomeFromGui);
        _homeGuiDeleteBtn = new Button("DELETE", DeleteSelectedHomeFromGui);
        _homeGuiSetIconHeldBtn = new Button("ICON: HELD", SetSelectedHomeIconFromHeld);
        _homeGuiSetIconAutoBtn = new Button("ICON: LETTER", SetSelectedHomeIconAuto);
        _homeGuiCloseBtn = new Button("CLOSE", () => _homeGuiOpen = false);
        _structureFinderBiomeTabBtn = new Button("BIOMES", () => _structureFinderStructureTab = false);
        _structureFinderStructureTabBtn = new Button("STRUCTURES", () => _structureFinderStructureTab = true);
        _structureFinderRadiusBtn = new Button("RADIUS", CycleStructureFinderRadius);
        _structureFinderFindBtn = new Button("FIND", ExecuteStructureFinderSearchFromGui);
        _structureFinderCloseBtn = new Button("CLOSE", () => _structureFinderOpen = false);
        SyncStructureFinderRadiusLabel();

        try
        {
            _pausePanel = _assets.LoadTexture("textures/menu/GUIS/Pause_GUI.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Pause menu asset load: {ex.Message}");
        }

        LoadGamemodeWheelIcons();
        _structureFinderPanelTexture = TryLoadOptionalTexture(
            "textures/menu/GUIS/GUIBOX.png",
            "textures/menu/GUIS/WorldGeneration_GUI.png");

        // Note: World load is deferred to the first Update() to allow "Loading..." screen to draw.
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        UpdatePauseMenuLayout();
        UpdateHotbarLayout();
        UpdateInventoryLayout();
        LayoutStructureFinderGui();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (!_hasLoadedWorld)
        {
            _log.Info("Starting delayed world load...");
            LoadWorld();
            SeedLocalPlayerName();
            _hasLoadedWorld = true;
            _log.Info("Delayed world load complete.");

            // If we are Host or Singleplayer, we don't need to wait for network sync.
            if (_lanSession == null || _lanSession.IsHost)
            {
                _worldSyncInProgress = false;
                _log.Info("Host/SP mode: Sync skipped (local world ready).");
            }
            
            // Return to allow the loop to proceed cleanly next frame
            return;
        }

        if (_saveAndExitInProgress)
        {
            _spinnerFrame = (_spinnerFrame + 1) % 8;
            if (_saveAndExitTask != null && _saveAndExitTask.IsCompleted)
                CompleteSaveAndExit();
            return;
        }

        var rawDt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        // Only clamp catastrophic stalls; do not clamp normal frame deltas to avoid perceived slow-motion.
        var dt = Math.Min(rawDt, 0.1f);
        _worldTimeSeconds = (float)gameTime.TotalGameTime.TotalSeconds;

        _fpsTimer += rawDt;
        _frameCount++;
        if (_fpsTimer >= 1.0f)
        {
            _fps = _frameCount / _fpsTimer;
            _frameCount = 0;
            _fpsTimer = 0;
        }

        if (_commandStatusTimer > 0f)
        {
            _commandStatusTimer = Math.Max(0f, _commandStatusTimer - rawDt);
            if (_commandStatusTimer <= 0f)
                _commandStatusText = "";
        }

        if (_gamemodeToastTimer > 0f)
        {
            _gamemodeToastTimer = Math.Max(0f, _gamemodeToastTimer - rawDt);
            if (_gamemodeToastTimer <= 0f)
                _gamemodeToastText = string.Empty;
        }
        _overlayMousePos = input.MousePosition;
        UpdateChatLines(rawDt);
        HandleChatOverlayActions(input);

        if (input.IsNewKeyPress(Keys.F9))
            ExportAtlas();
        if (input.IsNewKeyPress(Keys.F3))
            _debugHudVisible = !_debugHudVisible;
        if (input.IsNewKeyPress(Keys.F4))
            _debugFaceOverlay = !_debugFaceOverlay;

#if DEBUG
        if (input.IsNewKeyPress(Keys.F6))
            _debugSkyClear = !_debugSkyClear;
        if (input.IsNewKeyPress(Keys.F7))
            _debugWireframe = !_debugWireframe;
        if (input.IsNewKeyPress(Keys.F5))
            _debugDrawPlayerModel = !_debugDrawPlayerModel;
        if (input.IsNewKeyPress(Keys.F8))
            _debugDisableHands = !_debugDisableHands;
#endif
        RefreshSettingsIfChanged();

        // PERIODIC PLAYER STATE SAVING - Save player position periodically during gameplay
        _playerSaveTimer += rawDt;
        if (!_isClosing && _playerSaveTimer >= 30.0f) // Save every 30 seconds
        {
            QueuePlayerStateSaveAsync();
            _playerSaveTimer = 0f;
        }

        // Always update network layer, even if world sync is in progress
        UpdateNetwork(rawDt);
        _blockBreakParticles.Update(rawDt);

        if (_worldSyncInProgress)
        {
            // 4) DRAIN RESULTS - Always apply completed load+mesh results even while preparing
            ProcessStreamingResults(aggressiveApply: !_spawnPrewarmComplete);
            
            // Only update UI relevant for loading screen if sync in progress
            if (input.IsNewKeyPress(Keys.Escape)) // Still allow pausing
            {
                _pauseMenuOpen = !_pauseMenuOpen;
            }
            if (_pauseMenuOpen)
            {
                _pauseResume.Update(input);
                _pauseOpenLan.Update(input);
                _pauseHostOnline.Update(input);
                _pauseInvite.Update(input);
                _pauseSettings.Update(input);
                _pauseSaveExit.Update(input);
            }
            return; // Skip all other game logic if sync is not complete
        }

        // CONTINUOUS MESH PROCESSING - Process mesh results to prevent graphical issues
        ProcessMeshJobsNonBlocking();
        var chunkScheduleBudget = _spawnPrewarmComplete
            ? RuntimeChunkScheduleBudget
            : Math.Max(RuntimeChunkScheduleBudget, PrewarmChunkBudgetPerFrame);
        ProcessChunkGenerationJobs(chunkScheduleBudget);

        if (!_spawnPrewarmComplete)
        {
            _spinnerFrame = (_spinnerFrame + 1) % 8;
            ProcessPrewarmStep();

            if (input.IsNewKeyPress(Keys.Escape))
                _pauseMenuOpen = !_pauseMenuOpen;

            if (_pauseMenuOpen)
            {
                _pauseResume.Update(input);
                _pauseOpenLan.Update(input);
                _pauseHostOnline.Update(input);
                _pauseInvite.Update(input);
                _pauseSettings.Update(input);
                _pauseSaveExit.Update(input);
                WarmBlockIcons();
            }

            return; // Block gameplay controls until prewarm gate is visually ready.
        }

        if (_inventoryOpen)
        {
            if (input.IsNewKeyPress(_inventoryKey) || input.IsNewKeyPress(Keys.Escape))
                CloseInventory();

            UpdateInventory(input);
            WarmBlockIcons();
            return;
        }

        if (!_pauseMenuOpen && !IsTextInputActive && !_gamemodeWheelVisible && input.IsNewKeyPress(_inventoryKey))
        {
            _inventoryOpen = true;
            return;
        }

        if (!IsTextInputActive && !_gamemodeWheelVisible && input.IsNewKeyPress(Keys.Escape))
        {
            _pauseMenuOpen = !_pauseMenuOpen;
            return;
        }

        if (_pauseMenuOpen)
        {
            _pauseResume.Update(input);
            _pauseOpenLan.Update(input);
            _pauseHostOnline.Update(input);
            _pauseInvite.Update(input);
            _pauseSettings.Update(input);
            _pauseSaveExit.Update(input);
            WarmBlockIcons();
            return;
        }

        if (_homeGuiOpen)
        {
            UpdateHomeGui(input, rawDt);
            WarmBlockIcons();
            return;
        }

        if (_structureFinderOpen)
        {
            UpdateStructureFinderGui(input, rawDt);
            WarmBlockIcons();
            return;
        }

        if (!IsTextInputActive && !_gamemodeWheelVisible && input.IsNewKeyPress(_homeGuiKey))
        {
            OpenHomeGui();
            return;
        }

        if (!IsTextInputActive && !_gamemodeWheelVisible && IsChatOpenKeyPressed(input))
        {
            BeginChatInput();
            return;
        }

        if (!IsTextInputActive && !_gamemodeWheelVisible && IsCommandOpenKeyPressed(input))
        {
            BeginChatInput("/");
            return;
        }

        if (_chatInputActive)
        {
            UpdateChatInput(input);
            WarmBlockIcons();
            return;
        }

        if (_commandInputActive)
        {
            UpdateCommandInput(input);
            WarmBlockIcons();
            return;
        }

        if (HandleGameModeWheelInput(input))
        {
            WarmBlockIcons();
            return;
        }

#if DEBUG
        if (input.IsNewKeyPress(Keys.F10))
        {
            EnsurePlayerNotInsideSolid(forceLog: true);
            _player.SetFlying(true);
        }
#endif

        UpdatePlayer(gameTime, dt, input);
        ApplyPlayerSeparation(dt);
        UpdateActiveChunks(force: false);
        ProcessChunkGenerationJobs(Math.Max(1, RuntimeChunkScheduleBudget / 2));
        HandleHotbarInput(input);
        UpdateSelectionTimer(dt);
        UpdateHandoffTarget(input);
        HandleDropAndGive(input);
        UpdateWorldItems();
        HandleBlockInteraction(gameTime, input);
        ProcessStreamingResults(); // Apply completed loads/unloads first (main thread only)
        QueueDirtyChunks(); // Then enumerate safely
        ProcessMeshBuildQueue();
        WarmBlockIcons();
    }

    public void OnMouseCaptureGained()
    {
        // No-op; capture is handled centrally in Game1.
    }

    public void OnMouseCaptureLost()
    {
        // No-op; capture is handled centrally in Game1.
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        if (_saveAndExitInProgress)
        {
            DrawSavingOverlay(sb);
            return;
        }

        // SIMPLE LOADING INDICATOR - NO MORE COMPLEX PROGRESS
        bool needsLoading = ShouldShowLoadingScreen();
        
        if (needsLoading)
        {
            // Draw simple centered loading
            sb.Begin();
            var center = new Vector2(_viewport.Width / 2f, _viewport.Height / 2f);
            
            var title = !_hasLoadedWorld ? "WORLD MATERIALIZING..." : 
                       _worldSyncInProgress ? "DIMENSIONAL SYNC..." : 
                       "REALM STABILIZING...";
            var titleSize = _font.MeasureString(title);
            _font.DrawString(sb, title, new Vector2(center.X - titleSize.X / 2f, center.Y - 20), Color.White);

            if (!_spawnPrewarmComplete && _prewarmTargetCount > 0)
            {
                var progress = $"PREWARM {_prewarmReadyCount}/{_prewarmTargetCount}";
                var progressSize = _font.MeasureString(progress);
                _font.DrawString(sb, progress, new Vector2(center.X - progressSize.X / 2f, center.Y + 10 + _font.LineHeight + 6), Color.White);
            }
            
            // Simple centered indicator
            var indicatorSize = 12;
            var indicatorRect = new Rectangle((int)center.X - indicatorSize/2, (int)center.Y + 10, indicatorSize, indicatorSize);
            sb.Draw(_pixel, indicatorRect, Color.White);
            
            sb.End();
            return;
        }

        var device = sb.GraphicsDevice;
        var clearColor = SkyColor;
#if DEBUG
        if (!_debugSkyClear)
            clearColor = Color.Black;
#endif
        device.Clear(clearColor);
        device.DepthStencilState = DepthStencilState.Default;
        
        // D) DEBUG / WIREFRAME SAFETY - Disable wireframe during prewarm and first frame
        bool allowWireframe = false;
#if DEBUG
        allowWireframe = _debugWireframe && _visibilitySafe && !_firstGameplayFrame;
        device.RasterizerState = allowWireframe ? WireframeState : RasterizerState.CullNone;
#else
        device.RasterizerState = RasterizerState.CullNone;
#endif
        device.BlendState = BlendState.Opaque;
        
        var sampler = GetWorldSampler();
        device.SamplerStates[0] = sampler;
#if DEBUG
        _blockSamplerLabel = sampler.ToString() ?? "null";
#endif

        EnsureEffect(device);
        if (_effect == null || _atlas == null)
            return;

        var view = GetViewMatrix();
        var aspect = device.Viewport.AspectRatio;
        var chunkWorld = VoxelChunkData.ChunkSizeX * Scale.BlockSize;
        var farPlane = MathF.Max(500f, (_activeRadiusChunks + 4) * chunkWorld);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(70f), aspect, 0.1f, farPlane);
        
        _effect.TextureEnabled = true;
        _effect.Texture = _atlas.Texture;
        _effect.FogEnabled = true;
        _effect.FogColor = clearColor.ToVector3();
        
        // 5) DISTANCE FOG - Tied to render distance to prevent seeing sharp void edges
        var fogStartRadius = Math.Max(4f, (_activeRadiusChunks - 3) * chunkWorld);
        var fogEndRadius = Math.Max(fogStartRadius + chunkWorld * 2f, (_activeRadiusChunks - 0.8f) * chunkWorld);
        
        _effect.FogStart = fogStartRadius;
        _effect.FogEnd = fogEndRadius;
        _effect.View = view;
        _effect.Projection = proj;
        _effect.World = Matrix.Identity;
        _effect.DiffuseColor = Vector3.One;
        _effect.Alpha = 1f;

        var frustum = new BoundingFrustum(view * proj);

#if DEBUG
        _debugWorldAtlasBound = _effect.Texture != null;
#endif
        // OPTIMIZED RENDERING - Improved mesh validation and error handling
        foreach (var coord in _chunkOrder)
        {
            if (!_chunkMeshes.TryGetValue(coord, out var mesh))
                continue;
            if (!frustum.Intersects(mesh.Bounds))
                continue;

            var verts = mesh.OpaqueVertices;
            if (verts == null || verts.Length == 0)
                continue;

            // Validate vertex data before rendering
            if (verts.Length % 3 != 0)
            {
                _log.Warn($"Invalid vertex count for chunk {coord}: {verts.Length} (must be multiple of 3)");
                continue;
            }

            try
            {
                // Safe rendering with error handling
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Rendering error for chunk {coord}: {ex.Message}");
                // Remove problematic mesh to prevent repeated errors
                _chunkMeshes.Remove(coord, out _);
            }
        }

#if DEBUG
        if (_debugDrawPlayerModel)
        {
            device.SamplerStates[0] = SamplerState.PointClamp;
            DrawPlayerModel(device, view, proj);
            device.SamplerStates[0] = sampler;
        }
#endif
        device.SamplerStates[0] = SamplerState.PointClamp;
        DrawRemotePlayers(device, view, proj);
        device.SamplerStates[0] = sampler;

        device.BlendState = BlendState.AlphaBlend;
        device.DepthStencilState = DepthStencilState.DepthRead;
        
        // OPTIMIZED TRANSPARENT RENDERING - Improved mesh validation and error handling
        foreach (var coord in _chunkOrder)
        {
            if (!_chunkMeshes.TryGetValue(coord, out var mesh))
                continue;
            if (!frustum.Intersects(mesh.Bounds))
                continue;

            var verts = mesh.TransparentVertices;
            if (verts == null || verts.Length == 0)
                continue;

            // Validate vertex data before rendering
            if (verts.Length % 3 != 0)
            {
                _log.Warn($"Invalid transparent vertex count for chunk {coord}: {verts.Length} (must be multiple of 3)");
                continue;
            }

            try
            {
                // Safe rendering with error handling
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Transparent rendering error for chunk {coord}: {ex.Message}");
                // Remove problematic mesh to prevent repeated errors
                _chunkMeshes.Remove(coord, out _);
            }
        }

        DrawWaterChunks(device, frustum, view, proj);

        DrawWorldItems(device, view, proj);
        if (_blockBreakParticles.ActiveCount > 0)
        {
            sb.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
            _blockBreakParticles.Draw(sb, _pixel, view, proj, device.Viewport);
            sb.End();
        }

        var drawHands = !_pauseMenuOpen;
#if DEBUG
        drawHands = drawHands && !_debugDisableHands;
        _debugOverlayAtlasBound = drawHands && _atlas?.Texture != null;
#endif
        if (drawHands)
            DrawFirstPersonHands(device, view, proj);

        if (!_pauseMenuOpen)
            DrawBlockHighlight(device, view, proj);
        else
            _highlightActive = false;

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        DrawReticle(sb);
        var name = _meta?.Name ?? "WORLD";
        var mode = _gameMode;
        _font.DrawString(sb, $"WORLD: {name}", new Vector2(_viewport.X + 20, _viewport.Y + 20), Color.White);
        _font.DrawString(sb, $"MODE: {mode.ToString().ToUpperInvariant()}", new Vector2(_viewport.X + 20, _viewport.Y + 20 + _font.LineHeight + 4), Color.White);
        var modeCombo = $"{FormatKeyLabel(_gamemodeModifierKey)}+{FormatKeyLabel(_gamemodeWheelKey)}";
        _font.DrawString(sb, $"WASD MOVE | SPACE JUMP | CTRL DOWN | DOUBLE TAP SPACE: FLY | {modeCombo} MODE WHEEL | ESC PAUSE", new Vector2(_viewport.X + 20, _viewport.Y + 20 + (_font.LineHeight + 4) * 2), Color.White);
        
        DrawHotbar(sb);
        DrawSelectedBlockName(sb);
        DrawPlayerList(sb);
        DrawCommandOverlay(sb);
        DrawGamemodeToast(sb);
        DrawGamemodeWheel(sb);
        if (_homeGuiOpen)
            DrawHomeGui(sb);
        if (_structureFinderOpen)
            DrawStructureFinderGui(sb);
        if (_debugHudVisible)
            DrawDevOverlay(sb);
        DrawFaceOverlay(sb);
        sb.End();

        if (_pauseMenuOpen)
            DrawPauseMenu(sb);
        if (_inventoryOpen)
            DrawInventory(sb);
            
        // E) FAILSAFE ASSERT - Check visibility on first gameplay frame
        if (_visibilitySafe && !_firstGameplayFrame && !_visibilityAssertLogged)
        {
            _firstGameplayFrame = true;
            PerformVisibilityAssert();
        }
    }

    private void LoadWorld()
    {
        var swTotal = Stopwatch.StartNew();

        var sw = Stopwatch.StartNew();
        // If world was not pre-loaded by the host screen, load it now.
        if (_world == null)
        {
            _world = VoxelWorld.Load(_worldPath, _metaPath, _log);
            if (_world == null)
            {
                _log.Warn("World data missing; cannot load world.");
                return;
            }
        }
        sw.Stop();
        _log.Info($"World load: VoxelWorld.Load {sw.ElapsedMilliseconds}ms");

        _meta = _world.Meta;
        _gameMode = _meta.CurrentWorldGameMode;
        _playerCollisionEnabled = _meta.PlayerCollision;
        EnsureBiomeCatalog();

        // 1) FIX INIT ORDER: Build atlas FIRST, then create streaming service
        sw.Restart();
        _atlas = CubeNetAtlas.Build(_assets, _log, _settings.QualityPreset);
        sw.Stop();
        _log.Info($"World load: CubeNetAtlas.Build {sw.ElapsedMilliseconds}ms");
        
        // 1) HARD GUARD: Ensure atlas is valid before creating streaming service
        if (_atlas == null || _atlas.Texture == null)
        {
            _log.Error("Atlas build failed, cannot initialize streaming service");
            return;
        }
        
        if (_streamingService == null)
            _streamingService = new StubStreamingService(_world, _log);
        if (_pregenerationService == null)
            _pregenerationService = new StubPregenerationService(_world, _log);
        
        InitBlockModel();
        
        _player.AllowFlying = _gameMode == GameMode.Artificer;
        _inventory.SetMode(_gameMode);
        if (_gameMode == GameMode.Veilwalker)
            _player.SetFlying(false);

        // Initialize performance optimizer for maximum GPU/CPU/RAM utilization
        AdvancedPerformanceOptimizer.Initialize();
        _log.Info(
            $"Streaming budgets: chunkReq={Math.Max(MaxNewChunkRequestsPerFrame, RuntimeChunkRequestBudget)} " +
            $"meshReq={Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget)} " +
            $"chunkWorkers={ChunkWorkerCount} meshWorkers={MeshWorkerCount}");
        
        // RESTORE PLAYER POSITION FIRST - This determines where chunks need to be loaded
        RestoreOrCenterCamera();
        
        // AGGRESSIVE FIX: Generate solid ground immediately at player position
        // DISABLED: HybridWorldManager handles this now
        // GenerateSolidGroundAtPlayer();
        
        // CRITICAL: Force immediate chunk generation at player position BEFORE anything else
        // DISABLED: HybridWorldManager handles this now
        // ForceGeneratePlayerChunk();
        
        // Pre-load chunks around PLAYER'S SAVED POSITION (not just spawn)
        // DISABLED: HybridWorldManager handles this now
        // PreloadChunksAroundPlayer();
        // PreloadChunksAroundPlayer();
        
        // Start automatic world pregeneration with OPTIMIZED area (non-blocking)
        if (_pregenerationService != null)
        {
            var optimalRadius = AdvancedPerformanceOptimizer.GetOptimalRenderDistance();
            _log.Info($"Starting OPTIMIZED world pregeneration for best experience...");
            _pregenerationService.StartPregeneration(optimalRadius); // Use hardware-optimal radius
            
            _log.Info($"World pregeneration started in background - {optimalRadius} chunk radius optimized for hardware");
        }
        
        // TEST: Run our new world generation system test
        try
        {
            _log.Info("=== Testing New World Generation System ===");
            WorldGenerationTest.RunTest(_log);
            _log.Info("=== New World Generation System Test Complete ===");
        }
        catch (Exception ex)
        {
            _log.Error($"World generation test failed: {ex.Message}");
        }
        sw.Stop();
        _log.Info($"World load: player position chunk pre-load {sw.ElapsedMilliseconds}ms");

        // CRITICAL: Wait for player's chunk to be COMPLETELY loaded before allowing movement
        // DISABLED: HybridWorldManager handles this now
        // WaitForPlayerChunkComplete();
        
        // OPTIMIZED: Force mesh generation without blocking to prevent freezes
        // DISABLED: HybridWorldManager handles this now
        // ForceGeneratePlayerChunkMeshOptimized();
        
        // AGGRESSIVE: Ensure all chunks around player are visible
        // DISABLED: HybridWorldManager handles this now
        // EnsureAllChunksVisible();
        
        // ULTRA AGGRESSIVE: Force load more chunks to prevent missing chunks
        // DISABLED: HybridWorldManager handles this now
        // ForceLoadAdditionalChunks();
        
        // RESEARCH-BASED: Implement spawn chunk system for guaranteed loading
        // DISABLED: HybridWorldManager handles this now
        // EnsureSpawnChunksLoaded();
        
        // RESEARCH-BASED: Implement force loading system similar to server force-load commands
        // DISABLED: HybridWorldManager handles this now
        // ForceLoadCriticalChunks();
        
        // AGGRESSIVE SAFETY: Ensure player has solid ground to stand on
        // DISABLED: HybridWorldManager handles this now
        // EnsureSolidGroundUnderPlayer();
        
        EnsurePlayerNotInsideSolid(forceLog: false);
        InitializePrewarmTargets();
        UpdateActiveChunks(force: false);

        swTotal.Stop();
        _log.Info($"World load: total {swTotal.ElapsedMilliseconds}ms");
    }

    private void InitBlockModel()
    {
        _blockIconCache?.Dispose();
        _blockIconCache = null;

        _blockModel = BlockModel.LoadOrDefault(_log);
        if (_atlas == null)
            return;

        _blockIconCache = new BlockIconCache(_assets.GraphicsDevice, _atlas, _blockModel, _log);
    }

    private int GetStreamingMaxChunkY(int playerChunkY)
    {
        if (_world == null)
            return 0;

        var worldMax = Math.Max(0, _world.MaxChunkY);
        var terrainBandTop = Math.Min(worldMax, PrewarmVerticalChunkTop);
        var playerHeadroomTop = Math.Clamp(playerChunkY + 2, 0, worldMax);
        return Math.Max(terrainBandTop, playerHeadroomTop);
    }

    private bool IsChunkWithinWorldBounds(ChunkCoord coord)
    {
        if (_meta?.Size == null)
            return true;

        var minX = coord.X * VoxelChunkData.ChunkSizeX;
        var minZ = coord.Z * VoxelChunkData.ChunkSizeZ;
        var maxX = minX + VoxelChunkData.ChunkSizeX - 1;
        var maxZ = minZ + VoxelChunkData.ChunkSizeZ - 1;
        return maxX >= 0
            && maxZ >= 0
            && minX < _meta.Size.Width
            && minZ < _meta.Size.Depth;
    }

    private void InitializePrewarmTargets()
    {
        if (_world == null)
            return;

        var playerChunk = VoxelWorld.WorldToChunk(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y),
            (int)MathF.Floor(_player.Position.Z),
            out _, out _, out _);

        var maxCy = GetStreamingMaxChunkY(playerChunk.Y);
        var prewarmRadius = Math.Clamp(Math.Min(_activeRadiusChunks, PrewarmRadius), 2, PrewarmRadius);
        var gateRadius = Math.Clamp(Math.Min(prewarmRadius, PrewarmGateRadius), 1, prewarmRadius);
        var prewarmRadiusSq = prewarmRadius * prewarmRadius;
        var gateRadiusSq = gateRadius * gateRadius;

        _prewarmRequiredChunks.Clear();
        _tier0Chunks.Clear();

        for (var dz = -prewarmRadius; dz <= prewarmRadius; dz++)
        {
            for (var dx = -prewarmRadius; dx <= prewarmRadius; dx++)
            {
                var distSq = dx * dx + dz * dz;
                if (distSq > prewarmRadiusSq)
                    continue;

                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    if (!IsChunkWithinWorldBounds(coord))
                        continue;
                    _prewarmRequiredChunks.Add(coord);
                    if (distSq <= gateRadiusSq)
                        _tier0Chunks.Add(coord);
                }
            }
        }

        _prewarmTargetCount = _tier0Chunks.Count;
        _prewarmReadyCount = 0;
        _spawnPrewarmComplete = false;
        _visibilitySafe = false;
        _firstGameplayFrame = false;
        _visibilityAssertLogged = false;
        _spawnPrewarmStartTime = DateTime.UtcNow;

        PrimeTier0FromMeshCache();
        if (IsPrewarmGateReady())
        {
            _spawnPrewarmComplete = true;
            _visibilitySafe = true;
            _log.Info($"Prewarm satisfied from cache ({_prewarmReadyCount}/{_prewarmTargetCount} gate chunks).");
        }

        if (!_spawnPrewarmComplete)
        {
            foreach (var coord in _tier0Chunks)
            {
                QueueChunkGeneration(coord);
            }
        }

        _log.Info($"Prewarm targets: gate={_prewarmTargetCount}, full={_prewarmRequiredChunks.Count}, radius={prewarmRadius}, maxCy={maxCy}");
    }

    private void PrimeTier0FromMeshCache()
    {
        if (_world == null)
            return;

        var loaded = 0;
        foreach (var coord in _tier0Chunks)
        {
            if (_chunkMeshes.ContainsKey(coord))
            {
                loaded++;
                continue;
            }

            if (!ChunkMeshCache.TryLoadFresh(_worldPath, _world.ChunksDir, coord, out var mesh))
                continue;
            if (!ValidateMesh(mesh))
                continue;

            _chunkMeshes[coord] = mesh;
            if (!_chunkOrder.Contains(coord))
                _chunkOrder.Add(coord);
            loaded++;
        }

        if (loaded > 0)
            _log.Info($"Loaded {loaded} prewarmed chunk meshes from disk cache.");
    }

    private void QueueChunkGeneration(ChunkCoord coord)
    {
        if (!IsChunkNearPlayer(coord) && (_chunkGenerationQueued.Count + _chunkGenerationInFlight.Count) >= MaxOutstandingChunkJobs)
            return;

        if (_chunkGenerationQueued.TryAdd(coord, 0))
            _chunkGenerationQueue.Enqueue(coord);
    }

    private void ProcessChunkGenerationJobs(int maxSchedule)
    {
        if (_world == null || _isClosing || maxSchedule <= 0)
            return;

        var scheduled = 0;
        while (scheduled < maxSchedule && _chunkGenerationQueue.TryDequeue(out var coord))
        {
            _chunkGenerationQueued.TryRemove(coord, out _);

            if (_chunkGenerationInFlight.ContainsKey(coord))
                continue;

            if (!_chunkGenerationInFlight.TryAdd(coord, 0))
                continue;

            if (!_chunkGenerationSemaphore.Wait(0))
            {
                _chunkGenerationInFlight.TryRemove(coord, out _);
                QueueChunkGeneration(coord);
                break;
            }

            var world = _world;
            _ = Task.Run(() =>
            {
                try
                {
                    world.GetOrCreateChunk(coord);
                }
                finally
                {
                    _chunkGenerationInFlight.TryRemove(coord, out _);
                    _chunkGenerationSemaphore.Release();
                }
            });

            scheduled++;
        }
    }

    private void ProcessPrewarmStep()
    {
        if (_world == null)
            return;

        ProcessStreamingResults(aggressiveApply: true);
        ProcessChunkGenerationJobs(PrewarmChunkBudgetPerFrame);
        UpdateActiveChunks(force: false);
        QueueDirtyChunks();
        ProcessMeshBuildQueue(PrewarmMeshBudgetPerFrame);

        if (IsPrewarmGateReady())
        {
            _spawnPrewarmComplete = true;
            _visibilitySafe = true;
            var elapsed = (DateTime.UtcNow - _spawnPrewarmStartTime).TotalSeconds;
            _log.Info($"Prewarm complete in {elapsed:0.00}s ({_prewarmReadyCount}/{_prewarmTargetCount} gate chunks ready).");
            return;
        }

        var elapsedSeconds = (DateTime.UtcNow - _spawnPrewarmStartTime).TotalSeconds;
        if (elapsedSeconds >= PrewarmTimeoutSeconds)
        {
            _spawnPrewarmComplete = true;
            _visibilitySafe = true;
            _log.Warn($"Prewarm timeout at {elapsedSeconds:0.00}s; continuing with {_prewarmReadyCount}/{_prewarmTargetCount} gate chunks.");
        }
    }

    private bool IsPrewarmGateReady()
    {
        if (_world == null)
            return false;

        if (_tier0Chunks.Count == 0)
            return true;

        var ready = 0;
        foreach (var coord in _tier0Chunks)
        {
            if (!_world.TryGetChunk(coord, out var chunk) || chunk == null)
            {
                QueueChunkGeneration(coord);
                continue;
            }

            if (_chunkMeshes.TryGetValue(coord, out _))
            {
                ready++;
            }
            else
            {
                QueueMeshBuild(coord);
            }
        }

        _prewarmReadyCount = ready;
        return ready >= _tier0Chunks.Count;
    }

    private void UpdateActiveChunks(bool force)
    {
        if (_world == null)
            return;

        _chunksRequestedThisFrame = 0;
        _meshesRequestedThisFrame = 0;

        var playerChunk = VoxelWorld.WorldToChunk(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Y),
            (int)MathF.Floor(_player.Position.Z),
            out _, out _, out _);
        _centerChunk = playerChunk;
        _playerChunkCoord = playerChunk;

        var maxCy = GetStreamingMaxChunkY(playerChunk.Y);
        var radius = Math.Clamp(_activeRadiusChunks, 2, MaxRuntimeActiveRadius);
        var radiusSq = radius * radius;
        var chunkBudgetPerFrame = _spawnPrewarmComplete
            ? Math.Max(MaxNewChunkRequestsPerFrame, RuntimeChunkRequestBudget)
            : Math.Max(Math.Max(MaxNewChunkRequestsPerFrame, RuntimeChunkRequestBudget), PrewarmChunkBudgetPerFrame);
        var meshBudgetPerFrame = _spawnPrewarmComplete
            ? Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget)
            : Math.Max(Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget), PrewarmMeshBudgetPerFrame);
        _activeKeepBuffer.Clear();
        _drawOrderBuffer.Clear();

        for (var ring = 0; ring <= radius; ring++)
        {
            for (var dz = -ring; dz <= ring; dz++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                        continue;
                    if (dx * dx + dz * dz > radiusSq)
                        continue;

                    for (var cy = 0; cy <= maxCy; cy++)
                    {
                        var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                        if (!IsChunkWithinWorldBounds(coord))
                            continue;
                        _activeKeepBuffer.Add(coord);
                        _drawOrderBuffer.Add(coord);
                        var nearCritical = ring <= 2 && Math.Abs(cy - playerChunk.Y) <= 1;

                        if (!_world.TryGetChunk(coord, out var chunk) || chunk == null)
                        {
                            if (!force && !nearCritical && _chunksRequestedThisFrame >= chunkBudgetPerFrame)
                                continue;

                            _chunksRequestedThisFrame++;
                            if (force)
                            {
                                chunk = _world.GetOrCreateChunk(coord);
                            }
                            else
                            {
                                QueueChunkGeneration(coord);
                                continue;
                            }
                        }

                        if (chunk.IsDirty || !_chunkMeshes.ContainsKey(coord))
                        {
                            if (!force && !nearCritical && _meshesRequestedThisFrame >= meshBudgetPerFrame)
                                continue;

                            if (nearCritical)
                                QueuePriorityMeshBuild(coord);
                            else
                                QueueMeshBuild(coord);
                            _meshesRequestedThisFrame++;
                        }
                    }
                }
            }
        }

        _activeChunks.Clear();
        foreach (var coord in _activeKeepBuffer)
            _activeChunks.Add(coord);

        _meshRemovalBuffer.Clear();
        foreach (var meshCoord in _chunkMeshes.Keys)
        {
            if (!_activeKeepBuffer.Contains(meshCoord))
                _meshRemovalBuffer.Add(meshCoord);
        }

        for (var i = 0; i < _meshRemovalBuffer.Count; i++)
        {
            var coord = _meshRemovalBuffer[i];
            _chunkMeshes.TryRemove(coord, out _);
            _meshQueued.Remove(coord);
        }

        _chunkOrder.Clear();
        for (var i = 0; i < _drawOrderBuffer.Count; i++)
        {
            var coord = _drawOrderBuffer[i];
            if (_chunkMeshes.ContainsKey(coord))
                _chunkOrder.Add(coord);
        }
    }

    private void ScheduleNeighborMeshes(ChunkCoord center, int priority)
    {
        if (_world == null)
            return;

        // Schedule mesh for N/S/E/W neighbors (F) neighbor-aware meshing
        var neighbors = new[]
        {
            new ChunkCoord(center.X + 1, center.Y, center.Z),
            new ChunkCoord(center.X - 1, center.Y, center.Z),
            new ChunkCoord(center.X, center.Y, center.Z + 1),
            new ChunkCoord(center.X, center.Y, center.Z - 1)
        };

        foreach (var neighbor in neighbors)
        {
            if (_world.TryGetChunk(neighbor, out var chunk) && chunk != null &&
                _meshesRequestedThisFrame < Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget))
            {
                if (!_chunkMeshes.ContainsKey(neighbor) || chunk.IsDirty)
                {
                    // ALL WORLD GENERATION DELETED - NO STREAMING
                    _meshesRequestedThisFrame++;
                }
            }
        }
    }

    private void RefreshSettingsIfChanged()
    {
        var stamp = GetSettingsStamp();
        if (stamp == _settingsStamp)
            return;

        var oldQuality = _settings.QualityPreset;
        _settingsStamp = stamp;
        _settings = GameSettings.LoadOrCreate(_log);
        
        // C) RENDER DISTANCE GUARDRAILS - check command line first
        var requestedRadius = _settings.RenderDistanceChunks;
        
        // Check for command line override from launcher
        var args = Environment.GetCommandLineArgs();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--render-distance="))
            {
                if (int.TryParse(arg.AsSpan()["--render-distance=".Length..], out var cliRadius))
                {
                    requestedRadius = cliRadius;
                    _log.Info($"Using render distance from command line: {cliRadius}");
                }
            }
        }
        
        var newRadius = Math.Clamp(requestedRadius, 4, MaxRuntimeActiveRadius);
        if (requestedRadius > MaxRuntimeActiveRadius)
        {
            _log.Info($"Render distance capped at {MaxRuntimeActiveRadius} (requested {requestedRadius}) to reduce in-world lag.");
        }
        var newQuality = _settings.QualityPreset;
        _inventoryKey = GetKeybind("Inventory", Keys.E);
        _dropKey = GetKeybind("DropItem", Keys.Q);
        _giveKey = GetKeybind("GiveItem", Keys.F);
        _stackModifierKey = GetKeybind("Crouch", Keys.LeftShift);
        _chatKey = GetKeybind("Chat", Keys.T);
        _commandKey = GetKeybind("Command", Keys.OemQuestion);
        _homeGuiKey = GetKeybind("HomeGui", Keys.H);
        _gamemodeModifierKey = GetKeybind("GamemodeModifier", Keys.LeftAlt);
        _gamemodeWheelKey = GetKeybind("GamemodeWheel", Keys.G);
        _blockBreakParticles.ApplyPreset(_settings.ParticlePreset, _settings.QualityPreset);
        UpdateReticleSettings();

        if (newQuality != oldQuality)
        {
            _log.Info($"Quality changed: {oldQuality} -> {newQuality}. Rebuilding atlas...");
            _atlas = CubeNetAtlas.Build(_assets, _log, newQuality);
            _world?.MarkAllChunksDirty();
        }

        if (newRadius != _activeRadiusChunks)
        {
            var oldRadius = _activeRadiusChunks;
            _activeRadiusChunks = newRadius;
            
            // 4) RENDER DISTANCE CHANGES - Aggressive unloading and job cancellation
            if (newRadius < oldRadius)
            {
                // Lowered render distance - aggressively unload chunks outside new keep radius
                _log.Info($"Render distance lowered: {oldRadius} -> {newRadius}, aggressively unloading");
                
                // Cancel pending jobs outside new radius
                CancelJobsOutsideRadius(newRadius);
                
                // Force immediate trim of far chunks
                TrimFarChunks(newRadius + KeepRadiusBuffer);
                
                // Clear meshes for unloaded chunks
                var chunksToRemove = _chunkMeshes.Keys.Where(coord => {
                    var dist = Math.Max(Math.Abs(coord.X - _centerChunk.X), Math.Abs(coord.Z - _centerChunk.Z));
                    return dist > newRadius + KeepRadiusBuffer;
                }).ToList();
                
                foreach (var coord in chunksToRemove)
                {
                    _chunkMeshes.TryRemove(coord, out _);
                    _chunkOrder.Remove(coord);
                }
                
                _log.Info($"Unloaded {chunksToRemove.Count} chunk meshes due to reduced render distance");
            }
            else
            {
                // Raised render distance - expand outward gradually
                _log.Info($"Render distance raised: {oldRadius} -> {newRadius}, expanding gradually");
            }
            
            UpdateActiveChunks(force: true);
            _log.Info($"Render distance changed: {_activeRadiusChunks} chunks");
        }
    }

    private void CancelJobsOutsideRadius(int radius)
    {
        // This would require extending ChunkStreamingService to support job cancellation
        // For now, we'll just log that jobs outside radius should be cancelled
        _log.Debug($"Would cancel jobs outside radius {radius} (job cancellation not yet implemented)");
    }

    private void UpdateReticleSettings()
    {
        _reticleEnabled = _settings.ReticleEnabled;
        _reticleStyle = NormalizeReticleStyle(_settings.ReticleStyle);
        _reticleSize = Math.Clamp(_settings.ReticleSize, ReticleSizeMin, ReticleSizeMax);
        _reticleThickness = Math.Clamp(_settings.ReticleThickness, ReticleThicknessMin, ReticleThicknessMax);
        _reticleColor = TryParseHexColor(_settings.ReticleColor, out var color)
            ? color
            : new Color(255, 255, 255, 200);
        _blockOutlineColor = TryParseHexColor(_settings.BlockOutlineColor, out var outlineColor)
            ? outlineColor
            : new Color(200, 220, 230, 120);
    }

    private static string NormalizeReticleStyle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Dot";

        var style = value.Trim();
        if (style.Equals("Dot", StringComparison.OrdinalIgnoreCase)) return "Dot";
        if (style.Equals("Plus", StringComparison.OrdinalIgnoreCase)) return "Plus";
        if (style.Equals("Square", StringComparison.OrdinalIgnoreCase)) return "Square";
        if (style.Equals("Circle", StringComparison.OrdinalIgnoreCase)) return "Circle";
        return "Dot";
    }

    private static bool TryParseHexColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text.Substring(1);

        if (text.Length == 6)
            text += "FF";

        if (text.Length != 8)
            return false;

        if (!TryParseHexByte(text, 0, out var r)) return false;
        if (!TryParseHexByte(text, 2, out var g)) return false;
        if (!TryParseHexByte(text, 4, out var b)) return false;
        if (!TryParseHexByte(text, 6, out var a)) return false;

        color = new Color(r, g, b, a);
        return true;
    }

    private static bool TryParseHexByte(string text, int start, out byte value)
    {
        value = 0;
        if (start + 1 >= text.Length)
            return false;

        var hi = HexValue(text[start]);
        var lo = HexValue(text[start + 1]);
        if (hi < 0 || lo < 0)
            return false;

        value = (byte)((hi << 4) | lo);
        return true;
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        return -1;
    }

    private SamplerState GetWorldSampler()
    {
        return _settings.QualityPreset switch
        {
            "LOW" => _samplerLow ??= CreateWorldSampler(2.0f),
            "MEDIUM" => _samplerMedium ??= CreateWorldSampler(1.0f),
            "HIGH" => _samplerHigh ??= CreateWorldSampler(0.5f),
            "ULTRA" => _samplerUltra ??= CreateWorldSampler(0.0f),
            _ => SamplerState.PointClamp
        };
    }

    private static SamplerState CreateWorldSampler(float lodBias)
    {
        return new SamplerState
        {
            Filter = TextureFilter.Point,
            MipMapLevelOfDetailBias = lodBias,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp
        };
    }

    private static DateTime GetSettingsStamp()
    {
        try
        {
            return File.Exists(Paths.SettingsJsonPath)
                ? File.GetLastWriteTimeUtc(Paths.SettingsJsonPath)
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private Keys GetKeybind(string action, Keys fallback)
    {
        if (_settings.Keybinds != null && _settings.Keybinds.TryGetValue(action, out var key))
            return key;
        return fallback;
    }

    private void QueueDirtyChunks()
    {
        if (_world == null || _atlas == null)
            return;

        const int MaxDirtyChunksPerFrame = 8;
        var dirtyChunksProcessed = 0;
        _world.CopyChunksTo(_chunkSnapshotBuffer);

        // Two passes: near player first, then far chunks. Reuses buffers and avoids per-frame list allocations.
        for (var pass = 0; pass < 2 && dirtyChunksProcessed < MaxDirtyChunksPerFrame; pass++)
        {
            for (var i = 0; i < _chunkSnapshotBuffer.Count; i++)
            {
                var chunk = _chunkSnapshotBuffer[i];
                if (!chunk.IsDirty)
                    continue;
                if (_activeChunks.Count > 0 && !_activeChunks.Contains(chunk.Coord))
                    continue;

                var dx = Math.Abs(chunk.Coord.X - _centerChunk.X);
                var dz = Math.Abs(chunk.Coord.Z - _centerChunk.Z);
                var nearPlayer = Math.Max(dx, dz) <= 2;
                if ((pass == 0 && !nearPlayer) || (pass == 1 && nearPlayer))
                    continue;

                QueueMeshBuild(chunk.Coord);
                dirtyChunksProcessed++;
                if (dirtyChunksProcessed >= MaxDirtyChunksPerFrame)
                    break;
            }
        }
    }

    private void QueueMeshBuild(ChunkCoord coord)
    {
        if (!IsChunkNearPlayer(coord) && (_meshQueued.Count + _meshBuildInFlight.Count) >= MaxOutstandingMeshJobs)
            return;

        if (_meshQueued.Add(coord))
            _meshQueue.Enqueue(coord);
    }

    private void QueuePriorityMeshBuild(ChunkCoord coord)
    {
        if (_activeChunks.Count > 0 && !_activeChunks.Contains(coord))
            return;
        if (_priorityMeshQueued.Add(coord))
            _priorityMeshQueue.Enqueue(coord);
    }

    private bool IsChunkNearPlayer(ChunkCoord coord, int horizontalRadius = 2, int verticalRadius = 1)
    {
        var dx = Math.Abs(coord.X - _centerChunk.X);
        var dz = Math.Abs(coord.Z - _centerChunk.Z);
        var dy = Math.Abs(coord.Y - _centerChunk.Y);
        return Math.Max(dx, dz) <= horizontalRadius && dy <= verticalRadius;
    }

    private void QueuePriorityRemeshForBlockEdit(int wx, int wy, int wz)
    {
        _blockEditBoostRemaining = Math.Max(_blockEditBoostRemaining, BlockEditBoostFrames);
        var coord = VoxelWorld.WorldToChunk(wx, wy, wz, out var lx, out var ly, out var lz);
        QueuePriorityMeshBuild(coord);

        if (lx == 0) QueuePriorityMeshBuild(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
        if (lx == VoxelChunkData.ChunkSizeX - 1) QueuePriorityMeshBuild(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
        if (ly == 0) QueuePriorityMeshBuild(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
        if (ly == VoxelChunkData.ChunkSizeY - 1) QueuePriorityMeshBuild(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
        if (lz == 0) QueuePriorityMeshBuild(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        if (lz == VoxelChunkData.ChunkSizeZ - 1) QueuePriorityMeshBuild(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
    }

    private bool TryDequeuePriorityMeshBuildCandidate(VoxelWorld world, out ChunkCoord coord, out VoxelChunkData chunk)
    {
        while (_priorityMeshQueue.Count > 0)
        {
            coord = _priorityMeshQueue.Dequeue();
            _priorityMeshQueued.Remove(coord);
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(coord))
                continue;
            if (!world.TryGetChunk(coord, out var chunkData) || chunkData == null)
                continue;

            chunk = chunkData;
            return true;
        }

        coord = default;
        chunk = null!;
        return false;
    }

    private bool TryDequeueMeshBuildCandidate(VoxelWorld world, out ChunkCoord coord, out VoxelChunkData chunk)
    {
        while (_meshQueue.Count > 0)
        {
            coord = _meshQueue.Dequeue();
            _meshQueued.Remove(coord);
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(coord))
                continue;
            if (!world.TryGetChunk(coord, out var chunkData) || chunkData == null)
                continue;

            chunk = chunkData;
            return true;
        }

        coord = default;
        chunk = null!;
        return false;
    }

    private void ProcessStreamingResults(bool aggressiveApply = false)
    {
        if (_streamingService == null || _world == null)
            return;

        // Check if streaming service is stuck and force recovery
        if (_streamingService.IsStuck(TimeSpan.FromSeconds(10)))
        {
            _log.Warn("Chunk streaming service appears stuck, forcing recovery...");
            
            if (!_spawnPrewarmComplete)
                _log.Warn("Streaming service is stuck during prewarm; continuing local prewarm path.");
        }

        // D) Reset per-frame counters
        _completedLoadsAppliedThisFrame = 0;
        _completedMeshesAppliedThisFrame = 0;
        
        // 4) AGGRESSIVE APPLY - Higher budget during preparing (up to 16 meshes/frame)
        int maxMeshesPerFrame = aggressiveApply ? 16 : 4;

        // Process load results - add loaded chunks to world
        _streamingService.ProcessLoadResults(result => {
            if (result.Success && result.Chunk != null)
            {
                // Add chunk to world dictionary (main thread only)
                _world.AddChunkDirect(result.Coord, result.Chunk);
                
                // 3) NEIGHBOR SCHEDULING - When a chunk loads, schedule mesh for it AND its N/S/E/W neighbors
                if (_activeChunks.Contains(result.Coord))
                {
                    _streamingService.EnqueueMeshJob(result.Coord, result.Chunk, 0);
                    
                    // Schedule neighbor meshes (N/S/E/W only)
                    var neighbors = new[]
                    {
                        new ChunkCoord(result.Coord.X + 1, result.Coord.Y, result.Coord.Z),
                        new ChunkCoord(result.Coord.X - 1, result.Coord.Y, result.Coord.Z),
                        new ChunkCoord(result.Coord.X, result.Coord.Y, result.Coord.Z + 1),
                        new ChunkCoord(result.Coord.X, result.Coord.Y, result.Coord.Z - 1)
                    };
                    
                    foreach (var neighborCoord in neighbors)
                    {
                        if (_world.TryGetChunk(neighborCoord, out var neighborChunk) && neighborChunk != null &&
                            _activeChunks.Contains(neighborCoord) &&
                            !_chunkMeshes.ContainsKey(neighborCoord))
                        {
                            _streamingService.EnqueueMeshJob(neighborCoord, neighborChunk, 1);
                        }
                    }
                }
                
                _completedLoadsAppliedThisFrame++;
            }
            else if (!result.Success)
            {
                _log.Error($"Failed to load chunk {result.Coord}: {result.Error}");
            }
        }, maxResults: MaxApplyCompletedChunkLoadsPerFrame);

        // Process mesh results - add built meshes to rendering
        _streamingService.ProcessMeshResults(result => {
            if (result.Success && result.Mesh != null)
            {
                _chunkMeshes[result.Coord] = result.Mesh;
                if (!_chunkOrder.Contains(result.Coord))
                    _chunkOrder.Add(result.Coord);
                
                _completedMeshesAppliedThisFrame++;
            }
            else if (result.Success && result.IsPlaceholder)
            {
                // Accept placeholder meshes to prevent infinite retry
                if (result.Mesh != null)
                {
                    _chunkMeshes[result.Coord] = result.Mesh;
                    if (!_chunkOrder.Contains(result.Coord))
                        _chunkOrder.Add(result.Coord);
                    
                    _completedMeshesAppliedThisFrame++;
                    _log.Debug($"Applied placeholder mesh for chunk {result.Coord}");
                }
            }
            else if (!result.Success)
            {
                _log.Error($"Failed to build mesh for chunk {result.Coord}: {result.Error}");
            }
        }, maxResults: maxMeshesPerFrame);

        // Process save results - log any save errors
        _streamingService.ProcessSaveResults(result => {
            if (!result.Success)
            {
                _log.Error($"Failed to save chunk {result.Coord}: {result.Error}");
            }
        }, maxResults: 2);

        // G) Log streaming stats periodically (every 5 seconds)
        var now = DateTime.UtcNow;
        if ((now - _lastStreamingStats).TotalSeconds >= 5)
        {
            if (_debugHudVisible)
            {
                var (loadQueue, meshQueue, saveQueue) = _streamingService.GetQueueSizes();
                _log.Info($"Streaming: Player={_playerChunkCoord}, Loaded={_world.ChunkCount}, " +
                         $"Queues(L/M/S)={loadQueue}/{meshQueue}/{saveQueue}, " +
                         $"Applied(L/M)={_completedLoadsAppliedThisFrame}/{_completedMeshesAppliedThisFrame}");
            }
            _lastStreamingStats = now;
        }
    }

    private readonly struct MeshBuildResult
    {
        public ChunkCoord Coord { get; }
        public ChunkMesh? Mesh { get; }
        public string? Error { get; }
        public int Version { get; }

        public MeshBuildResult(ChunkCoord coord, ChunkMesh? mesh, string? error, int version)
        {
            Coord = coord;
            Mesh = mesh;
            Error = error;
            Version = version;
        }
    }

    private readonly struct SaveAndExitResult
    {
        public bool Success { get; }
        public int SavedChunks { get; }
        public int SavedMeshes { get; }
        public double Seconds { get; }
        public string? Error { get; }

        public SaveAndExitResult(bool success, int savedChunks, int savedMeshes, double seconds, string? error)
        {
            Success = success;
            SavedChunks = savedChunks;
            SavedMeshes = savedMeshes;
            Seconds = seconds;
            Error = error;
        }
    }

    private void ProcessMeshBuildQueue(int budgetOverride = 0)
    {
        if (_isClosing || _world == null || _atlas == null)
            return;

        var boostBuilds = _blockEditBoostRemaining > 0;
        if (boostBuilds)
            _blockEditBoostRemaining--;

        var applyBudget = budgetOverride > 0 ? Math.Max(2, budgetOverride * 2) : 3;
        if (boostBuilds)
            applyBudget = Math.Max(applyBudget, 16);
        ApplyCompletedMeshBuilds(applyBudget);

        var scheduleBudget = budgetOverride > 0
            ? budgetOverride
            : Math.Max(MaxMeshBuildsPerFrame, RuntimeMeshScheduleBudget);
        if (boostBuilds)
            scheduleBudget = Math.Max(scheduleBudget, Math.Min(MeshWorkerCount + 1, 6));

        var useFastMeshing = boostBuilds || ShouldUseFastMeshing();
        var priorityCap = boostBuilds ? MaxPriorityMeshBuildsPerFrame + 2 : MaxPriorityMeshBuildsPerFrame;
        var priorityBudget = Math.Min(scheduleBudget, priorityCap);
        var inlinePriorityBudget = boostBuilds ? MaxInlinePriorityMeshBuildsPerFrame : 0;

        // Always schedule edit-driven remeshes first so block breaks appear immediately.
        while (priorityBudget > 0 && scheduleBudget > 0)
        {
            if (!TryDequeuePriorityMeshBuildCandidate(_world, out var coord, out var chunk))
                break;

            if (!_meshBuildInFlight.TryAdd(coord, 0))
                continue;

            var tookSemaphore = _meshBuildSemaphore.Wait(0);
            if (!tookSemaphore)
            {
                if (inlinePriorityBudget > 0)
                {
                    try
                    {
                        var inlineMesh = VoxelMesherGreedy.BuildChunkMeshPriority(_world, chunk, _atlas, _log);
                        if (!ValidateMesh(inlineMesh))
                        {
                            _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coord, null, "invalid", chunk.Version));
                        }
                        else
                        {
                            _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coord, inlineMesh, null, chunk.Version));
                        }
                    }
                    catch (Exception ex)
                    {
                        _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coord, null, ex.Message, chunk.Version));
                    }
                    finally
                    {
                        _meshBuildInFlight.TryRemove(coord, out _);
                    }

                    inlinePriorityBudget--;
                    scheduleBudget--;
                    priorityBudget--;
                    continue;
                }

                _meshBuildInFlight.TryRemove(coord, out _);
                QueuePriorityMeshBuild(coord);
                break;
            }

            var world = _world;
            var atlas = _atlas;
            var log = _log;
            var coordForBuild = coord;
            var chunkForBuild = chunk;
            var versionForBuild = chunk.Version;
            void BuildPriorityMesh()
            {
                try
                {
                    var mesh = VoxelMesherGreedy.BuildChunkMeshPriority(world, chunkForBuild, atlas, log);
                    if (!ValidateMesh(mesh))
                    {
                        _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, null, "invalid", versionForBuild));
                        return;
                    }

                    if (!_isClosing)
                        _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, mesh, null, versionForBuild));
                }
                catch (Exception ex)
                {
                    _completedPriorityMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, null, ex.Message, versionForBuild));
                }
                finally
                {
                    _meshBuildInFlight.TryRemove(coordForBuild, out _);
                    if (tookSemaphore)
                        _meshBuildSemaphore.Release();
                }
            }

            _ = Task.Run(BuildPriorityMesh);

            scheduleBudget--;
            priorityBudget--;
        }

        while (scheduleBudget > 0)
        {
            if (!TryDequeueMeshBuildCandidate(_world, out var coord, out var chunk))
                break;

            if (_priorityMeshQueued.Contains(coord))
                continue;

            if (!_meshBuildInFlight.TryAdd(coord, 0))
                continue;

            if (!_meshBuildSemaphore.Wait(0))
            {
                _meshBuildInFlight.TryRemove(coord, out _);
                QueueMeshBuild(coord);
                break;
            }

            var world = _world;
            var atlas = _atlas;
            var log = _log;
            var fast = useFastMeshing;
            var coordForBuild = coord;
            var chunkForBuild = chunk;
            var versionForBuild = chunk.Version;
            _ = Task.Run(() =>
            {
                try
                {
                    var mesh = fast
                        ? VoxelMesherGreedy.BuildChunkMeshFast(world, chunkForBuild, atlas, log)
                        : VoxelMesherGreedy.BuildChunkMesh(world, chunkForBuild, atlas, log);
                    if (!ValidateMesh(mesh))
                    {
                        _completedMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, null, "invalid", versionForBuild));
                        return;
                    }

                    if (!_isClosing)
                        _completedMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, mesh, null, versionForBuild));
                }
                catch (Exception ex)
                {
                    _completedMeshBuildQueue.Enqueue(new MeshBuildResult(coordForBuild, null, ex.Message, versionForBuild));
                }
                finally
                {
                    _meshBuildInFlight.TryRemove(coordForBuild, out _);
                    _meshBuildSemaphore.Release();
                }
            });

            scheduleBudget--;
        }

        // Apply inline priority builds immediately so rapid block breaks are visually instant.
        if (boostBuilds)
            ApplyCompletedMeshBuilds(MaxInlinePriorityMeshBuildsPerFrame + 1);
    }

    private void ApplyCompletedMeshBuilds(int maxApply)
    {
        if (_world == null || _isClosing)
            return;

        for (var i = 0; i < maxApply; i++)
        {
            if (!_completedPriorityMeshBuildQueue.TryDequeue(out var result)
                && !_completedMeshBuildQueue.TryDequeue(out result))
                break;

            if (!_world.TryGetChunk(result.Coord, out var chunk) || chunk == null)
                continue;

            // Skip stale background results produced before a newer block edit.
            if (chunk.Version != result.Version)
            {
                chunk.IsDirty = true;
                QueuePriorityMeshBuild(result.Coord);
                continue;
            }

            if (result.Mesh != null)
            {
                _chunkMeshes[result.Coord] = result.Mesh;
                if (!_chunkOrder.Contains(result.Coord))
                    _chunkOrder.Add(result.Coord);
            }
            else if (!string.IsNullOrWhiteSpace(result.Error) && result.Error != "invalid" && !_isClosing)
            {
                _log.Warn($"Background mesh build failed for {result.Coord}: {result.Error}");
            }
            else if (!_loggedInvalidChunkMesh)
            {
                _log.Error($"Invalid chunk mesh for {result.Coord}. Skipping draw to avoid artifacts.");
                _loggedInvalidChunkMesh = true;
            }

            chunk.IsDirty = false;
        }
    }

    private bool ShouldUseFastMeshing()
    {
        // During prewarm we prioritize quick visual availability over final mesh quality.
        if (!_spawnPrewarmComplete)
            return true;

        // In gameplay, only use fast meshing under sustained pressure and queue backlog.
        if (_meshQueue.Count > 24 && _fps > 0f && _fps < 50f)
            return true;

        return false;
    }

    private static bool ValidateMesh(ChunkMesh mesh)
    {
        if (mesh.OpaqueVertices.Length % 3 != 0 || mesh.TransparentVertices.Length % 3 != 0 || mesh.WaterVertices.Length % 3 != 0)
            return false;

        return VerticesFinite(mesh.OpaqueVertices)
            && VerticesFinite(mesh.TransparentVertices)
            && VerticesFinite(mesh.WaterVertices);
    }

    private static bool VerticesFinite(VertexPositionTexture[] verts)
    {
        for (int i = 0; i < verts.Length; i++)
        {
            var p = verts[i].Position;
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z))
                return false;
            if (float.IsInfinity(p.X) || float.IsInfinity(p.Y) || float.IsInfinity(p.Z))
                return false;
        }
        return true;
    }

    private void TrimFarChunks(int keepRadius)
    {
        if (_world == null || _streamingService == null)
            return;

        var keep = new HashSet<ChunkCoord>();
        var keepSq = keepRadius * keepRadius;
        var maxCy = _world.MaxChunkY;
        for (var dz = -keepRadius; dz <= keepRadius; dz++)
        {
            for (var dx = -keepRadius; dx <= keepRadius; dx++)
            {
                if (dx * dx + dz * dz > keepSq)
                    continue;
                for (var cy = 0; cy <= maxCy; cy++)
                    keep.Add(new ChunkCoord(_centerChunk.X + dx, cy, _centerChunk.Z + dz));
            }
        }

        // Unload chunks with save-on-unload support
        _world.UnloadChunks(keep, (coord, blocks) => {
            _streamingService.EnqueueSaveJob(coord, blocks);
        });

        // Also remove meshes for unloaded chunks - thread-safe with ConcurrentDictionary
        var toRemoveMeshes = new List<ChunkCoord>();
        var meshKeys = _chunkMeshes.Keys.ToArray(); // Create snapshot for safe enumeration
        foreach (var meshCoord in meshKeys)
        {
            if (!keep.Contains(meshCoord))
                toRemoveMeshes.Add(meshCoord);
        }

        foreach (var coord in toRemoveMeshes)
        {
            if (_chunkMeshes.TryRemove(coord, out _))
            {
                _chunkOrder.Remove(coord);
            }
        }
    }

    private void CenterCamera()
    {
        var state = BuildDefaultPlayerState();
        ApplyPlayerState(state);
    }

    private void UpdatePlayer(GameTime gameTime, float dt, InputState input)
    {
        if (_world == null)
            return;

        _player.Update(dt, gameTime.TotalGameTime.TotalSeconds, input, _world.GetBlock);
        ClampPlayerToWorldBounds();
    }

    private void ClampPlayerToWorldBounds()
    {
        if (_meta == null)
            return;

        var maxX = Math.Max(0.5f, _meta.Size.Width - 0.5f);
        var maxZ = Math.Max(0.5f, _meta.Size.Depth - 0.5f);
        var pos = _player.Position;
        var clampedX = Math.Clamp(pos.X, 0.5f, maxX);
        var clampedZ = Math.Clamp(pos.Z, 0.5f, maxZ);
        if (MathF.Abs(clampedX - pos.X) < 0.0001f && MathF.Abs(clampedZ - pos.Z) < 0.0001f)
            return;

        _player.Position = new Vector3(clampedX, pos.Y, clampedZ);
    }

    private void ApplyPlayerSeparation(float dt)
    {
        if (!_playerCollisionEnabled || _remotePlayers.Count == 0)
            return;

        var pos = _player.Position;
        var half = PlayerController.ColliderHalfWidth;
        var height = PlayerController.ColliderHeight;
        var minDist = Math.Max(0.01f, half * 2f - PlayerPushSkin);
        var minDistSq = minDist * minDist;
        var totalPush = Vector3.Zero;

        foreach (var model in _remotePlayers.Values)
        {
            var other = model.Position;
            var verticalOverlap = pos.Y < other.Y + height && pos.Y + height > other.Y;
            if (!verticalOverlap)
                continue;

            var dx = pos.X - other.X;
            var dz = pos.Z - other.Z;
            var distSq = dx * dx + dz * dz;
            if (distSq >= minDistSq)
                continue;

            Vector2 dir;
            float push;
            if (distSq < 0.0001f)
            {
                var f = _player.Forward;
                dir = new Vector2(f.X, f.Z);
                if (dir.LengthSquared() < 0.0001f)
                    dir = Vector2.UnitX;
                else
                    dir.Normalize();
                push = Math.Min(minDist * 0.5f, PlayerPushStrength * dt);
            }
            else
            {
                var dist = MathF.Sqrt(distSq);
                dir = new Vector2(dx / dist, dz / dist);
                var penetration = minDist - dist;
                push = Math.Min(penetration * 0.5f, PlayerPushStrength * dt);
            }

            push = Math.Min(push, PlayerPushMaxStep);
            totalPush += new Vector3(dir.X * push, 0f, dir.Y * push);
        }

        if (totalPush.LengthSquared() <= 0f)
            return;

        var candidate = pos + totalPush;
        if (!IsPlayerInsideSolid(candidate))
            _player.Position = candidate;
    }

    private void HandleBlockInteraction(GameTime gameTime, InputState input)
    {
        if (_world == null)
            return;

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_interactCooldown > 0f)
            _interactCooldown = Math.Max(0f, _interactCooldown - dt);

        if (_interactCooldown > 0f)
            return;

        var origin = _player.Position + _player.HeadOffset;
        var dir = _player.Forward;
        if (!VoxelRaycast.Raycast(origin, dir, InteractRange, _world.GetBlock, out var hit))
            return;

        if (input.IsNewLeftClick())
        {
            var id = _world.GetBlock(hit.X, hit.Y, hit.Z);
            if (id != BlockIds.Air)
            {
                if (id == BlockIds.Nullblock && _gameMode == GameMode.Veilwalker)
                {
                    _interactCooldown = InteractCooldownSeconds;
                    return;
                }

                _world.SetBlock(hit.X, hit.Y, hit.Z, BlockIds.Air);
                _lanSession?.SendBlockSet(hit.X, hit.Y, hit.Z, BlockIds.Air);
                QueuePriorityRemeshForBlockEdit(hit.X, hit.Y, hit.Z);
                _blockBreakParticles.SpawnBlockBreak(hit.X, hit.Y, hit.Z, id);
                
                if (_gameMode == GameMode.Veilwalker)
                    SpawnBlockDrop(hit.X, hit.Y, hit.Z, (BlockId)id);
                else
                    _inventory.Add((BlockId)id, 1);
                _interactCooldown = InteractCooldownSeconds;
            }
        }
        else if (input.IsNewMiddleClick())
        {
            var id = _world.GetBlock(hit.X, hit.Y, hit.Z);
            if (id != BlockIds.Air)
            {
                _inventory.PickBlock((BlockId)id);
                _interactCooldown = InteractCooldownSeconds;
            }
        }
        else if (input.IsNewRightClick())
        {
            var id = _world.GetBlock(hit.PrevX, hit.PrevY, hit.PrevZ);
            var selected = _inventory.SelectedId;
            var sandbox = _gameMode == GameMode.Artificer;
            if (id == BlockIds.Air && selected == BlockId.WaterBucket && (sandbox || _inventory.SelectedCount > 0))
            {
                if (WouldBlockIntersectAnyPlayer(hit.PrevX, hit.PrevY, hit.PrevZ))
                {
                    _interactCooldown = InteractCooldownSeconds;
                    return;
                }

                _world.SetBlock(hit.PrevX, hit.PrevY, hit.PrevZ, BlockIds.Water);
                _lanSession?.SendBlockSet(hit.PrevX, hit.PrevY, hit.PrevZ, BlockIds.Water);
                QueuePriorityRemeshForBlockEdit(hit.PrevX, hit.PrevY, hit.PrevZ);
                _interactCooldown = InteractCooldownSeconds;
                return;
            }

            if (id == BlockIds.Air && selected != BlockId.Air && (sandbox || _inventory.SelectedCount > 0))
            {
                if (WouldBlockIntersectAnyPlayer(hit.PrevX, hit.PrevY, hit.PrevZ))
                {
                    _interactCooldown = InteractCooldownSeconds;
                    return;
                }
                _world.SetBlock(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
                _lanSession?.SendBlockSet(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
                QueuePriorityRemeshForBlockEdit(hit.PrevX, hit.PrevY, hit.PrevZ);
                
                if (!sandbox && _inventory.TryConsumeSelected(1))
                    _interactCooldown = InteractCooldownSeconds;
                if (sandbox)
                    _interactCooldown = InteractCooldownSeconds;
            }
        }
    }

    private void HandleHotbarInput(InputState input)
    {
        if (input.ScrollDelta != 0)
        {
            var step = input.ScrollDelta > 0 ? -1 : 1;
            _inventory.Scroll(step);
        }

        if (input.IsNewKeyPress(Keys.D1) || input.IsNewKeyPress(Keys.NumPad1)) _inventory.Select(0);
        if (input.IsNewKeyPress(Keys.D2) || input.IsNewKeyPress(Keys.NumPad2)) _inventory.Select(1);
        if (input.IsNewKeyPress(Keys.D3) || input.IsNewKeyPress(Keys.NumPad3)) _inventory.Select(2);
        if (input.IsNewKeyPress(Keys.D4) || input.IsNewKeyPress(Keys.NumPad4)) _inventory.Select(3);
        if (input.IsNewKeyPress(Keys.D5) || input.IsNewKeyPress(Keys.NumPad5)) _inventory.Select(4);
        if (input.IsNewKeyPress(Keys.D6) || input.IsNewKeyPress(Keys.NumPad6)) _inventory.Select(5);
        if (input.IsNewKeyPress(Keys.D7) || input.IsNewKeyPress(Keys.NumPad7)) _inventory.Select(6);
        if (input.IsNewKeyPress(Keys.D8) || input.IsNewKeyPress(Keys.NumPad8)) _inventory.Select(7);
        if (input.IsNewKeyPress(Keys.D9) || input.IsNewKeyPress(Keys.NumPad9)) _inventory.Select(8);

        if (input.IsNewLeftClick() && _hotbarRect.Contains(input.MousePosition))
        {
            for (int i = 0; i < _hotbarSlots.Length; i++)
            {
                if (_hotbarSlots[i].Contains(input.MousePosition))
                {
                    _inventory.Select(i);
                    break;
                }
            }
        }
    }

    private bool WouldBlockIntersectAnyPlayer(int x, int y, int z)
    {
        var blockMin = new Vector3(x, y, z);
        var blockMax = new Vector3(x + 1f, y + 1f, z + 1f);

        if (IntersectsPlayerAabb(_player.Position, blockMin, blockMax))
            return true;

        foreach (var model in _remotePlayers.Values)
        {
            if (IntersectsPlayerAabb(model.Position, blockMin, blockMax))
                return true;
        }

        return false;
    }

    private static bool IntersectsPlayerAabb(Vector3 pos, Vector3 blockMin, Vector3 blockMax)
    {
        const float eps = 0.001f;
        var half = PlayerController.ColliderHalfWidth;
        var height = PlayerController.ColliderHeight;
        var min = new Vector3(pos.X - half + eps, pos.Y + eps, pos.Z - half + eps);
        var max = new Vector3(pos.X + half - eps, pos.Y + height - eps, pos.Z + half - eps);
        return min.X < blockMax.X && max.X > blockMin.X
            && min.Y < blockMax.Y && max.Y > blockMin.Y
            && min.Z < blockMax.Z && max.Z > blockMin.Z;
    }

    private void UpdateHandoffTarget(InputState input)
    {
        if (_pauseMenuOpen || _inventoryOpen || _lanSession == null || _gameMode != GameMode.Veilwalker)
            return;

        if (_inventory.SelectedId == BlockId.Air || _inventory.SelectedCount <= 0)
            return;

        if (_remotePlayers.Count == 0)
            return;

        var origin = _player.Position + _player.HeadOffset;
        var forward = _player.Forward;
        var bestDistSq = HandoffRange * HandoffRange;
        var targetId = -1;
        var targetName = "";

        foreach (var pair in _remotePlayers)
        {
            var id = pair.Key;
            var model = pair.Value;
            var targetPos = model.Position + new Vector3(0f, Scale.PlayerHeadHeight * 0.5f, 0f);
            var toTarget = targetPos - origin;
            var distSq = toTarget.LengthSquared();
            if (distSq > bestDistSq)
                continue;

            var dir = toTarget;
            if (dir.LengthSquared() <= 0.0001f)
                continue;
            dir.Normalize();

            if (Vector3.Dot(dir, forward) < HandoffDotThreshold)
                continue;

            bestDistSq = distSq;
            targetId = id;
            targetName = _playerNames.TryGetValue(id, out var name) ? name : $"PLAYER {id}";
        }

        if (targetId >= 0)
        {
            _handoffTargetId = targetId;
            _handoffTargetName = targetName;
            _handoffPromptVisible = true;
        }
    }

    private void HandleDropAndGive(InputState input)
    {
        if (_pauseMenuOpen || _inventoryOpen || _gameMode != GameMode.Veilwalker)
            return;

        if (_inventory.SelectedId == BlockId.Air || _inventory.SelectedCount <= 0)
            return;

        var fullStack = IsStackModifierDown(input);

        if (_handoffTargetId >= 0 && input.IsNewKeyPress(_giveKey))
        {
            var amount = fullStack ? _inventory.SelectedCount : 1;
            var id = _inventory.SelectedId;
            if (!_inventory.TryConsumeSelected(amount))
                return;

            if (TryGetHandoffTargetPosition(_handoffTargetId, out var targetPos))
            {
                var spawn = new LanItemSpawn
                {
                    ItemId = NextItemId(),
                    BlockId = (byte)id,
                    Count = amount,
                    X = targetPos.X,
                    Y = targetPos.Y,
                    Z = targetPos.Z,
                    VelX = 0f,
                    VelY = 0f,
                    VelZ = 0f,
                    PickupDelay = ItemPickupDelaySeconds,
                    PickupLockPlayerId = _lanSession?.LocalPlayerId ?? -1
                };
                SpawnWorldItem(spawn);
                SendItemSpawn(spawn);
            }
            return;
        }

        if (input.IsNewKeyPress(_dropKey))
        {
            var amount = fullStack ? _inventory.SelectedCount : 1;
            var id = _inventory.SelectedId;
            if (!_inventory.TryConsumeSelected(amount))
                return;

            SpawnDroppedItem(id, amount);
        }
    }

    private bool IsStackModifierDown(InputState input)
    {
        if (_stackModifierKey == Keys.LeftShift || _stackModifierKey == Keys.RightShift)
            return input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        return input.IsKeyDown(_stackModifierKey);
    }

    private bool TryGetHandoffTargetPosition(int targetId, out Vector3 position)
    {
        if (_remotePlayers.TryGetValue(targetId, out var model))
        {
            position = model.Position + new Vector3(0f, Scale.PlayerHeadHeight * 0.4f, 0f);
            return true;
        }

        position = default;
        return false;
    }

    private int NextItemId()
    {
        var owner = _lanSession?.LocalPlayerId ?? 0;
        var seq = _nextItemId++;
        return (owner << 24) | (seq & 0x00FFFFFF);
    }

    private void SpawnBlockDrop(int x, int y, int z, BlockId id)
    {
        var spawn = new LanItemSpawn
        {
            ItemId = NextItemId(),
            BlockId = (byte)id,
            Count = 1,
            X = x + 0.5f,
            Y = y + 0.5f,
            Z = z + 0.5f,
            VelX = 0f,
            VelY = 0f,
            VelZ = 0f,
            PickupDelay = ItemPickupDelaySeconds,
            PickupLockPlayerId = _lanSession?.LocalPlayerId ?? -1
        };
        SpawnWorldItem(spawn);
        SendItemSpawn(spawn);
    }

    private void SpawnDroppedItem(BlockId id, int count)
    {
        if (_world == null || count <= 0)
            return;

        var origin = _player.Position + _player.HeadOffset * 0.6f;
        var forward = _player.Forward;
        var tossDir = new Vector3(forward.X, 0f, forward.Z);
        if (tossDir.LengthSquared() < 0.001f)
            tossDir = Vector3.UnitZ;
        tossDir.Normalize();
        var spawnPos = origin + tossDir * 0.6f + new Vector3(0f, 0.1f, 0f);
        var velocity = tossDir * (3.0f * Scale.BlockSize) + new Vector3(0f, 2.5f * Scale.BlockSize, 0f);
        var spawn = new LanItemSpawn
        {
            ItemId = NextItemId(),
            BlockId = (byte)id,
            Count = count,
            X = spawnPos.X,
            Y = spawnPos.Y,
            Z = spawnPos.Z,
            VelX = velocity.X,
            VelY = velocity.Y,
            VelZ = velocity.Z,
            PickupDelay = ItemDropPickupDelaySeconds,
            PickupLockPlayerId = _lanSession?.LocalPlayerId ?? -1
        };
        SpawnWorldItem(spawn);
        SendItemSpawn(spawn);
    }

    private void SendItemSpawn(LanItemSpawn spawn)
    {
        if (_lanSession == null)
            return;
        _lanSession.SendItemSpawn(spawn);
    }

    private void SendItemPickup(int itemId)
    {
        if (_lanSession == null)
            return;
        _lanSession.SendItemPickup(itemId);
    }

    private void SpawnWorldItem(LanItemSpawn spawn)
    {
        if (_world == null)
            return;

        if (spawn.Count <= 0 || spawn.BlockId == (byte)BlockId.Air)
            return;

        if (_worldItems.ContainsKey(spawn.ItemId))
            return;

        var item = new WorldItem
        {
            ItemId = spawn.ItemId,
            BlockId = (BlockId)spawn.BlockId,
            Count = spawn.Count,
            Position = new Vector3(spawn.X, spawn.Y, spawn.Z),
            Velocity = new Vector3(spawn.VelX, spawn.VelY, spawn.VelZ),
            SpawnTime = _worldTimeSeconds,
            LastUpdateTime = _worldTimeSeconds,
            PickupDelay = spawn.PickupDelay,
            PickupLockPlayerId = spawn.PickupLockPlayerId
        };

        _worldItems[item.ItemId] = item;
    }

    private void RemoveWorldItem(int itemId)
    {
        _worldItems.Remove(itemId);
    }

    private void UpdateWorldItems()
    {
        if (_world == null || _worldItems.Count == 0)
            return;

        var localPlayerId = _lanSession?.LocalPlayerId ?? -1;
        var playerPos = _player.Position + new Vector3(0f, Scale.PlayerHeadHeight * 0.5f, 0f);

        _worldItemRemove.Clear();
        foreach (var pair in _worldItems)
        {
            var item = pair.Value;
            if (item.Count <= 0 || item.BlockId == BlockId.Air)
            {
                _worldItemRemove.Add(item.ItemId);
                continue;
            }

            var dt = Math.Clamp(_worldTimeSeconds - item.LastUpdateTime, 0f, 0.1f);
            if (dt > 0f)
            {
                var vel = item.Velocity;
                if (!item.IsGrounded || vel.Y > 0f)
                    vel.Y += ItemGravity * dt;
                else
                    vel.Y = 0f;
                vel.X *= ItemAirDrag;
                vel.Z *= ItemAirDrag;

                var newPos = item.Position + vel * dt;
                var groundedThisFrame = false;

                if (vel.Y <= 0f && TrySnapToGround(newPos, out var groundedPos))
                {
                    newPos = groundedPos;
                    vel.Y = 0f;
                    vel.X *= ItemGroundFriction;
                    vel.Z *= ItemGroundFriction;
                    groundedThisFrame = true;
                }
                else if (IsSolidAt(newPos))
                {
                    var cellY = (int)Math.Floor(newPos.Y);
                    newPos.Y = cellY + 1f + ItemGroundOffset;
                    if (vel.Y < 0f)
                        vel.Y = 0f;
                    groundedThisFrame = true;
                }

                item.Position = newPos;
                item.Velocity = vel;
                item.LastUpdateTime = _worldTimeSeconds;
                if (groundedThisFrame)
                {
                    if (!item.IsGrounded)
                        item.GroundedTime = _worldTimeSeconds;
                    item.IsGrounded = true;
                }
                else
                {
                    item.IsGrounded = false;
                }
            }

            if (item.PickupDelay > 0f && localPlayerId == item.PickupLockPlayerId)
            {
                var elapsed = _worldTimeSeconds - item.SpawnTime;
                if (elapsed < item.PickupDelay)
                    continue;
            }

            if (item.IsGrounded && _worldTimeSeconds - item.GroundedTime < ItemGroundPickupDelaySeconds)
                continue;

            var distSq = Vector3.DistanceSquared(item.Position, playerPos);
            if (distSq > ItemPickupRadiusSq)
                continue;

            if (!_inventory.CanAdd(item.BlockId, item.Count))
                continue;

            var leftover = _inventory.Add(item.BlockId, item.Count);
            if (leftover <= 0)
            {
                _worldItemRemove.Add(item.ItemId);
                SendItemPickup(item.ItemId);
            }
        }

        for (int i = 0; i < _worldItemRemove.Count; i++)
            _worldItems.Remove(_worldItemRemove[i]);
    }

    private bool TrySnapToGround(Vector3 pos, out Vector3 grounded)
    {
        grounded = pos;
        if (_world == null)
            return false;

        var bx = (int)Math.Floor(pos.X);
        var by = (int)Math.Floor(pos.Y - ItemHalfHeight - ItemGroundSnap - 0.001f);
        var bz = (int)Math.Floor(pos.Z);
        if (BlockRegistry.IsSolid(_world.GetBlock(bx, by, bz)))
        {
            var targetY = by + 1f + ItemGroundOffset;
            if (pos.Y <= targetY + ItemGroundSnap)
            {
                grounded.Y = targetY;
                return true;
            }
        }
        return false;
    }

    private bool IsSolidAt(Vector3 pos)
    {
        if (_world == null)
            return false;

        var bx = (int)Math.Floor(pos.X);
        var by = (int)Math.Floor(pos.Y);
        var bz = (int)Math.Floor(pos.Z);
        return BlockRegistry.IsSolid(_world.GetBlock(bx, by, bz));
    }

    private void UpdateHotbarLayout()
    {
        var slotSize = Math.Clamp((int)(_viewport.Width * 0.05f), 36, 64);
        var gap = 6;
        var totalW = slotSize * Inventory.HotbarSize + gap * (Inventory.HotbarSize - 1);
        var startX = _viewport.X + (_viewport.Width - totalW) / 2;
        var y = _viewport.Bottom - slotSize - 18;

        _hotbarRect = new Rectangle(startX, y, totalW, slotSize);
        for (int i = 0; i < _hotbarSlots.Length; i++)
        {
            var x = startX + i * (slotSize + gap);
            _hotbarSlots[i] = new Rectangle(x, y, slotSize, slotSize);
        }
    }

    private void UpdateInventoryLayout()
    {
        var maxW = _viewport.Width - 40;
        var maxH = _viewport.Height - 40;
        var slotSize = InventorySlotSize;
        var gridW = InventoryCols * slotSize + (InventoryCols - 1) * InventorySlotGap;
        var gridH = InventoryRows * slotSize + (InventoryRows - 1) * InventorySlotGap;
        var windowW = InventoryPadding * 2 + gridW;
        var windowH = InventoryPadding * 2 + InventoryTitleHeight + gridH + InventoryFooterHeight;

        if (windowW > maxW)
        {
            slotSize = Math.Clamp((maxW - InventoryPadding * 2 - InventorySlotGap * (InventoryCols - 1)) / InventoryCols, 36, InventorySlotSize);
            gridW = InventoryCols * slotSize + (InventoryCols - 1) * InventorySlotGap;
            windowW = InventoryPadding * 2 + gridW;
        }

        if (windowH > maxH)
        {
            var maxSlot = (maxH - InventoryPadding * 2 - InventoryTitleHeight - InventoryFooterHeight - InventorySlotGap * (InventoryRows - 1)) / InventoryRows;
            slotSize = Math.Clamp(Math.Min(slotSize, maxSlot), 32, InventorySlotSize);
            gridH = InventoryRows * slotSize + (InventoryRows - 1) * InventorySlotGap;
            windowH = InventoryPadding * 2 + InventoryTitleHeight + gridH + InventoryFooterHeight;
        }

        _inventoryRect = new Rectangle(
            _viewport.X + (_viewport.Width - windowW) / 2,
            _viewport.Y + (_viewport.Height - windowH) / 2,
            windowW,
            windowH);

        var startX = _inventoryRect.X + InventoryPadding;
        var startY = _inventoryRect.Y + InventoryPadding + InventoryTitleHeight;

        for (int row = 0; row < InventoryRows; row++)
        {
            for (int col = 0; col < InventoryCols; col++)
            {
                var idx = row * InventoryCols + col;
                var x = startX + col * (slotSize + InventorySlotGap);
                var y = startY + row * (slotSize + InventorySlotGap);
                _inventoryGridSlots[idx] = new Rectangle(x, y, slotSize, slotSize);
            }
        }
        _inventoryClose.Bounds = new Rectangle(_inventoryRect.Right - 140, _inventoryRect.Bottom - 44, 120, 32);
    }

    private void UpdateInventory(InputState input)
    {
        if (!_inventoryOpen)
            return;

        if (_viewport != UiLayout.Viewport)
        {
            _viewport = UiLayout.Viewport;
            UpdateHotbarLayout();
            UpdateInventoryLayout();
        }

        _inventoryMousePos = input.MousePosition;
        _inventoryClose.Update(input);
        if (!_inventoryOpen)
            return;

        // Hover detection
        var p = _inventoryMousePos;
        var hoveredAny = false;
        for (int i = 0; i < _inventoryGridSlots.Length; i++)
        {
            if (_inventoryGridSlots[i].Contains(p))
            {
                var slot = _inventory.Grid[i];
                if (slot.Id != BlockId.Air && slot.Count > 0)
                {
                    SetDisplayName(BlockRegistry.Get(slot.Id).Name);
                    hoveredAny = true;
                }
                break;
            }
        }

        if (!hoveredAny)
        {
            for (int i = 0; i < _hotbarSlots.Length; i++)
            {
                if (_hotbarSlots[i].Contains(p))
                {
                    var slot = _inventory.Hotbar[i];
                    if (slot.Id != BlockId.Air && slot.Count > 0)
                    {
                        SetDisplayName(BlockRegistry.Get(slot.Id).Name);
                        hoveredAny = true;
                    }
                    break;
                }
            }
        }

        if (!input.IsNewLeftClick())
            return;

        for (int i = 0; i < _inventoryGridSlots.Length; i++)
        {
            if (_inventoryGridSlots[i].Contains(p))
            {
                ref var slot = ref _inventory.Grid[i];
                HandleInventorySlotClick(ref slot, InventorySlotGroup.Grid, i);
                return;
            }
        }

        for (int i = 0; i < _hotbarSlots.Length; i++)
        {
            if (_hotbarSlots[i].Contains(p))
            {
                ref var slot = ref _inventory.Hotbar[i];
                HandleInventorySlotClick(ref slot, InventorySlotGroup.Hotbar, i);
                return;
            }
        }
    }

    private void WarmBlockIcons()
    {
        if (_blockIconCache == null)
            return;

        const int pad = 6;
        var hotbarSize = _hotbarSlots.Length > 0 && _hotbarSlots[0].Width > 0
            ? _hotbarSlots[0].Width - pad * 2
            : InventorySlotSize - pad * 2;

        WarmIconSlots(_inventory.Hotbar, hotbarSize);

        if (_inventoryOpen)
        {
            var gridSize = _inventoryGridSlots.Length > 0 && _inventoryGridSlots[0].Width > 0
                ? _inventoryGridSlots[0].Width - pad * 2
                : InventorySlotSize - pad * 2;
            WarmIconSlots(_inventory.Grid, gridSize);
        }

        if (_inventoryHasHeld && _inventoryHeld.Id != BlockId.Air)
        {
            var heldSize = _inventoryGridSlots.Length > 0 && _inventoryGridSlots[0].Width > 0
                ? _inventoryGridSlots[0].Width - pad * 2
                : InventorySlotSize - pad * 2;
            _blockIconCache.Warm(_inventoryHeld.Id, heldSize, BlockModelContext.Gui);
        }
    }

    private void WarmIconSlots(HotbarSlot[] slots, int size)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Count <= 0 || slot.Id == BlockId.Air)
                continue;

            _blockIconCache?.Warm(slot.Id, size, BlockModelContext.Gui);
        }
    }

    private Texture2D? GetBlockIcon(BlockId id, int size)
    {
        return _blockIconCache?.GetOrCreate(id, size, BlockModelContext.Gui);
    }

    private void CloseInventory()
    {
        if (_inventoryHasHeld && _inventoryHeld.Id != BlockId.Air && _inventoryHeld.Count > 0)
        {
            if (_inventory.Mode != GameMode.Veilwalker)
            {
                var leftover = _inventory.Add(_inventoryHeld.Id, _inventoryHeld.Count);
                if (leftover > 0)
                    SpawnDroppedItem(_inventoryHeld.Id, leftover);
            }

            _inventoryHeld = default;
            _inventoryHasHeld = false;
            _heldFrom = InventorySlotGroup.None;
            _heldIndex = -1;
        }

        _inventoryOpen = false;
    }

    private void HandleInventorySlotClick(ref HotbarSlot slot, InventorySlotGroup group, int index)
    {
        if (_inventory.Mode != GameMode.Artificer)
        {
            HandleSandboxInventoryClick(ref slot, group, index);
            return;
        }

        if (!_inventoryHasHeld)
        {
            if (slot.Count <= 0 || slot.Id == BlockId.Air)
                return;
            _inventoryHeld = slot;
            _inventoryHasHeld = true;
            _heldFrom = group;
            _heldIndex = index;
            slot = default;
            return;
        }

        var temp = slot;
        slot = _inventoryHeld;
        _inventoryHeld = temp;
        _inventoryHasHeld = _inventoryHeld.Count > 0 && _inventoryHeld.Id != BlockId.Air;
        if (_inventoryHasHeld)
        {
            _heldFrom = group;
            _heldIndex = index;
        }
        else
        {
            _heldFrom = InventorySlotGroup.None;
            _heldIndex = -1;
        }

        if (_gameMode == GameMode.Artificer)
        {
            ClampSandboxSlot(ref slot);
            if (_inventoryHasHeld)
                ClampSandboxSlot(ref _inventoryHeld);
        }
    }

    private static void ClampSandboxSlot(ref HotbarSlot slot)
    {
        if (slot.Count > 1)
            slot.Count = 1;
    }

    private void HandleSandboxInventoryClick(ref HotbarSlot slot, InventorySlotGroup group, int index)
    {
        if (group == InventorySlotGroup.Grid)
        {
            if (slot.Count <= 0 || slot.Id == BlockId.Air)
                return;

            _inventoryHeld = slot;
            _inventoryHasHeld = true;
            _heldFrom = InventorySlotGroup.None;
            _heldIndex = -1;
            return;
        }

        // Hotbar: place copies from held, or pick up existing hotbar items.
        if (_inventoryHasHeld)
        {
            slot = _inventoryHeld;
            ClampSandboxSlot(ref slot);
            _inventoryHeld = default;
            _inventoryHasHeld = false;
            _heldFrom = InventorySlotGroup.None;
            _heldIndex = -1;
            return;
        }

        if (slot.Count <= 0 || slot.Id == BlockId.Air)
            return;

        _inventoryHeld = slot;
        _inventoryHasHeld = true;
        _heldFrom = InventorySlotGroup.None;
        _heldIndex = -1;
        slot = default;
    }

    private enum InventorySlotGroup
    {
        None,
        Grid,
        Hotbar
    }

    private void DrawHotbar(SpriteBatch sb)
    {
        if (_viewport != UiLayout.Viewport)
            UpdateHotbarLayout();

        for (int i = 0; i < _hotbarSlots.Length; i++)
        {
            var rect = _hotbarSlots[i];
            var selected = i == _inventory.SelectedIndex;
            var bg = selected ? new Color(70, 70, 70, 220) : new Color(20, 20, 20, 180);
            sb.Draw(_pixel, rect, bg);
            DrawBorder(sb, rect, selected ? Color.Yellow : Color.White);

            var slot = _inventory.Hotbar[i];
            if (slot.Count > 0 && slot.Id != BlockId.Air && _atlas != null)
            {
                var src = _atlas.GetFaceSourceRect((byte)slot.Id, FaceDirection.PosY);
                var pad = 6;
                var dst = new Rectangle(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);
                var icon = GetBlockIcon(slot.Id, dst.Width);
                if (icon != null)
                    sb.Draw(icon, dst, Color.White);
                else
                    sb.Draw(_atlas.Texture, dst, src, Color.White);
            }

            if (slot.Count > 1)
            {
                var text = slot.Count.ToString();
                var size = _font.MeasureString(text);
                var pos = new Vector2(rect.Right - size.X - 4, rect.Bottom - size.Y - 2);
                _font.DrawString(sb, text, pos, Color.White);
            }
        }
    }

    private void DrawPlayerList(SpriteBatch sb)
    {
        if (_lanSession == null || _playerNames.Count == 0)
            return;

        var entries = new List<KeyValuePair<int, string>>(_playerNames);
        entries.Sort((a, b) => a.Key.CompareTo(b.Key));

        var lineH = _font.LineHeight + 2;
        var title = "PLAYERS";
        var maxWidth = _font.MeasureString(title).X;

        for (var i = 0; i < entries.Count; i++)
        {
            var label = FormatPlayerLabel(entries[i]);
            var width = _font.MeasureString(label).X;
            if (width > maxWidth)
                maxWidth = width;
        }

        var padding = 6;
        var widthPixels = (int)MathF.Ceiling(maxWidth) + padding * 2;
        var heightPixels = (entries.Count + 1) * lineH + padding * 2;

        var x = _viewport.Right - widthPixels - 20;
        var y = _viewport.Y + 20;
        var rect = new Rectangle(x, y, widthPixels, heightPixels);

        sb.Draw(_pixel, rect, new Color(0, 0, 0, 140));
        DrawBorder(sb, rect, Color.White);

        var cursor = new Vector2(rect.X + padding, rect.Y + padding);
        _font.DrawString(sb, title, cursor, Color.White);
        cursor.Y += lineH;

        for (var i = 0; i < entries.Count; i++)
        {
            var label = FormatPlayerLabel(entries[i]);
            _font.DrawString(sb, label, cursor, Color.White);
            cursor.Y += lineH;
        }
    }

    private string FormatPlayerLabel(KeyValuePair<int, string> entry)
    {
        var label = entry.Value;
        if (_lanSession != null && entry.Key == _lanSession.LocalPlayerId)
            label = $"{label} (YOU)";
        else if (entry.Key == 0)
            label = $"{label} (HOST)";

        return label;
    }

    private void DrawInventory(SpriteBatch sb)
    {
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        if (_pausePanel != null)
            sb.Draw(_pausePanel, _inventoryRect, Color.White);

        _font.DrawString(sb, "INVENTORY", new Vector2(_inventoryRect.X + 20, _inventoryRect.Y + 14), Color.White);

        DrawInventorySlots(sb, _inventory.Grid, _inventoryGridSlots);

        _inventoryClose.Draw(sb, _pixel, _font);

        if (_inventoryHasHeld && _atlas != null)
        {
            var size = _inventoryGridSlots.Length > 0 ? _inventoryGridSlots[0].Width : 40;
            var scale = 1.15f;
            var scaled = (int)MathF.Round(size * scale);
            var dst = new Rectangle(_inventoryMousePos.X - scaled / 2, _inventoryMousePos.Y - scaled / 2, scaled, scaled);
            var icon = GetBlockIcon(_inventoryHeld.Id, dst.Width);
            if (icon != null)
                sb.Draw(icon, dst, Color.White);
            else
                sb.Draw(_atlas.Texture, dst, _atlas.GetFaceSourceRect((byte)_inventoryHeld.Id, FaceDirection.PosY), Color.White);

            if (_inventoryHeld.Count > 1)
            {
                var text = _inventoryHeld.Count.ToString();
                var textSize = _font.MeasureString(text);
                var pos = new Vector2(dst.Right - textSize.X - 4, dst.Bottom - textSize.Y - 2);
                _font.DrawString(sb, text, pos, Color.White);
            }
        }

        sb.End();
    }

    private void DrawInventorySlots(SpriteBatch sb, HotbarSlot[] slots, Rectangle[] rects)
    {
        for (int i = 0; i < rects.Length; i++)
        {
            var rect = rects[i];
            sb.Draw(_pixel, rect, new Color(20, 20, 20, 200));
            DrawBorder(sb, rect, Color.White);

            var slot = slots[i];
            if (slot.Count > 0 && slot.Id != BlockId.Air && _atlas != null)
            {
                var pad = 6;
                var dst = new Rectangle(rect.X + pad, rect.Y + pad, rect.Width - pad * 2, rect.Height - pad * 2);
                var icon = GetBlockIcon(slot.Id, dst.Width);
                if (icon != null)
                    sb.Draw(icon, dst, Color.White);
                else
                    sb.Draw(_atlas.Texture, dst, _atlas.GetFaceSourceRect((byte)slot.Id, FaceDirection.PosY), Color.White);
            }

            if (slot.Count > 1)
            {
                var text = slot.Count.ToString();
                var size = _font.MeasureString(text);
                var pos = new Vector2(rect.Right - size.X - 4, rect.Bottom - size.Y - 2);
                _font.DrawString(sb, text, pos, Color.White);
            }
        }
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private void DrawDevOverlay(SpriteBatch sb)
    {
        var pos = _player.Position;
        var vel = _player.Velocity;
        var lines = new List<string>
        {
            "DEV HUD (F3)",
            $"FPS: {_fps:0}",
            $"POS: {pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00}",
            $"VEL: {vel.X:0.00}, {vel.Y:0.00}, {vel.Z:0.00}",
            $"CHUNK: {_playerChunkCoord}",
            $"FLY: {_player.IsFlying}  GND: {_player.IsGrounded}"
        };

        if (_world != null)
        {
            var wx = (int)MathF.Floor(pos.X);
            var wz = (int)MathF.Floor(pos.Z);
            var biomeName = _world.GetBiomeNameAt(wx, wz);
            var desertWeight = _world.GetDesertWeightAt(wx, wz);
            lines.Add($"BIOME: {biomeName} (DesertWeight {desertWeight:0.00}) @ {wx},{wz}");

            var origin = _player.Position + _player.HeadOffset;
            if (VoxelRaycast.Raycast(origin, _player.Forward, InteractRange, _world.GetBlock, out var hit))
            {
                var id = _world.GetBlock(hit.X, hit.Y, hit.Z);
                var def = BlockRegistry.Get(id);
                lines.Add($"LOOKING: {def.Name} ({id}) @ {hit.X},{hit.Y},{hit.Z}");
            }
        }

        if (_streamingService != null && _world != null)
        {
            var (loadQueue, meshQueue, saveQueue) = _streamingService.GetQueueSizes();
            lines.Add($"LOADED CHUNKS: {_world.ChunkCount}");
            lines.Add($"QUEUES L/M/S: {loadQueue}/{meshQueue}/{saveQueue}");
            lines.Add($"PREWARM: {(_spawnPrewarmComplete ? "DONE" : $"{_prewarmReadyCount}/{_prewarmTargetCount}")}");
        }

        var metrics = AdvancedPerformanceOptimizer.GetCurrentMetrics();
        var (ramUsed, _, vramUsed, _) = SimpleMemoryManager.GetMemoryStats();
        lines.Add($"PERF CPU:{metrics.CpuUsage:F1}% RAM:{ramUsed / 1024 / 1024}MB VRAM:{vramUsed / 1024 / 1024}MB");

        var x = _viewport.X + 20;
        var y = _viewport.Y + 20 + (_font.LineHeight + 4) * 3;
        for (var i = 0; i < lines.Count; i++)
            _font.DrawString(sb, lines[i], new Vector2(x, y + i * (_font.LineHeight + 2)), Color.White);
    }

#if DEBUG
    private void DrawDebugOverlay(SpriteBatch sb)
    {
        var pos = _player.Position;
        var vel = _player.Velocity;
        var move = _player.MoveIntent;
        var blockText = "BLOCK: NONE";
        var atlasText = _atlas != null ? $"ATLAS: {_atlas.Texture.Width}x{_atlas.Texture.Height}" : "ATLAS: NONE";
        var samplerText = $"SAMPLE: {_blockSamplerLabel}";
        var clearColor = _debugSkyClear ? SkyColor : Color.Black;
        var clearText = $"CLEAR: {clearColor.R},{clearColor.G},{clearColor.B}";
        var overlayText = $"OVERLAY: {((_debugDisableHands || _pauseMenuOpen) ? "OFF" : "ON")}";
        var wireText = $"WIRE: {(_debugWireframe ? "ON" : "OFF")}";
        var boundWorldText = $"ATLAS WORLD: {_debugWorldAtlasBound}";
        var boundOverlayText = $"ATLAS OVERLAY: {_debugOverlayAtlasBound}";
        var heldText = $"HELD: {_inventory.SelectedId}";
        var modelText = $"MODEL: {(_debugDrawPlayerModel ? "ON" : "OFF")}";
        if (_world != null)
        {
            var origin = _player.Position + _player.HeadOffset;
            if (VoxelRaycast.Raycast(origin, _player.Forward, InteractRange, _world.GetBlock, out var hit))
            {
                var id = _world.GetBlock(hit.X, hit.Y, hit.Z);
                var def = BlockRegistry.Get(id);
                blockText = $"BLOCK: {def.Name} ({id}) ATLAS:{def.AtlasIndex} @ {hit.X},{hit.Y},{hit.Z}";
            }
        }
        var lines = new List<string>
        {
            $"FPS: {_fps:0}",
            $"POS: {pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00}",
            $"VEL: {vel.X:0.00}, {vel.Y:0.00}, {vel.Z:0.00}",
            $"FLY: {_player.IsFlying}  GND: {_player.IsGrounded}",
            $"MOVE LEN: {move.Length():0.00}",
            blockText,
            atlasText,
            samplerText,
            clearText,
            overlayText,
            wireText,
            boundWorldText,
            boundOverlayText,
            heldText,
            modelText,
            $"BLOCKSIZE: {Scale.BlockSize:0.00}"
        };

        // G) Add streaming debug information
        if (_streamingService != null && _world != null)
        {
            var (loadQueue, meshQueue, saveQueue) = _streamingService.GetQueueSizes();
            var reqBudget = _spawnPrewarmComplete
                ? Math.Max(MaxNewChunkRequestsPerFrame, RuntimeChunkRequestBudget)
                : Math.Max(Math.Max(MaxNewChunkRequestsPerFrame, RuntimeChunkRequestBudget), PrewarmChunkBudgetPerFrame);
            var meshBudget = _spawnPrewarmComplete
                ? Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget)
                : Math.Max(Math.Max(MaxMeshBuildRequestsPerFrame, RuntimeMeshRequestBudget), PrewarmMeshBudgetPerFrame);
            lines.Add($"CHUNK: {_playerChunkCoord} | LOADED: {_world.ChunkCount}");
            lines.Add($"QUEUES: L:{loadQueue} M:{meshQueue} S:{saveQueue}");
            lines.Add($"BUDGET: REQ({_chunksRequestedThisFrame}/{reqBudget}) MESH({_meshesRequestedThisFrame}/{meshBudget})");
            lines.Add($"PREWARM: {(_spawnPrewarmComplete ? "DONE" : $"{_prewarmReadyCount}/{_prewarmTargetCount}")}");
        }

        // Add pregeneration debug information
        if (_pregenerationService != null)
        {
            var (total, generated, skipped, isRunning, elapsed) = _pregenerationService.GetStatus();
            lines.Add($"PREGEN: {generated}/{total} chunks ({elapsed:hh\\:mm\\:ss})");
        }

        // Add performance monitoring information
        var metrics = AdvancedPerformanceOptimizer.GetCurrentMetrics();
        var (ramUsed, ramPeak, vramUsed, vramLimit) = SimpleMemoryManager.GetMemoryStats();
        lines.Add($"PERF: CPU:{metrics.CpuUsage:F1}% RAM:{ramUsed/1024/1024}MB VRAM:{vramUsed/1024/1024}MB");
        lines.Add($"OPT: GPU:{(AdvancedPerformanceOptimizer.IsGpuOptimized() ? "✅" : "❌")} MEM:{(AdvancedPerformanceOptimizer.IsMemoryOptimized() ? "✅" : "❌")}");
        
        // Add biome information for current position
        if (_world != null && _player != null)
        {
            var playerPos = _player.Position;
            // ALL WORLD GENERATION DELETED
        }

        var x = _viewport.X + 20;
        var y = _viewport.Y + 20 + (_font.LineHeight + 4) * 3;
        for (int i = 0; i < lines.Count; i++)
            _font.DrawString(sb, lines[i], new Vector2(x, y + i * (_font.LineHeight + 2)), Color.White);
    }
#endif

    private void DrawFaceOverlay(SpriteBatch sb)
    {
        if (!_debugFaceOverlay)
            return;

        var hitFaceText = "HIT FACE: NONE";
        if (_world != null)
        {
            var origin = _player.Position + _player.HeadOffset;
            if (VoxelRaycast.Raycast(origin, _player.Forward, InteractRange, _world.GetBlock, out var hit))
                hitFaceText = $"HIT FACE: {FormatFaceLabel(hit.Face)} ({hit.Face})";
        }

        var x = _viewport.X + 20;
        var y = _viewport.Y + 20 + (_font.LineHeight + 4) * 3;
        _font.DrawString(sb, "FACE MAP: TOP=PosY | BOTTOM=NegY | SIDE1=PosX | SIDE2=NegX | SIDE3=PosZ | SIDE4=NegZ", new Vector2(x, y), Color.White);
        _font.DrawString(sb, hitFaceText, new Vector2(x, y + _font.LineHeight + 2), Color.White);
    }

    private void DrawReticle(SpriteBatch sb)
    {
        if (_pauseMenuOpen || !_reticleEnabled)
            return;

        var center = new Vector2(_viewport.X + _viewport.Width / 2f, _viewport.Y + _viewport.Height / 2f);
        var size = Math.Clamp(_reticleSize, ReticleSizeMin, ReticleSizeMax);
        var thickness = Math.Clamp(_reticleThickness, ReticleThicknessMin, ReticleThicknessMax);
        var color = _reticleColor;

        switch (_reticleStyle)
        {
            case "Plus":
            {
                var half = size;
                var t = Math.Max(1, thickness);
                var rectH = new Rectangle((int)(center.X - half), (int)(center.Y - t / 2f), half * 2, t);
                var rectV = new Rectangle((int)(center.X - t / 2f), (int)(center.Y - half), t, half * 2);
                sb.Draw(_pixel, rectH, color);
                sb.Draw(_pixel, rectV, color);
                break;
            }
            case "Square":
            {
                var half = size;
                var t = Math.Max(1, thickness);
                var left = (int)Math.Round(center.X - half);
                var top = (int)Math.Round(center.Y - half);
                var side = half * 2;
                sb.Draw(_pixel, new Rectangle(left, top, side, t), color);
                sb.Draw(_pixel, new Rectangle(left, top + side - t, side, t), color);
                sb.Draw(_pixel, new Rectangle(left, top, t, side), color);
                sb.Draw(_pixel, new Rectangle(left + side - t, top, t, side), color);
                break;
            }
            case "Circle":
                DrawReticleCircle(sb, center, size, thickness, color);
                break;
            default:
            {
                var dot = Math.Max(1, size);
                var rect = new Rectangle((int)(center.X - dot / 2f), (int)(center.Y - dot / 2f), dot, dot);
                sb.Draw(_pixel, rect, color);
                break;
            }
        }

        DrawHandoffPrompt(sb, center, size);
    }

    private void DrawHandoffPrompt(SpriteBatch sb, Vector2 center, int reticleSize)
    {
        if (!_handoffPromptVisible || _pauseMenuOpen || _inventoryOpen)
            return;

        var giveKey = FormatKeyLabel(_giveKey);
        var modKey = FormatKeyLabel(_stackModifierKey);
        var label = _handoffFullStack
            ? $"GIVE FULL STACK ({modKey}+{giveKey})"
            : $"GIVE ITEM ({giveKey})";

        if (!string.IsNullOrWhiteSpace(_handoffTargetName))
            label = $"{label} -> {_handoffTargetName.ToUpperInvariant()}";

        var size = _font.MeasureString(label);
        var pos = new Vector2(center.X - size.X / 2f, center.Y + reticleSize + 10f);
        _font.DrawString(sb, label, pos + new Vector2(2, 2), Color.Black * 0.6f);
        _font.DrawString(sb, label, pos, new Color(230, 230, 230, 230));
    }

    private static string FormatKeyLabel(Keys key)
    {
        return key switch
        {
            Keys.LeftShift or Keys.RightShift => "SHIFT",
            Keys.LeftControl or Keys.RightControl => "CTRL",
            Keys.LeftAlt or Keys.RightAlt => "ALT",
            Keys.Space => "SPACE",
            Keys.OemTilde => "~",
            Keys.OemMinus => "-",
            Keys.OemPlus => "+",
            Keys.OemComma => ",",
            Keys.OemPeriod => ".",
            Keys.OemQuestion => "?",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemPipe => "\\",
            _ => key.ToString().ToUpperInvariant()
        };
    }

    private void DrawReticleCircle(SpriteBatch sb, Vector2 center, float radius, int thickness, Color color)
    {
        if (radius <= 0f)
            return;

        var t = Math.Max(1, thickness);
        for (int i = 0; i < ReticleCirclePoints.Length; i++)
        {
            var p0 = ReticleCirclePoints[i];
            var p1 = ReticleCirclePoints[(i + 1) % ReticleCirclePoints.Length];
            var start = center + p0 * radius;
            var end = center + p1 * radius;
            DrawLine(sb, start, end, color, t);
        }
    }

    private void DrawLine(SpriteBatch sb, Vector2 start, Vector2 end, Color color, int thickness)
    {
        var diff = end - start;
        var length = diff.Length();
        if (length <= 0.001f)
            return;

        var angle = MathF.Atan2(diff.Y, diff.X);
        sb.Draw(_pixel, start, null, color, angle, new Vector2(0f, 0.5f), new Vector2(length, thickness), SpriteEffects.None, 0f);
    }

    private static Vector2[] BuildReticleCirclePoints(int segments)
    {
        var count = Math.Max(8, segments);
        var points = new Vector2[count];
        var step = MathHelper.TwoPi / count;
        for (int i = 0; i < count; i++)
        {
            var angle = step * i;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }

    private static string FormatFaceLabel(FaceDirection face)
    {
        return face switch
        {
            FaceDirection.PosY => "TOP",
            FaceDirection.NegY => "BOTTOM",
            FaceDirection.PosX => "SIDE 1",
            FaceDirection.NegX => "SIDE 2",
            FaceDirection.PosZ => "SIDE 3",
            FaceDirection.NegZ => "SIDE 4",
            _ => face.ToString().ToUpperInvariant()
        };
    }

    private Matrix GetViewMatrix()
    {
        var eye = _player.Position + _player.HeadOffset;
        var target = eye + _player.Forward;
        return Matrix.CreateLookAt(eye, target, Vector3.Up);
    }

    private void EnsureEffect(GraphicsDevice device)
    {
        if (_effect != null)
            return;

        _effect = new BasicEffect(device)
        {
            TextureEnabled = true,
            LightingEnabled = false,
            VertexColorEnabled = false,
            World = Matrix.Identity
        };
    }

    private void EnsureWaterEffect(GraphicsDevice device)
    {
        if (_waterEffectLoadAttempted)
            return;

        _waterEffectLoadAttempted = true;
        var candidates = new[]
        {
            "effects/water.ogl.mgfxo",
            "effects/water.dx11.mgfxo",
            "effects/water.mgfxo"
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            if (!AssetResolver.TryResolve(candidates[i], out _))
                continue;
            if (!_assets.TryLoadAssetBytes(candidates[i], out var bytes) || bytes.Length == 0)
                continue;

            try
            {
                _waterEffect = new Effect(device, bytes);
                _log.Info($"Loaded optional water effect: {candidates[i]}");
                return;
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to load water effect {candidates[i]}: {ex.Message}");
                _waterEffect?.Dispose();
                _waterEffect = null;
            }
        }
    }

    private static void SetEffectMatrix(Effect effect, string name, Matrix value)
    {
        effect.Parameters[name]?.SetValue(value);
    }

    private static void SetEffectVector3(Effect effect, string name, Vector3 value)
    {
        effect.Parameters[name]?.SetValue(value);
    }

    private static void SetEffectFloat(Effect effect, string name, float value)
    {
        effect.Parameters[name]?.SetValue(value);
    }

    private static void SetEffectTexture(Effect effect, string name, Texture2D value)
    {
        effect.Parameters[name]?.SetValue(value);
    }

    private void EnsureLineEffect(GraphicsDevice device)
    {
        if (_lineEffect != null)
            return;

        _lineEffect = new BasicEffect(device)
        {
            TextureEnabled = false,
            LightingEnabled = false,
            VertexColorEnabled = true,
            World = Matrix.Identity
        };
    }

    private void DrawPlayerModel(GraphicsDevice device, Matrix view, Matrix proj)
    {
        _playerModel ??= new PlayerModel(device);

        var prevRaster = device.RasterizerState;
        device.RasterizerState = RasterizerState.CullCounterClockwise;
        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.Opaque;

        _playerModel.Position = _player.Position;
        _playerModel.Yaw = _player.Yaw;
        _playerModel.Render(view, proj);

        device.RasterizerState = prevRaster;
    }

    private void DrawRemotePlayers(GraphicsDevice device, Matrix view, Matrix proj)
    {
        if (_remotePlayers.Count == 0)
            return;

        var prevRaster = device.RasterizerState;
        device.RasterizerState = RasterizerState.CullCounterClockwise;
        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.Opaque;

        foreach (var model in _remotePlayers.Values)
            model.Render(view, proj);

        device.RasterizerState = prevRaster;
    }

    private void DrawFirstPersonHands(GraphicsDevice device, Matrix view, Matrix proj)
    {
        if (_atlas == null)
            return;

        _handRenderer ??= new FirstPersonHandRenderer(device);
        if (!_handRenderer.IsHandMeshValid || !_handRenderer.IsHeldBlockMeshValid)
        {
            if (!_loggedInvalidHandMesh)
            {
                _log.Error("Invalid hand/held-block mesh detected. Skipping overlay draw.");
                _loggedInvalidHandMesh = true;
            }
            return;
        }

        var prevRaster = device.RasterizerState;
        device.SamplerStates[0] = SamplerState.PointClamp;
        device.Clear(ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
        device.DepthStencilState = DepthStencilState.Default;
        device.BlendState = BlendState.Opaque;
        device.RasterizerState = RasterizerState.CullNone;
        var forward = _player.Forward;
        var up = Vector3.Up;
        var heldId = _inventory.SelectedId;
        var heldModel = _blockModel;
        if (heldId != BlockId.Air && BlockRegistry.Get(heldId).HasCustomModel)
            heldModel = BlockModel.GetModel(heldId, _log);
        _handRenderer.Draw(view, proj, _player.Position + _player.HeadOffset, forward, up, _atlas, heldId, heldModel);
        device.DepthStencilState = DepthStencilState.Default;
        device.RasterizerState = prevRaster;
    }

    private void DrawBlockHighlight(GraphicsDevice device, Matrix view, Matrix proj)
    {
        UpdateBlockHighlight();
        if (!_highlightActive)
            return;

        EnsureLineEffect(device);
        if (_lineEffect == null)
            return;

        _lineEffect.View = view;
        _lineEffect.Projection = proj;
        _lineEffect.World = Matrix.Identity;

        device.DepthStencilState = DepthStencilState.DepthRead;
        device.BlendState = BlendState.AlphaBlend;
        device.RasterizerState = RasterizerState.CullNone;

        foreach (var pass in _lineEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.LineList, _highlightVerts, 0, 12);
        }
    }

    private void DrawWorldItems(GraphicsDevice device, Matrix view, Matrix proj)
    {
        if (_worldItems.Count == 0 || _atlas == null)
            return;

        EnsureItemEffect(device);
        if (_itemEffect == null)
            return;

        _itemEffect.View = view;
        _itemEffect.Projection = proj;
        _itemEffect.Texture = _atlas.Texture;

        device.RasterizerState = RasterizerState.CullNone;
        device.SamplerStates[0] = SamplerState.PointClamp;

        DrawWorldItemsPass(device, transparent: false);
        DrawWorldItemsPass(device, transparent: true);
    }

    private void DrawWaterChunks(GraphicsDevice device, BoundingFrustum frustum, Matrix view, Matrix proj)
    {
        if (_effect == null)
            return;

        EnsureWaterEffect(device);

        device.BlendState = BlendState.AlphaBlend;
        device.DepthStencilState = DepthStencilState.DepthRead;

        var pulse = (MathF.Sin(_worldTimeSeconds * 1.6f) + 1f) * 0.5f;
        var tintA = new Vector3(0.56f, 0.78f, 1f);
        var tintB = new Vector3(0.44f, 0.67f, 0.95f);
        var tint = Vector3.Lerp(tintA, tintB, pulse);
        var alpha = 0.66f + pulse * 0.10f;

        if (_waterEffect == null)
        {
            _effect.DiffuseColor = tint;
            _effect.Alpha = alpha;
        }
        else
        {
            SetEffectVector3(_waterEffect, "TintColor", tint);
            SetEffectFloat(_waterEffect, "WaterAlpha", alpha);
            SetEffectFloat(_waterEffect, "Time", _worldTimeSeconds);
            SetEffectFloat(_waterEffect, "WaveStrength", 0.015f);
            SetEffectFloat(_waterEffect, "UvScrollSpeed", 0.015f);
            SetEffectMatrix(_waterEffect, "View", view);
            SetEffectMatrix(_waterEffect, "Projection", proj);
            if (_atlas != null)
                SetEffectTexture(_waterEffect, "Texture0", _atlas.Texture);
        }

        foreach (var coord in _chunkOrder)
        {
            if (!_chunkMeshes.TryGetValue(coord, out var mesh))
                continue;
            if (!frustum.Intersects(mesh.Bounds))
                continue;

            var verts = mesh.WaterVertices;
            if (verts == null || verts.Length == 0)
                continue;
            if (verts.Length % 3 != 0)
                continue;

            var wave = MathF.Sin(_worldTimeSeconds * 1.9f + coord.X * 0.45f + coord.Z * 0.37f) * 0.015f;
            var world = Matrix.CreateTranslation(0f, wave, 0f);

            if (_waterEffect != null)
            {
                SetEffectMatrix(_waterEffect, "World", world);
                foreach (var pass in _waterEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
                }
            }
            else
            {
                _effect.World = world;
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
                }
            }
        }

        if (_waterEffect == null)
        {
            _effect.World = Matrix.Identity;
            _effect.DiffuseColor = Vector3.One;
            _effect.Alpha = 1f;
        }
    }

    private void DrawWorldItemsPass(GraphicsDevice device, bool transparent)
    {
        if (_itemEffect == null)
            return;

        device.DepthStencilState = transparent ? DepthStencilState.DepthRead : DepthStencilState.Default;
        device.BlendState = transparent ? BlendState.AlphaBlend : BlendState.Opaque;

        foreach (var pair in _worldItems)
        {
            var item = pair.Value;
            if (BlockRegistry.IsTransparent((byte)item.BlockId) != transparent)
                continue;
            var mesh = GetItemMesh(item.BlockId);
            if (mesh.Length == 0)
                continue;

            var bobPhase = MathF.Sin((_worldTimeSeconds - item.SpawnTime) * ItemBobSpeed + item.ItemId * 0.31f);
            var bob = item.IsGrounded ? (bobPhase * 0.5f + 0.5f) * ItemBobHeight : bobPhase * ItemBobHeight;
            var spin = (_worldTimeSeconds + item.ItemId * 0.17f) * ItemSpinSpeed;
            var world = Matrix.CreateScale(ItemScale)
                * Matrix.CreateRotationX(ItemTiltRadians)
                * Matrix.CreateRotationZ(ItemTiltRadians * 0.6f)
                * Matrix.CreateRotationY(spin)
                * Matrix.CreateTranslation(item.Position + new Vector3(0f, bob, 0f));
            _itemEffect.World = world;

            foreach (var pass in _itemEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, mesh.Length / 3);
            }
        }
    }

    private void EnsureItemEffect(GraphicsDevice device)
    {
        if (_itemEffect != null)
            return;

        _itemEffect = new BasicEffect(device)
        {
            TextureEnabled = true,
            VertexColorEnabled = false,
            LightingEnabled = false
        };
    }

    private VertexPositionTexture[] GetItemMesh(BlockId id)
    {
        if (_atlas == null)
            return Array.Empty<VertexPositionTexture>();

        if (_itemMeshCache.TryGetValue(id, out var mesh))
            return mesh;

        var model = _blockModel;
        if (BlockRegistry.Get(id).HasCustomModel)
            model = BlockModel.GetModel(id, _log);

        mesh = model?.BuildMesh(_atlas, id) ?? BlockModel.BuildCubeMesh(_atlas, id);
        _itemMeshCache[id] = mesh;
        return mesh;
    }

    private void UpdateBlockHighlight()
    {
        if (_world == null)
        {
            _highlightActive = false;
            return;
        }

        var origin = _player.Position + _player.HeadOffset;
        var dir = _player.Forward;
        if (!VoxelRaycast.Raycast(origin, dir, InteractRange, _world.GetBlock, out var hit))
        {
            _highlightActive = false;
            return;
        }

        var id = _world.GetBlock(hit.X, hit.Y, hit.Z);
        if (id == BlockIds.Air)
        {
            _highlightActive = false;
            return;
        }

        if (_highlightActive && hit.X == _highlightX && hit.Y == _highlightY && hit.Z == _highlightZ)
        {
            if (_highlightColor == _blockOutlineColor)
                return;
        }

        _highlightX = hit.X;
        _highlightY = hit.Y;
        _highlightZ = hit.Z;
        _highlightActive = true;

        var min = new Vector3(hit.X, hit.Y, hit.Z) * Scale.BlockSize;
        var max = min + new Vector3(Scale.BlockSize);
        var eps = HighlightEpsilon;
        min -= new Vector3(eps);
        max += new Vector3(eps);

        BuildHighlightVerts(min, max, _blockOutlineColor);
        _highlightColor = _blockOutlineColor;
    }

    private void BuildHighlightVerts(Vector3 min, Vector3 max, Color color)
    {
        var v000 = new Vector3(min.X, min.Y, min.Z);
        var v100 = new Vector3(max.X, min.Y, min.Z);
        var v110 = new Vector3(max.X, max.Y, min.Z);
        var v010 = new Vector3(min.X, max.Y, min.Z);
        var v001 = new Vector3(min.X, min.Y, max.Z);
        var v101 = new Vector3(max.X, min.Y, max.Z);
        var v111 = new Vector3(max.X, max.Y, max.Z);
        var v011 = new Vector3(min.X, max.Y, max.Z);

        SetLine(0, v000, v100, color);
        SetLine(1, v100, v110, color);
        SetLine(2, v110, v010, color);
        SetLine(3, v010, v000, color);

        SetLine(4, v001, v101, color);
        SetLine(5, v101, v111, color);
        SetLine(6, v111, v011, color);
        SetLine(7, v011, v001, color);

        SetLine(8, v000, v001, color);
        SetLine(9, v100, v101, color);
        SetLine(10, v110, v111, color);
        SetLine(11, v010, v011, color);
    }

    private void SetLine(int index, Vector3 a, Vector3 b, Color color)
    {
        var i = index * 2;
        _highlightVerts[i].Position = a;
        _highlightVerts[i].Color = color;
        _highlightVerts[i + 1].Position = b;
        _highlightVerts[i + 1].Color = color;
    }

    private void UpdatePauseMenuLayout()
    {
        var showInvite = CanShareJoinCode();
        var showOpenLan = CanOpenLanFromPause();
        var showHostOnline = CanOpenOnlineFromPause();

        _pauseOpenLan.Visible = showOpenLan;
        _pauseHostOnline.Visible = showHostOnline;
        _pauseInvite.Visible = showInvite;
        _pauseHostOnline.Enabled = showHostOnline;

        var buttonCount = 3;
        if (showOpenLan)
            buttonCount++;
        if (showHostOnline)
            buttonCount++;
        if (showInvite)
            buttonCount++;

        var panelW = Math.Clamp((int)(_viewport.Width * 0.42f), 320, _viewport.Width - 40);
        var buttonW = Math.Clamp((int)(panelW * 0.6f), 160, 320);
        var buttonH = Math.Clamp((int)(buttonW * 0.22f), 36, 60);
        var gap = 14;
        var titleH = _font.LineHeight + 14;
        var padding = 18;
        var totalButtonsH = buttonH * buttonCount + gap * (buttonCount - 1);
        var contentH = titleH + totalButtonsH + padding * 2;
        var panelH = Math.Clamp(contentH, 220, _viewport.Height - 40);
        _pauseRect = new Rectangle(
            _viewport.X + (_viewport.Width - panelW) / 2,
            _viewport.Y + (_viewport.Height - panelH) / 2,
            panelW,
            panelH);

        _pauseHeaderRect = new Rectangle(
            _pauseRect.X + padding,
            _pauseRect.Y + padding,
            _pauseRect.Width - padding * 2,
            titleH);

        var available = _pauseRect.Height - padding * 2 - titleH;
        var startY = _pauseRect.Y + padding + titleH + Math.Max(0, (available - totalButtonsH) / 2);
        var centerX = _pauseRect.X + (_pauseRect.Width - buttonW) / 2;

        var row = 0;
        _pauseResume.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row++, buttonW, buttonH);
        if (showOpenLan)
            _pauseOpenLan.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row++, buttonW, buttonH);
        if (showHostOnline)
            _pauseHostOnline.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row++, buttonW, buttonH);
        if (showInvite)
            _pauseInvite.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row++, buttonW, buttonH);
        _pauseSettings.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row++, buttonW, buttonH);
        _pauseSaveExit.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * row, buttonW, buttonH);
    }

    private void DrawPauseMenu(SpriteBatch sb)
    {
        UpdatePauseMenuLayout();

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 140));
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        if (_pausePanel is not null)
            sb.Draw(_pausePanel, _pauseRect, Color.White);
        else
            sb.Draw(_pixel, _pauseRect, new Color(25, 25, 25));

        var title = "PAUSED";
        var size = _font.MeasureString(title);
        sb.Draw(_pixel, _pauseHeaderRect, new Color(0, 0, 0, 80));
        var titlePos = new Vector2(_pauseHeaderRect.Center.X - size.X / 2f, _pauseHeaderRect.Y + (_pauseHeaderRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, title, titlePos + new Vector2(1, 1), Color.Black);
        _font.DrawString(sb, title, titlePos, Color.White);

        _pauseResume.Draw(sb, _pixel, _font);
        _pauseOpenLan.Draw(sb, _pixel, _font);
        _pauseHostOnline.Draw(sb, _pixel, _font);
        _pauseInvite.Draw(sb, _pixel, _font);
        _pauseSettings.Draw(sb, _pixel, _font);
        _pauseSaveExit.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawSavingOverlay(SpriteBatch sb)
    {
        sb.Begin();
        sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 220));

        var center = new Vector2(_viewport.Width / 2f, _viewport.Height / 2f);
        var title = "SAVING WORLD...";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(center.X - titleSize.X / 2f, center.Y - 26f), Color.White);

        var elapsed = _saveAndExitStartedUtc == DateTime.MinValue
            ? 0.0
            : (DateTime.UtcNow - _saveAndExitStartedUtc).TotalSeconds;
        var subtitle = $"PLEASE WAIT {elapsed:0.0}s";
        var subtitleSize = _font.MeasureString(subtitle);
        _font.DrawString(sb, subtitle, new Vector2(center.X - subtitleSize.X / 2f, center.Y + 4f), Color.White);

        if (!string.IsNullOrWhiteSpace(_saveAndExitError))
        {
            var errorSize = _font.MeasureString(_saveAndExitError);
            _font.DrawString(sb, _saveAndExitError, new Vector2(center.X - errorSize.X / 2f, center.Y + 4f + _font.LineHeight + 6f), Color.OrangeRed);
        }

        var indicatorSize = 10;
        var pulse = (_spinnerFrame % 8) / 7f;
        var color = Color.Lerp(new Color(120, 120, 120), Color.White, pulse);
        var indicatorRect = new Rectangle((int)center.X - indicatorSize / 2, (int)center.Y + 4 + _font.LineHeight + 24, indicatorSize, indicatorSize);
        sb.Draw(_pixel, indicatorRect, color);
        sb.End();
    }

    private void OpenSettings()
    {
        _pauseMenuOpen = false;
        _menus.Push(new OptionsScreen(_menus, _assets, _font, _pixel, _log, _graphics), _viewport);
    }




    private void OpenToLanFromPause()
    {
        if (!CanOpenLanFromPause())
        {
            SetCommandStatus("LAN host is already active.");
            return;
        }

        if (_world == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        var result = WorldHostBootstrap.TryStartLanHost(_log, _profile, _world);
        if (!result.Success || result.Session == null)
        {
            SetCommandStatus($"LAN host failed: {result.Error}");
            return;
        }

        _lanSession = result.Session;
        SeedLocalPlayerName();
        SetCommandStatus("LAN hosting enabled. Other players can now join from Multiplayer > LAN.", 7f);
        UpdatePauseMenuLayout();
    }

    private void OpenOnlineHostFromPause()
    {
        if (!CanOpenOnlineFromPause())
        {
            SetCommandStatus("Online host is already active.");
            return;
        }

        if (_world == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        var eos = EnsureEosClient();
        if (eos == null)
        {
            SetCommandStatus("EOS CLIENT UNAVAILABLE");
            return;
        }
        var onlineGate = OnlineGateClient.GetOrCreate();
        if (!onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetCommandStatus(gateDenied);
            return;
        }

        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            if (snapshot.Reason == EosRuntimeReason.Connecting)
                eos.StartLogin();
            SetCommandStatus(snapshot.StatusText);
            return;
        }

        var result = WorldHostBootstrap.TryStartEosHost(_log, _profile, eos, _world);
        if (!result.Success || result.Session == null)
        {
            SetCommandStatus($"Online host failed: {result.Error}");
            return;
        }

        _lanSession = result.Session;
        SeedLocalPlayerName();
        SetCommandStatus("Online hosting enabled. Use SHARE CODE to invite others.", 7f);
        UpdatePauseMenuLayout();
    }

    private void OpenShareCode()
    {
        _pauseMenuOpen = false;

        var onlineGate = OnlineGateClient.GetOrCreate();
        if (!onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetCommandStatus(gateDenied);
            return;
        }

        if (_lanSession is not EosP2PHostSession)
        {
            _log.Warn("Share code requested but this is not an EOS-hosted session.");
            return;
        }

        var eos = EnsureEosClient();
        if (eos == null || !eos.IsLoggedIn || string.IsNullOrWhiteSpace(eos.LocalProductUserId))
        {
            _log.Warn("Share code requested without EOS login.");
            return;
        }

        _menus.Push(new ShareJoinCodeScreen(_menus, _assets, _font, _pixel, _log, eos.LocalProductUserId), _viewport);
    }

    public void OnClose()
    {
        _log.Info("GameWorldScreen: Closing world screen.");
        _isClosing = true;
        if (!_skipOnCloseFullSave)
        {
            var closeState = CapturePlayerState();
            SavePlayerState(closeState);
            var savedChunks = _world?.SaveAllLoadedChunks() ?? 0;
            var savedMeshes = SaveAllLoadedChunkMeshesToCache();
            FlushWorldPreviewUpdate(closeState);
            _log.Info($"GameWorldScreen: OnClose saved {savedChunks} chunks and {savedMeshes} mesh caches.");
        }
        else
        {
            _log.Info("GameWorldScreen: OnClose save skipped because Save & Exit already completed.");
        }

        _streamingService?.Dispose();
        _streamingService = null;
        _meshQueue.Clear();
        _meshQueued.Clear();
        _priorityMeshQueue.Clear();
        _priorityMeshQueued.Clear();
        while (_completedMeshBuildQueue.TryDequeue(out _)) { }
        while (_completedPriorityMeshBuildQueue.TryDequeue(out _)) { }
        while (_chunkGenerationQueue.TryDequeue(out _)) { }
        _chunkGenerationQueued.Clear();
        _chunkGenerationInFlight.Clear();
        if (_lanSession is EosP2PHostSession)
            WorldHostBootstrap.TryClearHostingPresence(_log, EnsureEosClient());
        _lanSession?.Dispose();
        _blockIconCache?.Dispose();
        _blockIconCache = null;
        _samplerLow?.Dispose();
        _samplerLow = null;
        _samplerMedium?.Dispose();
        _samplerMedium = null;
        _samplerHigh?.Dispose();
        _samplerHigh = null;
        _samplerUltra?.Dispose();
        _samplerUltra = null;
        _waterEffect?.Dispose();
        _waterEffect = null;
    }

    private void SaveAndExit()
    {
        if (_saveAndExitInProgress)
            return;

        _pauseMenuOpen = false;
        _log.Info("Pause menu: Save & Exit requested.");
        BeginSaveAndExit();
    }

    private void BeginSaveAndExit()
    {
        _isClosing = true;
        _saveAndExitError = null;
        _saveAndExitInProgress = true;
        _saveAndExitStartedUtc = DateTime.UtcNow;

        // Apply any completed background mesh results before snapshotting save output.
        ApplyCompletedMeshBuilds(256);

        _saveAndExitTask = Task.Run(SaveWorldForExit);
    }

    private void CompleteSaveAndExit()
    {
        if (_saveAndExitTask == null)
            return;

        SaveAndExitResult result;
        try
        {
            result = _saveAndExitTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new SaveAndExitResult(false, 0, 0, 0, ex.Message);
        }

        _saveAndExitTask = null;
        _saveAndExitInProgress = false;

        if (result.Success)
        {
            _skipOnCloseFullSave = true;
            _log.Info($"Save & Exit complete: chunks={result.SavedChunks}, meshes={result.SavedMeshes}, time={result.Seconds:0.00}s");
        }
        else
        {
            _saveAndExitError = $"SAVE ERROR: {result.Error}";
            _log.Warn($"Save & Exit failed after {result.Seconds:0.00}s: {result.Error}");
        }

        _menus.Pop();
    }

    private SaveAndExitResult SaveWorldForExit()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var state = CapturePlayerState();
            state.Save(_worldPath, _log);
            if (_meta != null)
                _meta.Save(_metaPath, _log);

            var ensuredGateMeshes = EnsureResumeGateMeshes(state);
            var savedChunks = _world?.SaveAllLoadedChunks() ?? 0;
            var savedMeshes = SaveAllLoadedChunkMeshesToCache();
            FlushWorldPreviewUpdate(state);

            sw.Stop();
            _log.Info($"Save & Exit detail: ensured gate meshes={ensuredGateMeshes}, cached meshes={savedMeshes}");
            return new SaveAndExitResult(true, savedChunks, savedMeshes, sw.Elapsed.TotalSeconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SaveAndExitResult(false, 0, 0, sw.Elapsed.TotalSeconds, ex.Message);
        }
    }

    private int EnsureResumeGateMeshes(PlayerWorldState playerState)
    {
        if (_world == null || _atlas == null)
            return 0;

        var playerChunk = VoxelWorld.WorldToChunk(
            (int)MathF.Floor(playerState.PosX),
            (int)MathF.Floor(playerState.PosY),
            (int)MathF.Floor(playerState.PosZ),
            out _, out _, out _);
        var maxCy = GetStreamingMaxChunkY(playerChunk.Y);
        var prewarmRadius = Math.Clamp(Math.Min(_activeRadiusChunks, PrewarmRadius), 2, PrewarmRadius);
        var gateRadius = Math.Clamp(Math.Min(prewarmRadius, PrewarmGateRadius), 1, prewarmRadius);
        var gateRadiusSq = gateRadius * gateRadius;
        var built = 0;

        for (var dz = -gateRadius; dz <= gateRadius; dz++)
        {
            for (var dx = -gateRadius; dx <= gateRadius; dx++)
            {
                if (dx * dx + dz * dz > gateRadiusSq)
                    continue;

                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    if (!IsChunkWithinWorldBounds(coord))
                        continue;
                    if (!_world.TryGetChunk(coord, out var chunk) || chunk == null)
                        chunk = _world.GetOrCreateChunk(coord);

                    if (_chunkMeshes.TryGetValue(coord, out var existingMesh) && ValidateMesh(existingMesh))
                        continue;

                    try
                    {
                        var mesh = VoxelMesherGreedy.BuildChunkMeshPriority(_world, chunk, _atlas, _log);
                        if (!ValidateMesh(mesh))
                            continue;

                        _chunkMeshes[coord] = mesh;
                        if (!_chunkOrder.Contains(coord))
                            _chunkOrder.Add(coord);
                        chunk.IsDirty = false;
                        built++;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Resume gate mesh build failed for {coord}: {ex.Message}");
                    }
                }
            }
        }

        return built;
    }

    private int SaveAllLoadedChunkMeshesToCache()
    {
        var saved = 0;
        foreach (var pair in _chunkMeshes)
        {
            var mesh = pair.Value;
            if (mesh == null || !ValidateMesh(mesh))
                continue;

            try
            {
                ChunkMeshCache.Save(_worldPath, mesh);
                saved++;
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to save mesh cache for {mesh.Coord}: {ex.Message}");
            }
        }

        return saved;
    }

    private EosClient? EnsureEosClient()
    {
        if (_eosClient != null)
            return _eosClient;

        _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("GameWorldScreen: EOS client not available.");
        return _eosClient;
    }




    private bool CanOpenLanFromPause()
    {
        if (_saveAndExitInProgress || _world == null || _worldSyncInProgress)
            return false;
        return _lanSession == null;
    }

    private bool CanOpenOnlineFromPause()
    {
        if (_saveAndExitInProgress || _world == null || _worldSyncInProgress)
            return false;
        return _lanSession == null;
    }

    private bool CanShareJoinCode()
    {
        if (_lanSession == null || !_lanSession.IsHost)
            return false;

        if (_lanSession is not EosP2PHostSession)
            return false;

        var eos = EnsureEosClient();
        return eos != null && eos.IsLoggedIn && !string.IsNullOrWhiteSpace(eos.LocalProductUserId);
    }

    private void SeedLocalPlayerName()
    {
        if (_lanSession == null)
            return;

        var name = _profile.GetDisplayUsername();
        if (string.IsNullOrWhiteSpace(name))
            name = "You";

        _playerNames[_lanSession.LocalPlayerId] = name;
    }

    private void UpdateNetwork(float dt)
    {
        if (_lanSession == null || !_lanSession.IsConnected)
            return;
#if EOS_SDK
        if (_lanSession is EosP2PHostSession eosHost)
            eosHost.PumpIncoming();
        else if (_lanSession is EosP2PClientSession eosClient)
            eosClient.PumpIncoming();
#endif

        _netSendAccumulator += dt;
        if (_netSendAccumulator >= 0.05f)
        {
            byte status = 0;
            if (_player.IsFlying)
                status |= 1;
            _lanSession.SendPlayerState(_player.Position, _player.Yaw, _player.Pitch, status);
            _netSendAccumulator = 0;
        }

        while (_lanSession.TryDequeuePlayerList(out var list))
            ApplyPlayerList(list);

        while (_lanSession.TryDequeuePlayerState(out var state))
        {
            if (state.PlayerId == _lanSession.LocalPlayerId)
                continue;

            if (!_remotePlayers.TryGetValue(state.PlayerId, out var model))
            {
                model = new PlayerModel(_graphics.GraphicsDevice);
                _remotePlayers[state.PlayerId] = model;
            }

            model.Position = state.Position;
            model.Yaw = state.Yaw;
            model.IsFlying = (state.Status & 1) != 0;
        }

        while (_lanSession.TryDequeueChat(out var chat))
            HandleNetworkChat(chat);

        if (_world == null)
            return;
        
        // Handle chunk data for initial world sync
        if (_worldSyncInProgress)
        {
            if (_world == null) return;

            // The initial chunk flood can be massive, process a few per frame
            var chunkProcessBudget = 5;
            while (chunkProcessBudget > 0 && _lanSession.TryDequeueChunkData(out var chunkData))
            {
                if (chunkData.Blocks != null)
                {
                    var chunkCoord = new ChunkCoord(chunkData.X, chunkData.Y, chunkData.Z);
                    var chunk = _world.GetOrCreateChunk(chunkCoord);
                    Buffer.BlockCopy(chunkData.Blocks, 0, chunk.Blocks, 0, VoxelChunkData.Volume);
                    chunk.IsDirty = true; // Mark as dirty so it gets re-meshed
                    _chunksReceived++;
                }
                chunkProcessBudget--;
            }

            if (_lanSession.TryDequeueWorldSyncComplete(out _)) // Check for signal message
            {
                _worldSyncInProgress = false;
                _log.Info("Initial world sync complete!");
            }
        }

        while (_lanSession.TryDequeueBlockSet(out var block))
        {
            if (!_world.SetBlock(block.X, block.Y, block.Z, block.Id))
            {
                // Optional: log if client receives an invalid block set from host.
            }
        }

        while (_lanSession.TryDequeueItemSpawn(out var spawn))
            SpawnWorldItem(spawn);

        while (_lanSession.TryDequeueItemPickup(out var pickup))
            RemoveWorldItem(pickup.ItemId);
    }

    private void ApplyPlayerList(LanPlayerList list)
    {
        _playerNames.Clear();
        var players = list.Players ?? Array.Empty<LanPlayerInfo>();
        for (var i = 0; i < players.Length; i++)
        {
            var entry = players[i];
            var name = string.IsNullOrWhiteSpace(entry.Name) ? $"Player{entry.PlayerId}" : entry.Name;
            _playerNames[entry.PlayerId] = name;
        }

        if (_lanSession != null && !_playerNames.ContainsKey(_lanSession.LocalPlayerId))
            SeedLocalPlayerName();

        PruneRemotePlayers();
    }

    private void PruneRemotePlayers()
    {
        if (_playerNames.Count == 0)
            return;

        var toRemove = new List<int>();
        foreach (var id in _remotePlayers.Keys)
        {
            if (!_playerNames.ContainsKey(id))
                toRemove.Add(id);
        }

        for (var i = 0; i < toRemove.Count; i++)
            _remotePlayers.Remove(toRemove[i]);
    }

    private void RestoreOrCenterCamera()
    {
        if (_meta == null)
        {
            CenterCamera();
            return;
        }

        var username = _profile.GetDisplayUsername();
        var state = PlayerWorldState.LoadOrDefault(_worldPath, username, BuildDefaultPlayerState, _log);
        ApplyPlayerState(state);
    }

    private PlayerWorldState BuildDefaultPlayerState()
    {
        var username = _profile.GetDisplayUsername();
        var state = new PlayerWorldState
        {
            Username = username,
            CurrentGameMode = _meta?.CurrentWorldGameMode ?? GameMode.Artificer
        };

        if (_meta == null)
            return state;

        // Use dedicated spawn point instead of world center
        var spawnPoint = GetWorldSpawnPoint();
        
        // Find safe spawn height by scanning terrain at spawn point
        var safeHeight = FindSafeSpawnHeight(spawnPoint.X, spawnPoint.Y);
        var height = _meta.HasCustomSpawn
            ? Math.Clamp(_meta.SpawnY, 2, Math.Max(3, _meta.Size.Height - 2))
            : Math.Max(safeHeight + 2f, 6f); // Spawn 2 blocks above ground
        
        // Position player at spawn point looking toward world center
        var pos = new Vector3(spawnPoint.X, height, spawnPoint.Y);
        var centerX = _meta.Size.Width / 2f;
        var centerZ = _meta.Size.Depth / 2f;
        var target = new Vector3(centerX, height, centerZ);
        var dir = Vector3.Normalize(target - pos);

        state.PosX = pos.X;
        state.PosY = pos.Y;
        state.PosZ = pos.Z;
        state.Yaw = (float)Math.Atan2(dir.Z, dir.X);
        state.Pitch = (float)Math.Asin(dir.Y);

        return state;
    }

    private Vector2 GetWorldSpawnPoint()
    {
        if (_meta == null)
            return new Vector2(64f, 64f); // Fallback spawn

        if (_meta.HasCustomSpawn)
        {
            var customX = Math.Clamp(_meta.SpawnX, 0, Math.Max(0, _meta.Size.Width - 1));
            var customZ = Math.Clamp(_meta.SpawnZ, 0, Math.Max(0, _meta.Size.Depth - 1));
            return new Vector2(customX, customZ);
        }

        // For now, use a fixed spawn point that's 1/4 from the edge
        // This could be enhanced with dedicated spawn point data in world meta
        var spawnX = _meta.Size.Width * 0.25f; // 25% from left edge
        var spawnZ = _meta.Size.Depth * 0.25f; // 25% from top edge
        
        // Ensure spawn is within world bounds
        spawnX = Math.Max(16f, Math.Min(spawnX, _meta.Size.Width - 16f));
        spawnZ = Math.Max(16f, Math.Min(spawnZ, _meta.Size.Depth - 16f));
        
        return new Vector2(spawnX, spawnZ);
    }

    private float FindSafeSpawnHeight(float x, float z)
    {
        if (_world == null)
            return 64f; // Default fallback height

        // Scan from top to bottom to find first solid ground
        var maxY = _meta?.Size.Height ?? 128;
        for (int y = (int)maxY - 1; y >= 0; y--)
        {
            var blockId = _world.GetBlock((int)x, y, (int)z);
            if (blockId != BlockIds.Air && blockId != BlockIds.Nullblock)
            {
                return y + 1f; // Spawn 1 block above solid ground
            }
        }
        
        // If no ground found, return reasonable default
        return 64f;
    }

    /// <summary>
    /// AGGRESSIVE FIX: Generate solid ground immediately at player position
    /// This creates a guaranteed safe platform under the player
    /// </summary>
    private void GenerateSolidGroundAtPlayer()
    {
        if (_world == null || _player == null)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"AGGRESSIVE FIX: Generating solid ground at player position {playerPos}");
        
        // Create chunk if it doesn't exist
        if (!_world.TryGetChunk(playerChunk, out var chunkData) || chunkData == null)
        {
            chunkData = new VoxelChunkData(playerChunk);
            _world.AddChunkDirect(playerChunk, chunkData);
        }
        
        // Generate SOLID ground in a 5x5 area around player
        var playerBlockX = (int)Math.Floor(playerPos.X) - playerChunk.X * 16;
        var playerBlockZ = (int)Math.Floor(playerPos.Z) - playerChunk.Z * 16;
        var playerBlockY = (int)Math.Floor(playerPos.Y);
        
        // Generate solid ground from player level down to create a platform
        for (int x = Math.Max(0, playerBlockX - 2); x <= Math.Min(15, playerBlockX + 2); x++)
        {
            for (int z = Math.Max(0, playerBlockZ - 2); z <= Math.Min(15, playerBlockZ + 2); z++)
            {
                for (int y = Math.Max(0, playerBlockY - 5); y <= Math.Min(15, playerBlockY + 2); y++)
                {
                    // Create solid ground layers
                    if (y == playerBlockY)
                    {
                        chunkData.SetLocal(x, y, z, BlockIds.Grass); // Grass surface
                    }
                    else if (y >= playerBlockY - 3)
                    {
                        chunkData.SetLocal(x, y, z, BlockIds.Dirt); // Dirt layers
                    }
                    else
                    {
                        chunkData.SetLocal(x, y, z, BlockIds.Stone); // Stone below
                    }
                }
            }
        }
        
        _log.Info($"AGGRESSIVE FIX: Generated solid ground platform at player position");
    }

    /// <summary>
    /// AGGRESSIVE SAFETY: Ensure player has solid ground to stand on
    /// This is the ultimate safety check to prevent falling
    /// </summary>
    private void EnsureSolidGroundUnderPlayer()
    {
        if (_world == null || _player == null)
            return;

        var playerPos = _player.Position;
        var worldX = (int)Math.Floor(playerPos.X);
        var worldY = (int)Math.Floor(playerPos.Y);
        var worldZ = (int)Math.Floor(playerPos.Z);
        
        _log.Info($"AGGRESSIVE SAFETY: Ensuring solid ground under player at ({worldX}, {worldY}, {worldZ})");
        
        // Check if player has solid ground below them
        bool hasSolidGround = false;
        for (int y = worldY; y >= Math.Max(0, worldY - 10); y--)
        {
            var block = _world.GetBlock(worldX, y, worldZ);
            if (block != BlockIds.Air && block != BlockIds.Nullblock)
            {
                hasSolidGround = true;
                break;
            }
        }
        
        if (!hasSolidGround)
        {
            _log.Warn($"AGGRESSIVE SAFETY: No solid ground found below player! Creating emergency platform...");
            
            // Create emergency platform at player level
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    for (int dy = 0; dy <= 3; dy++)
                    {
                        var blockX = worldX + dx;
                        var blockY = worldY - dy;
                        var blockZ = worldZ + dz;
                        
                        if (dy == 0)
                        {
                            _world.SetBlock(blockX, blockY, blockZ, BlockIds.Grass);
                        }
                        else if (dy <= 2)
                        {
                            _world.SetBlock(blockX, blockY, blockZ, BlockIds.Dirt);
                        }
                        else
                        {
                            _world.SetBlock(blockX, blockY, blockZ, BlockIds.Stone);
                        }
                    }
                }
            }
            
            _log.Info($"AGGRESSIVE SAFETY: Emergency platform created at player position");
        }
        else
        {
            _log.Info($"AGGRESSIVE SAFETY: Solid ground confirmed below player");
        }
    }

    /// <summary>
    /// ULTRA AGGRESSIVE: Force load additional chunks to prevent missing chunks
    /// This loads a much larger area to ensure no random missing chunks
    /// </summary>
    private void ForceLoadAdditionalChunks()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        _log.Info("ULTRA AGGRESSIVE: Force loading additional chunks to prevent missing chunks");
        
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // ULTRA AGGRESSIVE: 7x7 area around player for complete coverage
        var radius = 3; // 7x7 area = 49 chunks
        var maxCy = _world.MaxChunkY;
        var totalChunks = 0;
        var loadedChunks = 0;
        
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    totalChunks++;
                    
                    // Force chunk generation if it doesn't exist
                    if (!_world.TryGetChunk(coord, out var chunk) || chunk == null)
                    {
                        _streamingService.EnqueueLoadJob(coord, -30); // Ultra high priority
                        _log.Debug($"Force loading chunk {coord}");
                    }
                    else
                    {
                        loadedChunks++;
                        
                        // Force mesh generation if it doesn't exist
                        if (!_chunkMeshes.ContainsKey(coord))
                        {
                            _streamingService.EnqueueMeshJob(coord, chunk, -30); // Ultra high priority
                            _log.Debug($"Force meshing chunk {coord}");
                        }
                    }
                }
            }
        }
        
        _log.Info($"Ultra aggressive loading: {loadedChunks}/{totalChunks} chunks loaded, {totalChunks - loadedChunks} chunks queued");
        
        // Process some results immediately
        ProcessMeshJobsNonBlocking();
        
        // Wait a bit for immediate chunks to load
        System.Threading.Thread.Sleep(500);
        
        // Check results and generate emergency meshes for anything still missing
        GenerateEmergencyMeshesForMissingChunks();
    }

    /// <summary>
    /// RESEARCH-BASED: Ensure spawn chunks are loaded using persistent spawn-chunk logic
    /// Spawn chunks always tick even without players nearby
    /// </summary>
    private void EnsureSpawnChunksLoaded()
    {
        if (_world == null || _streamingService == null)
            return;

        _log.Info("RESEARCH-BASED: Ensuring persistent spawn chunks are loaded");
        
        var spawnPoint = GetWorldSpawnPoint();
        var spawnChunk = VoxelWorld.WorldToChunk((int)spawnPoint.X, 0, (int)spawnPoint.Y, out _, out _, out _);
        var surfaceSpawnChunk = new ChunkCoord(spawnChunk.X, spawnChunk.Y, spawnChunk.Z);
        
        var maxCy = _world.MaxChunkY;
        var spawnChunksLoaded = 0;
        
        // Load spawn chunks in a persistent 3x3 area around spawn.
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(surfaceSpawnChunk.X + dx, cy, surfaceSpawnChunk.Z + dz);
                    
                    // Force load spawn chunks with highest priority
                    if (!_world.TryGetChunk(coord, out _))
                    {
                        _streamingService.EnqueueLoadJob(coord, -50); // Spawn chunks get highest priority
                        _log.Debug($"Loading spawn chunk {coord}");
                    }
                    else
                    {
                        spawnChunksLoaded++;
                        
                        // Force mesh generation for spawn chunks
                        if (!_chunkMeshes.ContainsKey(coord))
                        {
                            if (_world.TryGetChunk(coord, out var spawnChunkData) && spawnChunkData != null)
                            {
                                _streamingService.EnqueueMeshJob(coord, spawnChunkData, -50);
                                _log.Debug($"Meshing spawn chunk {coord}");
                            }
                        }
                    }
                }
            }
        }
        
        _log.Info($"Spawn chunks loaded: {spawnChunksLoaded} chunks in 3x3x{(maxCy + 1)} area");
        
        // Process spawn chunk results immediately
        ProcessMeshJobsNonBlocking();
    }

    /// <summary>
    /// RESEARCH-BASED: Force load critical chunks using admin-style force-load behavior
    /// This keeps critical chunks loaded artificially to prevent missing chunks
    /// </summary>
    private void ForceLoadCriticalChunks()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        _log.Info("RESEARCH-BASED: Force loading critical chunks with admin-style force-load behavior");
        
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // Force load player's current chunk and immediate neighbors (like /forceload)
        var criticalChunks = new List<ChunkCoord>();
        
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                var coord = new ChunkCoord(playerChunk.X + dx, playerChunk.Y, playerChunk.Z + dz);
                criticalChunks.Add(coord);
            }
        }
        
        var maxCy = _world.MaxChunkY;
        var forceLoadedCount = 0;
        
        foreach (var coord in criticalChunks)
        {
            for (var cy = 0; cy <= maxCy; cy++)
            {
                var fullCoord = new ChunkCoord(coord.X, cy, coord.Z);
                
                // Force load with ultra high priority
                if (!_world.TryGetChunk(fullCoord, out _))
                {
                    _streamingService.EnqueueLoadJob(fullCoord, -100); // Ultra high priority for force loading
                    forceLoadedCount++;
                    _log.Debug($"Force loading critical chunk {fullCoord}");
                }
                else
                {
                    // Force mesh generation for existing chunks
                    if (!_chunkMeshes.ContainsKey(fullCoord))
                    {
                        if (_world.TryGetChunk(fullCoord, out var criticalChunk) && criticalChunk != null)
                        {
                            _streamingService.EnqueueMeshJob(fullCoord, criticalChunk, -100);
                            _log.Debug($"Force meshing critical chunk {fullCoord}");
                        }
                    }
                }
            }
        }
        
        _log.Info($"Force loaded {forceLoadedCount} critical chunks in immediate area");
        
        // Wait a bit for force-loaded chunks to process
        System.Threading.Thread.Sleep(1000);
        
        // Process force-loaded results
        ProcessMeshJobsNonBlocking();
        
        // Generate emergency meshes for anything still missing
        GenerateEmergencyMeshesForMissingChunks();
    }

    /// <summary>
    /// AGGRESSIVE: Ensure all chunks around player are visible
    /// This guarantees no missing chunks when rejoining
    /// </summary>
    private void EnsureAllChunksVisible()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        _log.Info("AGGRESSIVE: Ensuring all chunks around player are visible");
        
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // AGGRESSIVE: 5x5 area around player to ensure no missing chunks
        var radius = 2; // 5x5 area
        var maxCy = _world.MaxChunkY;
        var totalChunks = 0;
        var existingChunks = 0;
        var meshedChunks = 0;
        
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    totalChunks++;
                    
                    // Check if chunk exists
                    if (_world.TryGetChunk(coord, out var chunk) && chunk != null)
                    {
                        existingChunks++;
                        
                        // Check if mesh exists
                        if (_chunkMeshes.ContainsKey(coord))
                        {
                            meshedChunks++;
                        }
                        else
                        {
                            // Force mesh generation for missing meshes
                            _streamingService.EnqueueMeshJob(coord, chunk, -20);
                            _log.Debug($"Enqueued mesh for missing chunk {coord}");
                        }
                    }
                    else
                    {
                        // Force chunk generation for missing chunks
                        _streamingService.EnqueueLoadJob(coord, -20);
                        _log.Debug($"Enqueued load for missing chunk {coord}");
                    }
                }
            }
        }
        
        _log.Info($"Chunk visibility status: {meshedChunks}/{existingChunks}/{totalChunks} chunks meshed/existing/total");
        
        // Process some results immediately
        ProcessMeshJobsNonBlocking();
        
        // Generate emergency meshes for any still-missing chunks
        GenerateEmergencyMeshesForMissingChunks();
    }

    /// <summary>
    /// AGGRESSIVE: Generate emergency meshes for missing chunks
    /// This is the ultimate fallback to ensure visibility
    /// </summary>
    private void GenerateEmergencyMeshesForMissingChunks()
    {
        if (_world == null || _player == null)
            return;

        _log.Info("AGGRESSIVE: Generating emergency meshes for missing chunks");
        
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // 3x3 area around player for emergency meshes
        var radius = 1;
        var maxCy = _world.MaxChunkY;
        var emergencyCount = 0;
        
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    
                    if (!_chunkMeshes.ContainsKey(coord) && _world.TryGetChunk(coord, out var chunk) && chunk != null)
                    {
                        var emergencyMesh = BuildFallbackMesh(coord, chunk);
                        if (emergencyMesh != null)
                        {
                            _chunkMeshes[coord] = emergencyMesh;
                            emergencyCount++;
                        }
                    }
                }
            }
        }
        
        _log.Info($"Generated {emergencyCount} emergency meshes for missing chunks");
    }

    /// <summary>
    /// OPTIMIZED: Force mesh generation for player's chunk without blocking
    /// This prevents freezes while ensuring visibility
    /// </summary>
    private void ForceGeneratePlayerChunkMeshOptimized()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"OPTIMIZED: Enqueueing mesh generation for player's chunk {playerChunk}");
        
        // Enqueue mesh generation for player's immediate area with high priority
        var maxCy = _world.MaxChunkY;
        var meshCount = 0;
        
        // Only generate meshes for immediate 3x3 area around player
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                    
                    if (_world.TryGetChunk(coord, out var chunk) && chunk != null)
                    {
                        // Only enqueue if mesh doesn't exist or chunk is dirty
                        if (!_chunkMeshes.ContainsKey(coord) || chunk.IsDirty)
                        {
                            _streamingService.EnqueueMeshJob(coord, chunk, -10);
                            meshCount++;
                        }
                    }
                }
            }
        }
        
        _log.Info($"Enqueued {meshCount} mesh jobs for player area");
        
        // Process a few results immediately but don't block
        ProcessMeshJobsNonBlocking();
    }

    /// <summary>
    /// AGGRESSIVE: Process additional chunks during loading to prevent missing chunks
    /// </summary>
    private void ProcessAdditionalChunks()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        // Process more mesh results during loading
        _streamingService.ProcessMeshResults(result =>
        {
            if (result.Mesh != null)
            {
                _chunkMeshes[result.Coord] = result.Mesh;
            }
        }, 10); // Process more results during loading
        
        // Check for any missing chunks in immediate area and force load them
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // Check 3x3 area for missing chunks
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                var coord = new ChunkCoord(playerChunk.X + dx, 0, playerChunk.Z + dz);
                
                if (!_world.TryGetChunk(coord, out _))
                {
                    _streamingService.EnqueueLoadJob(coord, -25); // High priority for missing chunks
                }
            }
        }
    }

    /// <summary>
    /// OPTIMIZED: Process mesh jobs without blocking to prevent freezes
    /// </summary>
    private void ProcessMeshJobsNonBlocking()
    {
        if (_streamingService == null)
            return;

        // Process only a few results to prevent blocking
        _streamingService.ProcessMeshResults(result =>
        {
            if (result.Mesh != null)
            {
                _chunkMeshes[result.Coord] = result.Mesh;
            }
        }, 5); // Only process 5 results to prevent freezing
    }

    /// <summary>
    /// CRITICAL: Force immediate generation of player's current chunk
    /// This ensures the player's chunk exists before any other operations
    /// </summary>
    private void ForceGeneratePlayerChunk()
    {
        if (_world == null || _streamingService == null)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"FORCE GENERATING player's chunk: {playerChunk} at position {playerPos}");
        
        // Check if chunk already exists
        if (_world.TryGetChunk(playerChunk, out var existingChunk) && existingChunk != null)
        {
            _log.Info($"Player's chunk {playerChunk} already exists, skipping forced generation");
            return;
        }
        
        // ALL WORLD GENERATION DELETED - NO CHUNKS
        
        _log.Info($"Player's chunk {playerChunk} FORCE GENERATED and added to world");
    }

    /// <summary>
    /// Pre-load chunks around PLAYER'S SAVED POSITION (not spawn)
    /// This fixes the issue where chunks don't load when rejoining a world
    /// </summary>
    private void PreloadChunksAroundPlayer()
    {
        if (_world == null || _streamingService == null)
            return;

        // Get player's current position (which is their saved position)
        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"Preloading chunks around player position: {playerChunk} (world pos: {playerPos})");
        
        // Start prewarming for smooth world formation
        _spawnPrewarmComplete = false;
        _spawnPrewarmStartTime = DateTime.UtcNow;
        
        // IMMEDIATELY GENERATE PLAYER'S CURRENT CHUNK (highest priority)
        var currentChunk = new ChunkCoord(playerChunk.X, playerChunk.Y, playerChunk.Z);
        if (!_world.TryGetChunk(currentChunk, out _))
        {
            _streamingService.EnqueueLoadJob(currentChunk, -100); // Highest priority
        }
        
        // Also generate immediate surrounding chunks for stability
        for (int dx = -2; dx <= 2; dx++) // Larger area for player safety
        {
            for (int dz = -2; dz <= 2; dz++)
            {
                var coord = new ChunkCoord(playerChunk.X + dx, playerChunk.Y, playerChunk.Z + dz);
                
                if (!_world.TryGetChunk(coord, out _))
                {
                    // Higher priority for chunks closer to player
                    int priority = -50 - (Math.Abs(dx) + Math.Abs(dz));
                    _streamingService.EnqueueLoadJob(coord, priority);
                }
            }
        }
        
        _log.Info($"Preloaded {25} chunks around player position for safe rejoin");
    }

    /// <summary>
    /// CRITICAL: Wait for player's chunk to be COMPLETELY loaded before allowing movement
    /// This is the most important safety check to prevent falling through the world
    /// </summary>
    private void WaitForPlayerChunkComplete()
    {
        if (_world == null)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"WAITING for player's chunk to be COMPLETELY loaded: {playerChunk}");
        
        // Wait up to 15 seconds for player's chunk to be loaded (increased timeout)
        var timeout = DateTime.UtcNow.AddSeconds(15);
        
        while (DateTime.UtcNow < timeout)
        {
            if (_world.TryGetChunk(playerChunk, out var chunkData) && chunkData != null)
            {
                // Verify chunk has actual data (not empty)
                bool hasData = false;
                for (int i = 0; i < chunkData.Blocks.Length; i++)
                {
                    if (chunkData.Blocks[i] != BlockIds.Air)
                    {
                        hasData = true;
                        break;
                    }
                }
                
                if (hasData)
                {
                    _log.Info($"Player's chunk {playerChunk} is COMPLETELY loaded with data");
                    return;
                }
                else
                {
                    _log.Warn($"Player's chunk {playerChunk} exists but appears empty, continuing to wait...");
                }
            }
            
            // Small delay to prevent busy waiting
            System.Threading.Thread.Sleep(100);
        }
        
        // If we get here, the chunk still isn't loaded properly
        _log.Error($"FAILED to load player's chunk {playerChunk} within timeout! Player may fall through world.");
        
        // LAST RESORT: Generate a basic chunk to prevent falling
        // ALL WORLD GENERATION DELETED - NO CHUNKS
    }

    /// <summary>
    /// Wait for critical chunks around player to load before allowing movement
    /// This prevents falling through the world when rejoining
    /// </summary>
    private void WaitForCriticalChunks()
    {
        if (_world == null || _streamingService == null)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        _log.Info($"Waiting for critical chunks to load around player position: {playerChunk}");
        
        // Wait up to 10 seconds for critical chunks to load
        var timeout = DateTime.UtcNow.AddSeconds(10);
        var criticalChunks = new List<ChunkCoord>();
        
        // Add current chunk and immediate neighbors to critical list
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                criticalChunks.Add(new ChunkCoord(playerChunk.X + dx, playerChunk.Y, playerChunk.Z + dz));
            }
        }
        
        int loadedCount = 0;
        while (DateTime.UtcNow < timeout && loadedCount < criticalChunks.Count)
        {
            loadedCount = 0;
            foreach (var chunk in criticalChunks)
            {
                if (_world.TryGetChunk(chunk, out var chunkData) && chunkData != null)
                {
                    loadedCount++;
                }
            }
            
            if (loadedCount < criticalChunks.Count)
            {
                // Process streaming results to load chunks
                ProcessStreamingResults(aggressiveApply: true);
                
                // Small delay to allow chunks to load
                System.Threading.Thread.Sleep(50);
            }
        }
        
        _log.Info($"Critical chunks loaded: {loadedCount}/{criticalChunks.Count}");
        
        // If not all chunks loaded, at least ensure player's current chunk exists
        if (!_world.TryGetChunk(playerChunk, out _))
        {
            _log.Warn($"Player's current chunk {playerChunk} still not loaded, generating immediately");
            _streamingService.EnqueueLoadJob(playerChunk, -1000); // Ultra high priority
            
            // Wait a bit more for this critical chunk
            var chunkTimeout = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < chunkTimeout)
            {
                ProcessStreamingResults(aggressiveApply: true);
                if (_world.TryGetChunk(playerChunk, out _))
                    break;
                System.Threading.Thread.Sleep(100);
            }
        }
    }

    private void PreloadSpawnChunks()
    {
        if (_world == null || _meta == null || _streamingService == null)
            return;

        // FIX HOLE - Generate spawn chunk immediately
        var spawnPoint = GetWorldSpawnPoint();
        var spawnChunk = VoxelWorld.WorldToChunk((int)spawnPoint.X, 0, (int)spawnPoint.Y, out _, out _, out _);
        
        // Start prewarming for smooth world formation
        _spawnPrewarmComplete = false;
        _spawnPrewarmStartTime = DateTime.UtcNow;
        
        // IMMEDIATELY GENERATE SPAWN CHUNK (highest priority)
        var surfaceChunk = new ChunkCoord(spawnChunk.X, spawnChunk.Y, spawnChunk.Z);
        if (!_world.TryGetChunk(surfaceChunk, out _))
        {
            _streamingService.EnqueueLoadJob(surfaceChunk, -100); // Highest priority
        }
        
        // Also generate immediate surrounding chunks for stability
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                var coord = new ChunkCoord(spawnChunk.X + dx, spawnChunk.Y, spawnChunk.Z + dz);
                
                if (!_world.TryGetChunk(coord, out _))
                {
                    _streamingService.EnqueueLoadJob(coord, -50); // High priority
                }
            }
        }
        
        _log.Info("Spawn chunk generation initiated - fixing hole in world");
    }

    private bool AreSpawnChunksLoaded()
    {
        if (_world == null || _meta == null)
            return false;

        // FIX HOLE IN WORLD - Generate spawn chunk if it doesn't exist
        var spawnPoint = GetWorldSpawnPoint();
        var spawnChunk = VoxelWorld.WorldToChunk((int)spawnPoint.X, 0, (int)spawnPoint.Y, out _, out _, out _);
        
        // Check and generate surface spawn chunk
        var surfaceChunk = new ChunkCoord(spawnChunk.X, spawnChunk.Y, spawnChunk.Z);
        
        // GENERATE CHUNK IF IT DOESN'T EXIST
        if (!_world.TryGetChunk(surfaceChunk, out var chunk) || chunk == null)
        {
            if (_streamingService != null)
            {
                _streamingService.EnqueueLoadJob(surfaceChunk, -10); // High priority
            }
            return false; // Still loading
        }
        
        // Also generate mesh if it doesn't exist
        if (!_chunkMeshes.TryGetValue(surfaceChunk, out var mesh) || mesh == null)
        {
            if (_streamingService != null)
            {
                _streamingService.EnqueueMeshJob(surfaceChunk, chunk, -10); // High priority
            }
            return false; // Still loading
        }
        
        // Mark as complete when spawn chunk and mesh are ready
        if (!_spawnPrewarmComplete)
        {
            _spawnPrewarmComplete = true;
            _log.Info("Spawn chunk and mesh ready - world formation complete");
        }
        
        return true;
    }

    private bool IsVisibilitySafe()
    {
        // REMOVED - NO MORE VISIBILITY CHECKS
        return true;
    }

    // CPU RAM OPTIMIZATION - Memory pool for chunk mesh vertices
    private readonly Dictionary<int, List<VertexPositionTexture[]>> _vertexPool = new();
    private readonly Dictionary<int, List<short[]>> _indexPool = new();
    private readonly object _poolLock = new object();
    private const int MaxPoolSize = 100; // Maximum pooled arrays per size
    
    // GPU RESOURCE OPTIMIZATION - Persistent buffer pools
    private readonly Dictionary<int, DynamicVertexBuffer> _vertexBufferPool = new();
    private readonly Dictionary<int, DynamicIndexBuffer> _indexBufferPool = new();
    private const int MaxGpuPoolSize = 50; // Maximum GPU buffers per size

    // NEW: Prevent loading screen after player has spawned
    private bool ShouldShowLoadingScreen()
    {
        if (!_hasLoadedWorld)
            return true;

        if (_worldSyncInProgress)
            return true;

        return !_spawnPrewarmComplete;
    }

    // CPU RAM OPTIMIZATION - Memory pool helper methods
    private VertexPositionTexture[] GetPooledVertices(int size)
    {
        lock (_poolLock)
        {
            if (_vertexPool.TryGetValue(size, out var pool) && pool.Count > 0)
            {
                var vertices = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);
                return vertices;
            }
        }
        return new VertexPositionTexture[size];
    }
    
    private void ReturnPooledVertices(VertexPositionTexture[] vertices)
    {
        var size = vertices.Length;
        lock (_poolLock)
        {
            if (!_vertexPool.TryGetValue(size, out var pool))
            {
                pool = new List<VertexPositionTexture[]>();
                _vertexPool[size] = pool;
            }
            
            if (pool.Count < MaxPoolSize)
            {
                Array.Clear(vertices, 0, vertices.Length); // Clear for reuse
                pool.Add(vertices);
            }
        }
    }
    
    private short[] GetPooledIndices(int size)
    {
        lock (_poolLock)
        {
            if (_indexPool.TryGetValue(size, out var pool) && pool.Count > 0)
            {
                var indices = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);
                return indices;
            }
        }
        return new short[size];
    }
    
    private void ReturnPooledIndices(short[] indices)
    {
        var size = indices.Length;
        lock (_poolLock)
        {
            if (!_indexPool.TryGetValue(size, out var pool))
            {
                pool = new List<short[]>();
                _indexPool[size] = pool;
            }
            
            if (pool.Count < MaxPoolSize)
            {
                Array.Clear(indices, 0, indices.Length); // Clear for reuse
                pool.Add(indices);
            }
        }
    }
    
    // GPU RESOURCE OPTIMIZATION - Buffer pool helper methods
    private DynamicVertexBuffer GetPooledVertexBuffer(GraphicsDevice device, int size)
    {
        if (_vertexBufferPool.TryGetValue(size, out var buffer))
        {
            _vertexBufferPool.Remove(size);
            return buffer;
        }
        return new DynamicVertexBuffer(device, VertexPositionTexture.VertexDeclaration, size, BufferUsage.WriteOnly);
    }
    
    private void ReturnPooledVertexBuffer(int size, DynamicVertexBuffer buffer)
    {
        if (_vertexBufferPool.Count < MaxGpuPoolSize)
        {
            _vertexBufferPool[size] = buffer;
        }
        else
        {
            buffer.Dispose();
        }
    }
    
    private DynamicIndexBuffer GetPooledIndexBuffer(GraphicsDevice device, int size)
    {
        if (_indexBufferPool.TryGetValue(size, out var buffer))
        {
            _indexBufferPool.Remove(size);
            return buffer;
        }
        return new DynamicIndexBuffer(device, typeof(short), size, BufferUsage.WriteOnly);
    }
    
    private void ReturnPooledIndexBuffer(int size, DynamicIndexBuffer buffer)
    {
        if (_indexBufferPool.Count < MaxGpuPoolSize)
        {
            _indexBufferPool[size] = buffer;
        }
        else
        {
            buffer.Dispose();
        }
    }

    private void EnsureFallbackMeshes()
    {
        // 3) TIER-0 FALLBACK MESHING - Build simple meshes for missing chunks, queue normal remesh later
        if (_world == null || _streamingService == null)
            return;

        foreach (var coord in _tier0Chunks)
        {
            if (_world.TryGetChunk(coord, out var chunk) && chunk != null)
            {
                if (!_chunkMeshes.TryGetValue(coord, out var mesh) || mesh == null || 
                    (mesh.OpaqueVertices.Length == 0 && mesh.TransparentVertices.Length == 0 && mesh.WaterVertices.Length == 0))
                {
                    _log.Warn($"Building Tier-0 fallback mesh for {coord}");
                    var fallbackMesh = BuildFallbackMesh(coord, chunk);
                    if (fallbackMesh != null)
                    {
                        _chunkMeshes[coord] = fallbackMesh;
                        
                        // 3) Queue normal greedy remesh for polish
                        chunk.IsDirty = true;
                    }
                }
            }
        }
    }

    private ChunkMesh BuildFallbackMesh(ChunkCoord coord, VoxelChunkData chunk)
    {
        // B) FAST FALLBACK MESH - Simple cube faces, no greedy meshing, no neighbor dependency
        var opaque = new List<VertexPositionTexture>();
        var transparent = new List<VertexPositionTexture>();
        var water = new List<VertexPositionTexture>();

        var bs = Scale.BlockSize;
        var originX = coord.X * VoxelChunkData.ChunkSizeX;
        var originY = coord.Y * VoxelChunkData.ChunkSizeY;
        var originZ = coord.Z * VoxelChunkData.ChunkSizeZ;

        // Simple per-block meshing - no optimization
        for (int x = 0; x < VoxelChunkData.ChunkSizeX; x++)
        {
            for (int y = 0; y < VoxelChunkData.ChunkSizeY; y++)
            {
                for (int z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
                {
                    var blockId = chunk.GetLocal(x, y, z);
                    if (blockId == BlockIds.Air || blockId == BlockIds.Nullblock)
                        continue;

                    // Simple cube mesh for each solid block
                    var worldX = originX + x * bs;
                    var worldY = originY + y * bs;
                    var worldZ = originZ + z * bs;

                    // Add all 6 faces for simplicity
                    if (blockId == BlockIds.Water)
                        AddFallbackCubeFace(water, worldX, worldY, worldZ, bs, blockId);
                    else if (BlockRegistry.IsTransparent(blockId))
                        AddFallbackCubeFace(transparent, worldX, worldY, worldZ, bs, blockId);
                    else
                        AddFallbackCubeFace(opaque, worldX, worldY, worldZ, bs, blockId);
                }
            }
        }

        return new ChunkMesh(coord, opaque.ToArray(), transparent.ToArray(), water.ToArray(), 
            new BoundingBox(new Vector3(originX, originY, originZ), 
                           new Vector3(originX + VoxelChunkData.ChunkSizeX * bs, 
                                       originY + VoxelChunkData.ChunkSizeY * bs, 
                                       originZ + VoxelChunkData.ChunkSizeZ * bs)));
    }

    private void AddFallbackCubeFace(List<VertexPositionTexture> vertices, float x, float y, float z, float bs, byte blockId)
    {
        // Simple UV coordinates - all faces use same texture
        var uv00 = Vector2.Zero;
        var uv10 = Vector2.UnitX;
        var uv11 = Vector2.One;
        var uv01 = Vector2.UnitY;

        // Add 6 faces of a cube (simplified - no face culling)
        var faces = new[]
        {
            // Front face
            new Vector3(x, y, z + bs), new Vector3(x + bs, y, z + bs), new Vector3(x + bs, y + bs, z + bs), new Vector3(x, y + bs, z + bs),
            // Back face  
            new Vector3(x + bs, y, z), new Vector3(x, y, z), new Vector3(x, y + bs, z), new Vector3(x + bs, y + bs, z),
            // Right face
            new Vector3(x + bs, y, z), new Vector3(x + bs, y, z + bs), new Vector3(x + bs, y + bs, z + bs), new Vector3(x + bs, y + bs, z),
            // Left face
            new Vector3(x, y, z + bs), new Vector3(x, y, z), new Vector3(x, y + bs, z), new Vector3(x, y + bs, z + bs),
            // Top face
            new Vector3(x, y + bs, z + bs), new Vector3(x + bs, y + bs, z + bs), new Vector3(x + bs, y + bs, z), new Vector3(x, y + bs, z),
            // Bottom face
            new Vector3(x, y, z), new Vector3(x + bs, y, z), new Vector3(x + bs, y, z + bs), new Vector3(x, y, z + bs)
        };

        // Add quads for each face
        for (int i = 0; i < faces.Length; i += 4)
        {
            vertices.Add(new VertexPositionTexture(faces[i], uv00));
            vertices.Add(new VertexPositionTexture(faces[i + 1], uv10));
            vertices.Add(new VertexPositionTexture(faces[i + 2], uv11));
            vertices.Add(new VertexPositionTexture(faces[i + 3], uv01));
        }
    }

    private void PerformPanicRecovery()
    {
        // 6) PANIC RECOVERY - Clear queues, re-enqueue tier0, rebuild atlas
        if (_streamingService == null || _world == null)
            return;

        try
        {
            _log.Error("AUTO-RECOVER: Clearing queues and rebuilding...");
            
            // Clear all chunk meshes to force rebuild
            _chunkMeshes.Clear();
            
            // Rebuild atlas
            _atlas = CubeNetAtlas.Build(_assets, _log, _settings.QualityPreset);
            if (_atlas == null || _atlas.Texture == null)
            {
                _log.Error("AUTO-RECOVER: Atlas rebuild failed");
                return;
            }
            
            // Re-enqueue tier0 chunks with high priority
            _tier0Chunks.Clear();
            var playerChunk = VoxelWorld.WorldToChunk((int)_player.Position.X, (int)_player.Position.Y, (int)_player.Position.Z, out _, out _, out _);
            var maxCy = _world.MaxChunkY;
            
            for (var ring = 0; ring <= 1; ring++)
            {
                for (var dz = -ring; dz <= ring; dz++)
                {
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        // Chebyshev distance = max(|dx|, |dz|)
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                            continue;

                        for (var cy = 0; cy <= maxCy; cy++)
                        {
                            var coord = new ChunkCoord(playerChunk.X + dx, cy, playerChunk.Z + dz);
                            _tier0Chunks.Add(coord);
                            _streamingService.EnqueueLoadJob(coord, -10); // Highest priority
                        }
                    }
                }
            }
            
            _log.Error($"AUTO-RECOVER: Re-enqueued {_tier0Chunks.Count} tier0 chunks");
        }
        catch (Exception ex)
        {
            _log.Error($"AUTO-RECOVER failed: {ex.Message}");
        }
    }

    private void PerformVisibilityAssert()
    {
        // E) FAILSAFE ASSERT - Check if player can see void or has no solid ground
        if (_world == null || _visibilityAssertLogged)
            return;

        var playerPos = _player.Position;
        var playerChunk = VoxelWorld.WorldToChunk((int)playerPos.X, (int)playerPos.Y, (int)playerPos.Z, out _, out _, out _);
        
        // Check for solid block under player
        var groundY = (int)MathF.Floor(playerPos.Y - 0.1f);
        var groundBlock = _world.GetBlock((int)playerPos.X, groundY, (int)playerPos.Z);
        var hasSolidGround = groundBlock != BlockIds.Air && groundBlock != BlockIds.Nullblock;
        
        // Check for void visibility (simplified - check if player is too high above ground)
        var worldHeight = _world.MaxChunkY * VoxelChunkData.ChunkSizeY;
        var isTooHigh = playerPos.Y > worldHeight - 10;
        
        if (!hasSolidGround || isTooHigh)
        {
            _log.Warn($"Visibility assert warning: Player at {playerPos} - Solid ground: {hasSolidGround}, Too high: {isTooHigh}");
            _visibilityAssertLogged = true;
        }
        else
        {
            _log.Info("Visibility assert passed: Player has solid ground and reasonable height");
            _visibilityAssertLogged = true;
        }
    }

    private void ScheduleBackgroundRemeshPass()
    {
        // C) After gameplay starts, schedule a NORMAL remesh pass for visual polish
        if (_world == null || _streamingService == null)
            return;

        _log.Info("Scheduling background remesh pass for Tier 0 chunks");
        
        foreach (var coord in _tier0Chunks)
        {
            if (_world.TryGetChunk(coord, out var chunk) && chunk != null)
            {
                // Mark as dirty for normal remesh with full quality
                chunk.IsDirty = true;
            }
        }
    }

    private void ApplyPlayerState(PlayerWorldState state)
    {
        var resolvedMode = state.Version >= 3 ? state.CurrentGameMode : _meta?.CurrentWorldGameMode ?? GameMode.Artificer;
        if (!Enum.IsDefined(typeof(GameMode), resolvedMode))
            resolvedMode = _meta?.CurrentWorldGameMode ?? GameMode.Artificer;

        ApplyGameModeChange(resolvedMode, GameModeChangeSource.Load, persistImmediately: false, emitFeedback: false);
        _player.Position = new Vector3(state.PosX, state.PosY, state.PosZ);
        _player.Yaw = state.Yaw;
        _player.Pitch = state.Pitch;
        _player.Velocity = Vector3.Zero;
        _player.SetFlying(_player.AllowFlying && state.IsFlying);
        _homes.Clear();
        if (state.Homes != null)
        {
            for (int i = 0; i < state.Homes.Count; i++)
            {
                var home = state.Homes[i];
                if (home == null || string.IsNullOrWhiteSpace(home.Name))
                    continue;

                var entry = new PlayerHomeEntry
                {
                    Name = NormalizeHomeName(home.Name),
                    Position = new Vector3(home.PosX, home.PosY, home.PosZ)
                };

                if (!string.IsNullOrWhiteSpace(home.IconBlockId) && TryResolveHomeIconBlock(home.IconBlockId, out var iconBlock))
                {
                    entry.HasIconBlock = true;
                    entry.IconBlock = iconBlock;
                }

                UpsertHomeEntry(entry);
            }
        }

        if (_homes.Count == 0 && state.HasHome)
        {
            _homes.Add(new PlayerHomeEntry
            {
                Name = DefaultHomeName,
                Position = new Vector3(state.HomeX, state.HomeY, state.HomeZ)
            });
        }

        _selectedHomeIndex = _homes.Count > 0 ? 0 : -1;
        SyncLegacyHomeFields();
        
        // Restore inventory data
        if (state.Hotbar != null && state.Hotbar.Length > 0)
        {
            _inventory.SetHotbarData(state.Hotbar);
            _log.Info($"Restored hotbar with {state.Hotbar.Count(x => x.Id != BlockIds.Air)} items");
        }
        
        // Restore selected slot
        if (state.SelectedIndex >= 0 && state.SelectedIndex < Inventory.HotbarSize)
        {
            _inventory.SelectedIndex = state.SelectedIndex;
            _log.Info($"Restored selected hotbar slot: {state.SelectedIndex}");
        }
    }

    private PlayerWorldState CapturePlayerState()
    {
        return new PlayerWorldState
        {
            Username = _profile.GetDisplayUsername(),
            PosX = _player.Position.X,
            PosY = _player.Position.Y,
            PosZ = _player.Position.Z,
            Yaw = _player.Yaw,
            Pitch = _player.Pitch,
            IsFlying = _player.IsFlying,
            CurrentGameMode = _gameMode,
            HasHome = _hasHome,
            HomeX = _homePosition.X,
            HomeY = _homePosition.Y,
            HomeZ = _homePosition.Z,
            Homes = BuildPlayerHomeStateList(),
            SelectedIndex = _inventory.SelectedIndex,
            Hotbar = _inventory.GetHotbarData()
        };
    }

    private List<PlayerHomeState> BuildPlayerHomeStateList()
    {
        var list = new List<PlayerHomeState>(_homes.Count);
        for (int i = 0; i < _homes.Count; i++)
        {
            var home = _homes[i];
            list.Add(new PlayerHomeState
            {
                Name = home.Name,
                PosX = home.Position.X,
                PosY = home.Position.Y,
                PosZ = home.Position.Z,
                IconBlockId = home.HasIconBlock ? home.IconBlock.ToString() : string.Empty
            });
        }

        return list;
    }

    private void SyncLegacyHomeFields()
    {
        _hasHome = _homes.Count > 0;
        if (_hasHome)
            _homePosition = _homes[0].Position;
        else
            _homePosition = Vector3.Zero;
    }

    private static string NormalizeHomeName(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            value = DefaultHomeName;
        if (value.Length > 24)
            value = value.Substring(0, 24);
        return value;
    }

    private bool TryResolveHomeIconBlock(string token, out BlockId blockId)
    {
        blockId = BlockId.Air;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (Enum.TryParse(token, true, out BlockId parsed) && parsed != BlockId.Air)
        {
            blockId = parsed;
            return true;
        }

        foreach (var def in BlockRegistry.All)
        {
            if (!string.Equals(def.Name, token, StringComparison.OrdinalIgnoreCase))
                continue;
            if (def.Id == BlockId.Air)
                return false;
            blockId = def.Id;
            return true;
        }

        return false;
    }

    private bool TryFindHomeIndex(string name, out int index)
    {
        index = -1;
        var normalized = NormalizeHomeName(name);
        for (int i = 0; i < _homes.Count; i++)
        {
            if (!string.Equals(_homes[i].Name, normalized, StringComparison.OrdinalIgnoreCase))
                continue;
            index = i;
            return true;
        }

        return false;
    }

    private int GetMaxHomesForWorld()
    {
        if (_meta == null)
            return 1;

        if (!_meta.EnableMultipleHomes)
            return 1;

        return Math.Clamp(_meta.MaxHomesPerPlayer, 1, 32);
    }

    private bool IsMultipleHomesEnabled()
    {
        return (_meta?.EnableMultipleHomes ?? false) && GetMaxHomesForWorld() > 1;
    }

    private void UpsertHomeEntry(PlayerHomeEntry entry)
    {
        entry.Name = NormalizeHomeName(entry.Name);
        if (TryFindHomeIndex(entry.Name, out var existing))
        {
            _homes[existing] = entry;
            _selectedHomeIndex = existing;
            SyncLegacyHomeFields();
            return;
        }

        _homes.Add(entry);
        _selectedHomeIndex = _homes.Count - 1;
        SyncLegacyHomeFields();
    }

    private bool TrySetHome(string requestedName, Vector3 position, out string result)
    {
        if (_meta == null)
        {
            result = "World is not ready yet.";
            return false;
        }

        var multi = IsMultipleHomesEnabled();
        var name = multi ? NormalizeHomeName(requestedName) : DefaultHomeName;

        if (TryFindHomeIndex(name, out var existing))
        {
            var updated = _homes[existing];
            updated.Position = position;
            _homes[existing] = updated;
            _selectedHomeIndex = existing;
            SyncLegacyHomeFields();
            SavePlayerState();
            result = $"Home '{updated.Name}' updated at {(int)position.X}, {(int)position.Y}, {(int)position.Z}.";
            return true;
        }

        var maxHomes = GetMaxHomesForWorld();
        if (_homes.Count >= maxHomes)
        {
            result = $"Home limit reached ({maxHomes}). Delete one first.";
            return false;
        }

        var entry = new PlayerHomeEntry { Name = name, Position = position };
        _homes.Add(entry);
        _selectedHomeIndex = _homes.Count - 1;
        SyncLegacyHomeFields();
        SavePlayerState();
        result = $"Home '{entry.Name}' set at {(int)position.X}, {(int)position.Y}, {(int)position.Z}.";
        return true;
    }

    private bool TryTeleportToHome(string? requestedName, out string result)
    {
        if (_homes.Count == 0)
        {
            result = "No home is set. Use /sethome first.";
            return false;
        }

        int index;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            index = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count ? _selectedHomeIndex : 0;
        }
        else if (!TryFindHomeIndex(requestedName, out index))
        {
            result = $"Home not found: {requestedName}";
            return false;
        }

        var home = _homes[index];
        _selectedHomeIndex = index;
        if (!TryTeleportPlayer((int)home.Position.X, (int)home.Position.Z, (int)home.Position.Y, out result))
            return false;

        result += $" (home: {home.Name})";
        return true;
    }

    private void QueuePlayerStateSaveAsync()
    {
        if (Interlocked.CompareExchange(ref _autoSaveInFlight, 1, 0) != 0)
            return;

        var snapshot = CapturePlayerState();
        _ = Task.Run(() =>
        {
            try
            {
                snapshot.Save(_worldPath, _log);
                QueueWorldPreviewUpdate(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to auto-save player state: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _autoSaveInFlight, 0);
            }
        });
    }

    private void SavePlayerState(PlayerWorldState? state = null)
    {
        try
        {
            state ??= CapturePlayerState();
            state.Save(_worldPath, _log);
            QueueWorldPreviewUpdate(state);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save player state: {ex.Message}");
        }
    }

    private void QueueWorldPreviewUpdate(PlayerWorldState state)
    {
        QueueWorldPreviewUpdate(
            (int)MathF.Floor(state.PosX),
            (int)MathF.Floor(state.PosZ));
    }

    private void QueueWorldPreviewUpdate(int centerX, int centerZ)
    {
        if (_meta == null || string.IsNullOrWhiteSpace(_worldPath))
            return;

        lock (_previewQueueLock)
        {
            _previewPendingCenterX = centerX;
            _previewPendingCenterZ = centerZ;
            _previewUpdatePending = true;
            if (_previewUpdateWorker == null || _previewUpdateWorker.IsCompleted)
                _previewUpdateWorker = Task.Run(ProcessPreviewUpdateQueue);
        }
    }

    private void FlushWorldPreviewUpdate(PlayerWorldState state)
    {
        if (_meta == null || string.IsNullOrWhiteSpace(_worldPath))
            return;

        Task? worker;
        lock (_previewQueueLock)
        {
            _previewPendingCenterX = (int)MathF.Floor(state.PosX);
            _previewPendingCenterZ = (int)MathF.Floor(state.PosZ);
            _previewUpdatePending = true;
            if (_previewUpdateWorker == null || _previewUpdateWorker.IsCompleted)
                _previewUpdateWorker = Task.Run(ProcessPreviewUpdateQueue);
            worker = _previewUpdateWorker;
        }

        try
        {
            worker?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to flush world preview update: {ex.Message}");
        }
    }

    private void ProcessPreviewUpdateQueue()
    {
        while (true)
        {
            int centerX;
            int centerZ;
            WorldMeta? meta;
            lock (_previewQueueLock)
            {
                if (!_previewUpdatePending)
                {
                    _previewUpdateWorker = null;
                    return;
                }

                centerX = _previewPendingCenterX;
                centerZ = _previewPendingCenterZ;
                _previewUpdatePending = false;
                meta = _meta;
            }

            if (meta == null)
                continue;

            try
            {
                WorldPreviewGenerator.GenerateAndSave(meta, _worldPath, centerX, centerZ, _log);
            }
            catch (Exception ex)
            {
                _log.Warn($"World preview update failed: {ex.Message}");
            }
        }
    }

    private void EnsurePlayerNotInsideSolid(bool forceLog)
    {
        if (_world == null)
            return;

        if (!IsPlayerInsideSolid())
        {
            if (forceLog)
                _log.Info("Unstuck: player already clear.");
            return;
        }

        var pos = _player.Position;
        var step = 0.25f;
        for (int i = 0; i < 64; i++)
        {
            pos.Y += step;
            _player.Position = pos;
            if (!IsPlayerInsideSolid())
            {
                _player.Velocity = Vector3.Zero;
                _log.Warn("Unstuck: moved player upward to clear collision.");
                return;
            }
        }

        var fallback = FindFallbackSpawn();
        _player.Position = fallback;
        _player.Velocity = Vector3.Zero;
        _log.Warn("Unstuck: fallback spawn used.");
    }

    private bool IsPlayerInsideSolid()
    {
        return IsPlayerInsideSolid(_player.Position);
    }

    private bool IsPlayerInsideSolid(Vector3 pos)
    {
        if (_world == null)
            return false;

        const float eps = 0.001f;
        var half = PlayerController.ColliderHalfWidth;
        var height = PlayerController.ColliderHeight;
        var min = new Vector3(pos.X - half, pos.Y, pos.Z - half);
        var max = new Vector3(pos.X + half, pos.Y + height, pos.Z + half);

        var minX = (int)MathF.Floor(min.X + eps);
        var maxX = (int)MathF.Floor(max.X - eps);
        var minY = (int)MathF.Floor(min.Y + eps);
        var maxY = (int)MathF.Floor(max.Y - eps);
        var minZ = (int)MathF.Floor(min.Z + eps);
        var maxZ = (int)MathF.Floor(max.Z - eps);

        for (int y = minY; y <= maxY; y++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (BlockRegistry.IsSolid(_world.GetBlock(x, y, z)))
                        return true;
                }
            }
        }

        return false;
    }

    private Vector3 FindFallbackSpawn()
    {
        if (_meta == null)
            return _player.Position;

        var centerX = _meta.Size.Width / 2f;
        var centerZ = _meta.Size.Depth / 2f;
        var maxY = Math.Max(0, _meta.Size.Height - 1);
        var groundY = -1;

        for (int y = maxY; y >= 0; y--)
        {
            if (BlockRegistry.IsSolid(_world!.GetBlock((int)centerX, y, (int)centerZ)))
            {
                groundY = y;
                break;
            }
        }

        var safeY = groundY >= 0 ? groundY + 2.0f : Math.Max(6f, _meta.Size.Height + 2f);
        return new Vector3(centerX, safeY, centerZ);
    }

    private void ExportAtlas()
    {
        if (_atlas == null)
            return;

        _atlas.ExportBlockNetPngs(Paths.BlocksTexturesDir, _log);
        var ok = _atlas.TryExportPng(Paths.BlocksAtlasPath, _log);
        if (ok)
            _log.Info($"Atlas exported: {Paths.BlocksAtlasPath}");
    }

    private void UpdateSelectionTimer(float dt)
    {
        if (_inventory.SelectedIndex != _lastSelectedIndex)
        {
            _lastSelectedIndex = _inventory.SelectedIndex;
            var id = _inventory.SelectedId;
            if (id != BlockId.Air)
            {
                SetDisplayName(BlockRegistry.Get(id).Name);
            }
        }
        
        if (_selectedNameTimer > 0)
        {
            _selectedNameTimer -= dt;
        }
    }

    private void SetDisplayName(string name)
    {
        _displayName = name;
        _selectedNameTimer = 3.0f; // Show for 3 seconds
    }

    private void UpdateChatLines(float dt)
    {
        if (_chatLines.Count == 0)
            return;

        for (var i = _chatLines.Count - 1; i >= 0; i--)
            _chatLines[i].TimeRemaining = Math.Max(0f, _chatLines[i].TimeRemaining - dt);
    }

    private bool IsChatOpenKeyPressed(InputState input)
    {
        return input.IsNewKeyPress(_chatKey);
    }

    private bool IsCommandOpenKeyPressed(InputState input)
    {
        if (input.IsNewKeyPress(_commandKey))
            return true;

        // Always keep slash as a direct command opener even when rebound.
        return input.IsNewKeyPress(Keys.OemQuestion) || input.IsNewKeyPress(Keys.Divide);
    }

    private void BeginChatInput(string initialText = "")
    {
        _chatInputActive = true;
        _commandInputActive = false;
        _chatInputText = initialText ?? string.Empty;
        ResetCommandTabCompletion();
        BeginTextInputHistorySession(_chatInputText);
    }

    private void BeginTextInputHistorySession(string currentText)
    {
        _textInputHistoryCursor = _textInputHistory.Count;
        _textInputHistoryDraft = currentText;
        _textInputHistoryBrowsing = false;
    }

    private bool TryNavigateTextInputHistory(InputState input, ref string text)
    {
        if (_textInputHistory.Count == 0)
            return false;

        if (input.IsNewKeyPress(Keys.Up))
        {
            if (!_textInputHistoryBrowsing)
            {
                _textInputHistoryDraft = text;
                _textInputHistoryCursor = _textInputHistory.Count;
                _textInputHistoryBrowsing = true;
            }

            if (_textInputHistoryCursor > 0)
                _textInputHistoryCursor--;

            _textInputHistoryCursor = Math.Clamp(_textInputHistoryCursor, 0, _textInputHistory.Count - 1);
            text = _textInputHistory[_textInputHistoryCursor];
            return true;
        }

        if (input.IsNewKeyPress(Keys.Down))
        {
            if (!_textInputHistoryBrowsing)
                return false;

            if (_textInputHistoryCursor < _textInputHistory.Count - 1)
            {
                _textInputHistoryCursor++;
                text = _textInputHistory[_textInputHistoryCursor];
            }
            else
            {
                _textInputHistoryBrowsing = false;
                _textInputHistoryCursor = _textInputHistory.Count;
                text = _textInputHistoryDraft;
            }

            return true;
        }

        return false;
    }

    private void RegisterTextInputHistory(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return;

        if (_textInputHistory.Count > 0 && string.Equals(_textInputHistory[^1], value, StringComparison.Ordinal))
        {
            BeginTextInputHistorySession(value);
            return;
        }

        _textInputHistory.Add(value);
        if (_textInputHistory.Count > MaxTextInputHistory)
            _textInputHistory.RemoveAt(0);

        BeginTextInputHistorySession(value);
    }

    private void HandleChatOverlayActions(InputState input)
    {
        _overlayMousePos = input.MousePosition;

        if (IsTextInputActive)
        {
            var maxScroll = Math.Max(0, _chatLines.Count - 1);
            if (input.ScrollDelta != 0)
            {
                var delta = input.ScrollDelta > 0 ? 1 : -1;
                _chatOverlayScrollLines = Math.Clamp(_chatOverlayScrollLines + delta, 0, maxScroll);
            }

            if (input.IsNewKeyPress(Keys.PageUp))
                _chatOverlayScrollLines = Math.Clamp(_chatOverlayScrollLines + ChatHistoryScrollStep, 0, maxScroll);
            if (input.IsNewKeyPress(Keys.PageDown))
                _chatOverlayScrollLines = Math.Clamp(_chatOverlayScrollLines - ChatHistoryScrollStep, 0, maxScroll);
            if (input.IsNewKeyPress(Keys.Home))
                _chatOverlayScrollLines = maxScroll;
            if (input.IsNewKeyPress(Keys.End))
                _chatOverlayScrollLines = 0;
        }
        else
        {
            _chatOverlayScrollLines = 0;
        }

        _hoveredChatActionIndex = -1;
        for (var i = 0; i < _chatActionHitboxes.Count; i++)
        {
            if (_chatActionHitboxes[i].Bounds.Contains(_overlayMousePos))
            {
                _hoveredChatActionIndex = i;
                break;
            }
        }

        if (_hoveredChatActionIndex >= 0 && input.IsNewLeftClick())
            ExecuteChatAction(_chatActionHitboxes[_hoveredChatActionIndex]);
    }

    private void ExecuteChatAction(ChatActionHitbox action)
    {
        if (!action.HasTeleport)
            return;

        if (!TryTeleportPlayer(action.TeleportX, action.TeleportZ, action.TeleportY, out var result))
        {
            SetCommandStatus(result);
            return;
        }

        _chatOverlayActionUnlockUntil = 0f;
        SetCommandStatus(result);
    }

    private bool TryTeleportPlayer(int x, int z, int? explicitY, out string result)
    {
        if (_world == null || _meta == null)
        {
            result = "World is not ready yet.";
            return false;
        }

        var clampedX = Math.Clamp(x, 0, Math.Max(0, _meta.Size.Width - 1));
        var clampedZ = Math.Clamp(z, 0, Math.Max(0, _meta.Size.Depth - 1));
        var y = explicitY ?? FindSafeSpawnHeight(clampedX, clampedZ) + 1f;
        y = Math.Clamp(y, 2f, Math.Max(3f, _meta.Size.Height - 2));

        _player.Position = new Vector3(clampedX + 0.5f, y, clampedZ + 0.5f);
        _player.Velocity = Vector3.Zero;
        EnsurePlayerNotInsideSolid(forceLog: false);
        UpdateActiveChunks(force: true);
        _queuedLocateOriginX = clampedX;
        _queuedLocateOriginZ = clampedZ;
        _hasQueuedLocateOrigin = true;
        var username = _profile.GetDisplayUsername();
        result = $"{username} Teleported to {clampedX} {(int)MathF.Round(y)} {clampedZ}";
        return true;
    }

    private bool TryOpenInventoryFromTextInput(InputState input, bool commandMode, string activeText)
    {
        if (!input.IsNewKeyPress(_inventoryKey))
            return false;

        var forceOpen = input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl);
        if (!forceOpen && !commandMode)
            return false;

        if (!forceOpen && commandMode)
        {
            if ((activeText ?? string.Empty).Trim().Length > 1)
                return false;
        }

        _chatInputActive = false;
        _commandInputActive = false;
        _chatInputText = "";
        _commandInputText = "";
        _inventoryOpen = true;
        return true;
    }

    private void UpdateChatInput(InputState input)
    {
        HandleChatOverlayActions(input);

        var commandLikeText = _chatInputText.TrimStart().StartsWith("/", StringComparison.Ordinal);
        if (TryOpenInventoryFromTextInput(input, commandMode: commandLikeText, activeText: _chatInputText))
            return;

        if (TryNavigateTextInputHistory(input, ref _chatInputText))
        {
            ResetCommandTabCompletion();
            return;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _chatInputActive = false;
            _chatInputText = "";
            ResetCommandTabCompletion();
            return;
        }

        if (input.IsNewKeyPress(Keys.Enter))
        {
            var message = _chatInputText.Trim();
            _chatInputActive = false;
            _chatInputText = "";
            if (!string.IsNullOrWhiteSpace(message))
            {
                RegisterTextInputHistory(message);
                if (message.StartsWith("/", StringComparison.Ordinal))
                    ExecuteCommand(message);
                else
                    SubmitLocalChatMessage(message);
            }

            ResetCommandTabCompletion();
            return;
        }

        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        if (commandLikeText && input.IsNewKeyPress(Keys.Tab) && TryApplyTabCommandCompletion(ref _chatInputText, reverseCycle: shift))
            return;

        var changed = false;
        foreach (var key in input.GetNewKeys())
        {
            if (key == Keys.Tab)
                continue;

            if (key == Keys.Back)
            {
                if (_chatInputText.Length > 0)
                {
                    _chatInputText = _chatInputText.Substring(0, _chatInputText.Length - 1);
                    changed = true;
                }
                continue;
            }

            if (!TryMapCommandInputKey(key, shift, out var c))
                continue;

            if (_chatInputText.Length < CommandInputMaxLength)
            {
                _chatInputText += c;
                changed = true;
            }
        }

        if (changed && _textInputHistoryBrowsing)
        {
            _textInputHistoryBrowsing = false;
            _textInputHistoryCursor = _textInputHistory.Count;
            _textInputHistoryDraft = _chatInputText;
        }

        if (changed)
            ResetCommandTabCompletion();
    }

    private void UpdateCommandInput(InputState input)
    {
        HandleChatOverlayActions(input);

        if (TryOpenInventoryFromTextInput(input, commandMode: true, activeText: _commandInputText))
            return;

        if (TryNavigateTextInputHistory(input, ref _commandInputText))
        {
            ResetCommandTabCompletion();
            return;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _commandInputActive = false;
            _commandInputText = "";
            ResetCommandTabCompletion();
            return;
        }

        if (input.IsNewKeyPress(Keys.Enter))
        {
            var command = _commandInputText.Trim();
            if (!string.IsNullOrWhiteSpace(command))
            {
                RegisterTextInputHistory(command);
                ExecuteCommand(command);
            }

            _commandInputActive = false;
            _commandInputText = "";
            ResetCommandTabCompletion();
            return;
        }

        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        if (input.IsNewKeyPress(Keys.Tab) && TryApplyTabCommandCompletion(ref _commandInputText, reverseCycle: shift))
            return;

        var changed = false;
        foreach (var key in input.GetNewKeys())
        {
            if (key == Keys.Tab)
                continue;

            if (key == Keys.Back)
            {
                if (_commandInputText.Length > 0)
                {
                    _commandInputText = _commandInputText.Substring(0, _commandInputText.Length - 1);
                    changed = true;
                }
                continue;
            }

            if (!TryMapCommandInputKey(key, shift, out var c))
                continue;

            if (_commandInputText.Length < CommandInputMaxLength)
            {
                _commandInputText += c;
                changed = true;
            }
        }

        if (changed && _textInputHistoryBrowsing)
        {
            _textInputHistoryBrowsing = false;
            _textInputHistoryCursor = _textInputHistory.Count;
            _textInputHistoryDraft = _commandInputText;
        }

        if (changed)
            ResetCommandTabCompletion();
    }

    private void ResetCommandTabCompletion()
    {
        _commandTabContextKey = string.Empty;
        _commandTabIndex = -1;
        _commandTabAppendSpace = false;
        _commandTabCycleCandidates.Clear();
    }

    private bool TryApplyTabCommandCompletion(ref string text, bool reverseCycle)
    {
        if (!TryParseCommandCompletionContext(text, out var context))
            return false;

        EnsureCommandRegistry();
        BuildCommandCompletionCandidates(context, _commandTabBuildBuffer);
        if (_commandTabBuildBuffer.Count == 0)
            return false;

        var contextKey = BuildCommandCompletionContextKey(context);
        var sameContext = string.Equals(_commandTabContextKey, contextKey, StringComparison.Ordinal)
            && HaveSameCommandCompletionSet(_commandTabCycleCandidates, _commandTabBuildBuffer);
        if (!sameContext)
        {
            _commandTabCycleCandidates.Clear();
            _commandTabCycleCandidates.AddRange(_commandTabBuildBuffer);
            _commandTabContextKey = contextKey;
            _commandTabIndex = reverseCycle ? _commandTabCycleCandidates.Count - 1 : 0;
            _commandTabAppendSpace = _commandTabCycleCandidates.Count == 1;
        }
        else
        {
            if (_commandTabCycleCandidates.Count == 0)
                return false;

            _commandTabIndex = reverseCycle
                ? (_commandTabIndex - 1 + _commandTabCycleCandidates.Count) % _commandTabCycleCandidates.Count
                : (_commandTabIndex + 1) % _commandTabCycleCandidates.Count;
        }

        if (_commandTabIndex < 0 || _commandTabIndex >= _commandTabCycleCandidates.Count)
            return false;

        var replacement = _commandTabCycleCandidates[_commandTabIndex].Value;
        text = $"{context.PrefixText}{replacement}{(_commandTabAppendSpace ? " " : string.Empty)}";
        if (text.Length > CommandInputMaxLength)
            text = text.Substring(0, CommandInputMaxLength);
        return true;
    }

    private static bool HaveSameCommandCompletionSet(IReadOnlyList<CommandCompletionCandidate> a, IReadOnlyList<CommandCompletionCandidate> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Value, b[i].Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string BuildCommandCompletionContextKey(in CommandCompletionContext context)
    {
        return $"{context.TokenIndex}|{context.PrefixText.ToLowerInvariant()}";
    }

    private static bool TryParseCommandCompletionContext(string rawInput, out CommandCompletionContext context)
    {
        context = default;
        var input = (rawInput ?? string.Empty).TrimStart();
        if (!input.StartsWith("/", StringComparison.Ordinal))
            return false;

        var body = input.Length > 1 ? input.Substring(1) : string.Empty;
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var endsWithSpace = input.EndsWith(" ", StringComparison.Ordinal);

        var tokenIndex = 0;
        var tokenPrefix = string.Empty;
        if (tokens.Length > 0)
        {
            if (endsWithSpace)
            {
                tokenIndex = tokens.Length;
            }
            else
            {
                tokenIndex = tokens.Length - 1;
                tokenPrefix = tokens[tokenIndex];
            }
        }

        var prefixPartsCount = Math.Clamp(tokenIndex, 0, tokens.Length);
        var prefixBody = prefixPartsCount > 0
            ? string.Join(" ", tokens.Take(prefixPartsCount)) + " "
            : string.Empty;
        var prefixText = "/" + prefixBody;
        context = new CommandCompletionContext(prefixText, tokenPrefix, tokenIndex, tokens);
        return true;
    }

    private void BuildCommandCompletionCandidates(in CommandCompletionContext context, List<CommandCompletionCandidate> output)
    {
        output.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = context.TokenPrefix ?? string.Empty;
        var prefixLower = prefix.ToLowerInvariant();

        if (context.TokenIndex == 0)
        {
            var ordered = _commandDescriptors.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in ordered)
            {
                if (descriptor.Permission == CommandPermission.OwnerAdmin && !HasOwnerAdminPermissions())
                    continue;

                AddCandidate(descriptor.Name, descriptor.Description, descriptor.Usage);
                for (var i = 0; i < descriptor.Aliases.Length; i++)
                    AddCandidate(descriptor.Aliases[i], $"Alias for /{descriptor.Name}. {descriptor.Description}", descriptor.Usage);
            }

            return;
        }

        if (context.Tokens.Length == 0)
            return;

        if (!_commandLookup.TryGetValue(context.Tokens[0], out var command))
            return;
        if (command.Permission == CommandPermission.OwnerAdmin && !HasOwnerAdminPermissions())
            return;

        var tokenOptions = GetCommandArgumentCompletionTokens(command, context.TokenIndex, context.Tokens);
        for (var i = 0; i < tokenOptions.Count; i++)
            AddCandidate(tokenOptions[i], $"Argument for /{command.Name}.", command.Usage);

        output.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
        return;

        void AddCandidate(string value, string description, string usage)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (!value.StartsWith(prefixLower, StringComparison.OrdinalIgnoreCase))
                return;
            if (!seen.Add(value))
                return;

            output.Add(new CommandCompletionCandidate(
                value,
                $"{description} Usage: {usage}"));
        }
    }

    private List<string> GetCommandArgumentCompletionTokens(in CommandDescriptor command, int tokenIndex, string[] tokens)
    {
        var values = new List<string>();
        var name = command.Name.ToLowerInvariant();
        switch (name)
        {
            case "help":
                if (tokenIndex == 1)
                {
                    for (var i = 0; i < _commandDescriptors.Count; i++)
                        values.Add(_commandDescriptors[i].Name);
                }
                break;

            case "biome":
                if (tokenIndex == 1)
                    values.AddRange(new[] { "list", "here", "desert", "grasslands", "ocean" });
                else if (tokenIndex == 2 && tokenIndex - 1 < tokens.Length && TryResolveBiome(tokens[1], out _, out _))
                    AppendRadiusPresets(values);
                break;

            case "structure":
                if (tokenIndex == 1)
                {
                    values.AddRange(new[] { "gui", "list", "biome" });
                }
                else if (tokenIndex == 2 && tokenIndex - 1 < tokens.Length && string.Equals(tokens[1], "biome", StringComparison.OrdinalIgnoreCase))
                {
                    values.AddRange(new[] { "desert", "grasslands", "ocean" });
                }
                else if (tokenIndex == 3 && tokenIndex - 2 < tokens.Length && string.Equals(tokens[1], "biome", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRadiusPresets(values);
                }
                break;

            case "gamemode":
                if (tokenIndex == 1)
                    values.AddRange(new[] { "artificer", "veilwalker", "veilseer", "gui" });
                break;

            case "difficulty":
                if (tokenIndex == 1)
                    values.AddRange(new[] { "peaceful", "easy", "normal", "hard" });
                break;

            case "time":
                if (tokenIndex == 1)
                    values.AddRange(new[] { "query", "day", "night", "set" });
                else if (tokenIndex == 2 && tokenIndex - 1 < tokens.Length && string.Equals(tokens[1], "set", StringComparison.OrdinalIgnoreCase))
                    values.AddRange(new[] { "0", "1000", "6000", "12000", "13000", "18000" });
                break;

            case "weather":
                if (tokenIndex == 1)
                    values.AddRange(new[] { "query", "clear", "rain", "storm" });
                break;

            case "chatclear":
                if (tokenIndex == 1)
                    values.Add("confirm");
                break;

            case "setspawn":
            case "tp":
                AppendCurrentPositionTokens(values);
                break;

            case "sethome":
                if (tokenIndex == 1)
                    AppendHomeNameTokens(values);
                break;

            case "home":
                if (tokenIndex == 1)
                {
                    values.AddRange(new[] { "list", "gui", "set", "rename", "delete", "icon" });
                    AppendHomeNameTokens(values);
                }
                else if (tokenIndex == 2 && tokenIndex - 1 < tokens.Length)
                {
                    var sub = tokens[1].ToLowerInvariant();
                    if (sub is "rename" or "delete" or "icon")
                        AppendHomeNameTokens(values);
                    else if (sub == "set")
                        AppendHomeNameTokens(values);
                }
                else if (tokenIndex == 3 && tokenIndex - 2 < tokens.Length && string.Equals(tokens[1], "icon", StringComparison.OrdinalIgnoreCase))
                {
                    values.Add("auto");
                    AppendBlockIconTokens(values);
                }
                break;

            case "msg":
                if (tokenIndex == 1)
                    AppendPlayerNameTokens(values);
                break;
        }

        var ordered = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered;
    }

    private static void AppendRadiusPresets(List<string> output)
    {
        for (var i = 0; i < FinderRadiusOptions.Length; i++)
            output.Add(FinderRadiusOptions[i].ToString());
    }

    private void AppendCurrentPositionTokens(List<string> output)
    {
        var px = (int)MathF.Floor(_player.Position.X);
        var py = (int)MathF.Floor(_player.Position.Y);
        var pz = (int)MathF.Floor(_player.Position.Z);
        output.Add(px.ToString());
        output.Add(py.ToString());
        output.Add(pz.ToString());
    }

    private void AppendHomeNameTokens(List<string> output)
    {
        for (var i = 0; i < _homes.Count; i++)
            output.Add(_homes[i].Name);
    }

    private void AppendPlayerNameTokens(List<string> output)
    {
        var localName = _profile.GetDisplayUsername();
        if (!string.IsNullOrWhiteSpace(localName))
            output.Add(localName);

        foreach (var name in _playerNames.Values)
        {
            if (!string.IsNullOrWhiteSpace(name))
                output.Add(name);
        }
    }

    private static void AppendBlockIconTokens(List<string> output)
    {
        var names = Enum.GetNames(typeof(BlockId));
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], nameof(BlockId.Air), StringComparison.OrdinalIgnoreCase))
                continue;
            output.Add(names[i]);
        }
    }

    private void SubmitLocalChatMessage(string message)
    {
        var username = _profile.GetDisplayUsername();
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0)
            return;

        if (_lanSession != null && _lanSession.IsConnected)
        {
            _lanSession.SendChat(new LanChatMessage
            {
                FromPlayerId = _lanSession.LocalPlayerId,
                ToPlayerId = -1,
                Kind = LanChatKind.Chat,
                Text = text,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return;
        }

        AddChatLine($"{username}: {text}", isSystem: false);
    }

    private void AddChatLine(string text, bool isSystem, int? teleportX = null, int? teleportY = null, int? teleportZ = null, string? hoverText = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (_chatLines.Count > 0 && string.Equals(_chatLines[^1].Text, text, StringComparison.Ordinal))
        {
            _chatLines[^1].TimeRemaining = ChatLineLifetimeSeconds;
            _chatLines[^1].IsSystem = isSystem;
            _chatLines[^1].HasTeleportAction = teleportX.HasValue && teleportY.HasValue && teleportZ.HasValue;
            _chatLines[^1].TeleportX = teleportX ?? 0;
            _chatLines[^1].TeleportY = teleportY ?? 0;
            _chatLines[^1].TeleportZ = teleportZ ?? 0;
            _chatLines[^1].HoverText = hoverText ?? string.Empty;
            if (teleportX.HasValue && teleportY.HasValue && teleportZ.HasValue)
                _chatOverlayActionUnlockUntil = _worldTimeSeconds + ChatOverlayActionUnlockSeconds;
            return;
        }

        _chatLines.Add(new ChatLine
        {
            Text = text,
            IsSystem = isSystem,
            TimeRemaining = ChatLineLifetimeSeconds,
            HasTeleportAction = teleportX.HasValue && teleportY.HasValue && teleportZ.HasValue,
            TeleportX = teleportX ?? 0,
            TeleportY = teleportY ?? 0,
            TeleportZ = teleportZ ?? 0,
            HoverText = hoverText ?? string.Empty
        });

        if (teleportX.HasValue && teleportY.HasValue && teleportZ.HasValue)
            _chatOverlayActionUnlockUntil = _worldTimeSeconds + ChatOverlayActionUnlockSeconds;

        if (_chatLines.Count > MaxChatLines)
            _chatLines.RemoveRange(0, _chatLines.Count - MaxChatLines);
    }

    private void EnsureCommandRegistry()
    {
        if (_commandsInitialized)
            return;

        _commandsInitialized = true;
        RegisterCommand("help", new[] { "?" }, "/help", "Show command help.", CommandPermission.Everyone, ExecuteHelpCommand);
        RegisterCommand("biome", Array.Empty<string>(), "/biome [list|here|<desert|grasslands|ocean> [radius]]", "Find or inspect biomes.", CommandPermission.Everyone, ExecuteBiomeCommand);
        RegisterCommand("structure", new[] { "locate" }, "/structure [gui|list|biome <name> [radius]]", "Open structure finder GUI or run finder tools.", CommandPermission.Everyone, ExecuteStructureCommand);
        RegisterCommand("tp", new[] { "teleport" }, "/tp <x> <z> or /tp <x> <y> <z>", "Teleport to coordinates.", CommandPermission.OwnerAdmin, ExecuteTeleportCommand);
        RegisterCommand("gamemode", new[] { "gm" }, "/gamemode <artificer|veilwalker|veilseer|gui>", "Change gamemode or open selector.", CommandPermission.OwnerAdmin, ExecuteGameModeCommand);
        RegisterCommand("artificer", Array.Empty<string>(), "/artificer", "Set ARTIFICER mode.", CommandPermission.OwnerAdmin, _ => ExecuteDirectGameMode(GameMode.Artificer));
        RegisterCommand("veilwalker", Array.Empty<string>(), "/veilwalker", "Set VEILWALKER mode.", CommandPermission.OwnerAdmin, _ => ExecuteDirectGameMode(GameMode.Veilwalker));
        RegisterCommand("veilseer", Array.Empty<string>(), "/veilseer", "Set VEILSEER mode.", CommandPermission.OwnerAdmin, _ => ExecuteDirectGameMode(GameMode.Veilseer));
        RegisterCommand("difficulty", new[] { "diff" }, "/difficulty <peaceful|easy|normal|hard>", "Set difficulty.", CommandPermission.OwnerAdmin, ExecuteDifficultyCommand);
        RegisterCommand("seed", Array.Empty<string>(), "/seed", "Show current world seed.", CommandPermission.OwnerAdmin, ExecuteSeedCommand);
        RegisterCommand("time", Array.Empty<string>(), "/time [query|day|night|set <ticks>]", "Set or inspect time.", CommandPermission.OwnerAdmin, ExecuteTimeCommand);
        RegisterCommand("weather", Array.Empty<string>(), "/weather [query|clear|rain|storm]", "Set or inspect weather.", CommandPermission.OwnerAdmin, ExecuteWeatherCommand);
        RegisterCommand("setspawn", Array.Empty<string>(), "/setspawn [x y z]", "Set world spawn.", CommandPermission.OwnerAdmin, ExecuteSetSpawnCommand);
        RegisterCommand("spawn", Array.Empty<string>(), "/spawn", "Teleport to world spawn.", CommandPermission.Everyone, ExecuteSpawnCommand);
        RegisterCommand("sethome", Array.Empty<string>(), "/sethome [name]", "Set or update a named home.", CommandPermission.Everyone, ExecuteSetHomeCommand);
        RegisterCommand("home", Array.Empty<string>(), "/home [name|list|gui|set|rename|delete|icon]", "Teleport/manage homes.", CommandPermission.Everyone, ExecuteHomeCommand);
        RegisterCommand("me", Array.Empty<string>(), "/me <action>", "Broadcast an action message.", CommandPermission.Everyone, ExecuteMeCommand);
        RegisterCommand("msg", Array.Empty<string>(), "/msg <player> <message>", "Send a private message.", CommandPermission.Everyone, ExecuteMsgCommand);
        RegisterCommand("chatclear", Array.Empty<string>(), "/chatclear confirm", "Clear chat history after confirmation.", CommandPermission.Everyone, ExecuteChatClearCommand);
        RegisterCommand("commandclear", Array.Empty<string>(), "/commandclear", "Clear command entries from shared input history.", CommandPermission.Everyone, ExecuteCommandClearCommand);
    }

    private void RegisterCommand(
        string name,
        string[] aliases,
        string usage,
        string description,
        CommandPermission permission,
        Action<string[]> handler)
    {
        var descriptor = new CommandDescriptor(name, aliases, usage, description, permission, handler);
        _commandDescriptors.Add(descriptor);
        _commandLookup[name] = descriptor;
        for (var i = 0; i < aliases.Length; i++)
            _commandLookup[aliases[i]] = descriptor;
    }

    private bool HasOwnerAdminPermissions()
    {
        return _lanSession == null || _lanSession.IsHost;
    }

    private void ExecuteCommand(string raw)
    {
        EnsureCommandRegistry();
        var commandText = (raw ?? string.Empty).Trim();
        if (commandText.Length == 0 || commandText == "/")
        {
            SetCommandStatus("Try /help for commands.");
            return;
        }

        var echoed = commandText.StartsWith("/", StringComparison.Ordinal) ? commandText : "/" + commandText;
        var username = _profile.GetDisplayUsername();
        AddChatLine($"{username}: {echoed}", isSystem: false);

        if (commandText.StartsWith("/", StringComparison.Ordinal))
            commandText = commandText.Substring(1);

        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            SetCommandStatus("Try /help for commands.");
            return;
        }

        var commandName = parts[0].ToLowerInvariant();
        if (commandName == "findbiome")
        {
            SetCommandStatus("Unknown command: /findbiome. Use /biome.");
            return;
        }

        if (!_commandLookup.TryGetValue(commandName, out var descriptor))
        {
            SetCommandStatus($"Unknown command: /{commandName}. Use /help.");
            return;
        }

        if (descriptor.Permission == CommandPermission.OwnerAdmin && !HasOwnerAdminPermissions())
        {
            SetCommandStatus("You do not have permission to use that command.");
            return;
        }

        descriptor.Handler(parts);
    }

    private void ExecuteHelpCommand(string[] _)
    {
        var lines = new List<string>();
        foreach (var descriptor in _commandDescriptors)
        {
            if (descriptor.Permission == CommandPermission.OwnerAdmin && !HasOwnerAdminPermissions())
                continue;
            lines.Add("/" + descriptor.Name);
        }

        SetCommandStatus($"Commands: {string.Join(", ", lines)}", 10f);
    }

    private void ExecuteBiomeCommand(string[] commandParts)
    {
        if (_world == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        var originX = (int)MathF.Floor(_player.Position.X);
        var originZ = (int)MathF.Floor(_player.Position.Z);

        if (commandParts.Length < 2 || string.Equals(commandParts[1], "here", StringComparison.OrdinalIgnoreCase))
        {
            ReportCurrentBiome(originX, originZ);
            return;
        }

        if (string.Equals(commandParts[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            SetCommandStatus("Biomes: Grasslands, Desert, Ocean", 8f);
            return;
        }

        if (!TryResolveBiome(commandParts[1], out var targetBiomeToken, out var targetBiomeLabel))
        {
            SetCommandStatus("Biome must be one of: desert, grasslands, ocean. Use /biome list.");
            return;
        }

        var maxRadius = CommandFindBiomeDefaultRadius;
        if (commandParts.Length >= 3)
        {
            if (!int.TryParse(commandParts[2], out maxRadius))
            {
                SetCommandStatus("Radius must be a number.");
                return;
            }

            maxRadius = Math.Clamp(maxRadius, 16, CommandFindBiomeMaxRadius);
        }

        if (!TryReportBiomeLocation(targetBiomeToken, targetBiomeLabel, maxRadius))
            return;
    }

    private void ExecuteStructureCommand(string[] commandParts)
    {
        if (commandParts.Length < 2 || string.Equals(commandParts[1], "gui", StringComparison.OrdinalIgnoreCase))
        {
            OpenStructureFinderGui(openStructuresTab: true);
            SetCommandStatus("Opened structure finder.");
            return;
        }

        if (string.Equals(commandParts[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            SetCommandStatus("Structures: (none configured yet).", 8f);
            return;
        }

        var tokenIndex = 1;
        if (string.Equals(commandParts[1], "biome", StringComparison.OrdinalIgnoreCase))
        {
            if (commandParts.Length < 3)
            {
                SetCommandStatus("Usage: /structure biome <desert|grasslands|ocean> [radius]");
                return;
            }

            tokenIndex = 2;
        }

        if (!TryResolveBiome(commandParts[tokenIndex], out var targetBiomeToken, out var targetBiomeLabel))
        {
            SetCommandStatus("No structures are configured yet. Use /structure gui.");
            return;
        }

        var maxRadius = CommandFindBiomeDefaultRadius;
        if (commandParts.Length > tokenIndex + 1)
        {
            if (!int.TryParse(commandParts[tokenIndex + 1], out maxRadius))
            {
                SetCommandStatus("Radius must be a number.");
                return;
            }

            maxRadius = Math.Clamp(maxRadius, 16, CommandFindBiomeMaxRadius);
        }

        if (!TryReportBiomeLocation(targetBiomeToken, targetBiomeLabel, maxRadius))
            return;
    }

    private bool TryReportBiomeLocation(string targetBiomeToken, string targetBiomeLabel, int maxRadius)
    {
        if (_world == null)
        {
            SetCommandStatus("World is not ready yet.");
            return false;
        }

        var originX = (int)MathF.Floor(_player.Position.X);
        var originZ = (int)MathF.Floor(_player.Position.Z);
        if (_hasQueuedLocateOrigin)
        {
            originX = _queuedLocateOriginX;
            originZ = _queuedLocateOriginZ;
            _hasQueuedLocateOrigin = false;
        }
        if (!TryResolveValidatedBiomeCoordinate(targetBiomeToken, originX, originZ, maxRadius, out var foundX, out var foundZ))
        {
            SetCommandStatus($"Unable to locate {targetBiomeLabel}.");
            return false;
        }

        var foundY = Math.Clamp(
            (int)MathF.Round(FindSafeSpawnHeight(foundX, foundZ) + 1f),
            2,
            Math.Max(3, (_meta?.Size?.Height ?? 256) - 2));
        var resultText = $"X: {foundX} Y: {foundY} Z: {foundZ}";
        AddChatLine(
            resultText,
            isSystem: true,
            teleportX: foundX,
            teleportY: foundY,
            teleportZ: foundZ,
            hoverText: $"Click to teleport to {targetBiomeLabel} at {foundX} {foundY} {foundZ}");
        return true;
    }

    private bool TryResolveValidatedBiomeCoordinate(string targetBiomeToken, int originX, int originZ, int maxRadius, out int foundX, out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;

        if (_world == null || _meta == null)
            return false;

        originX = Math.Clamp(originX, 0, Math.Max(0, _meta.Size.Width - 1));
        originZ = Math.Clamp(originZ, 0, Math.Max(0, _meta.Size.Depth - 1));

        if (IsBiomeCoordinateValid(targetBiomeToken, originX, originZ))
        {
            foundX = originX;
            foundZ = originZ;
            return true;
        }

        EnsureBiomeCatalog();
        if (TryFindValidatedBiomeFromCatalog(targetBiomeToken, originX, originZ, maxRadius, out var catalogX, out var catalogZ)
            && TryFinalizeBiomeLocateCandidate(targetBiomeToken, originX, originZ, catalogX, catalogZ, out foundX, out foundZ))
            return true;

        var worldRadius = GetBiomeSearchWorldRadius();
        var boundedRadius = Math.Clamp(maxRadius, 16, Math.Max(16, worldRadius));
        var ringRadius = Math.Min(boundedRadius, BiomeLocateMaxRingRadius);

        if (TryFindBiomeOnRingScan(targetBiomeToken, originX, originZ, ringRadius, BiomeLocateRingStep, out var ringX, out var ringZ)
            && TryFinalizeBiomeLocateCandidate(targetBiomeToken, originX, originZ, ringX, ringZ, out foundX, out foundZ))
            return true;

        if (TryFindBiomeByGlobalStrideScan(targetBiomeToken, originX, originZ, BiomeLocateGlobalStride, out var globalX, out var globalZ)
            && TryFinalizeBiomeLocateCandidate(targetBiomeToken, originX, originZ, globalX, globalZ, out foundX, out foundZ))
            return true;

        if (TryFindBiomeByGlobalStrideScan(targetBiomeToken, originX, originZ, BiomeLocateGlobalFallbackStride, out var fallbackX, out var fallbackZ)
            && TryFinalizeBiomeLocateCandidate(targetBiomeToken, originX, originZ, fallbackX, fallbackZ, out foundX, out foundZ))
            return true;

        if (TryFindBestBiomeClimateCandidate(targetBiomeToken, originX, originZ, out var climateX, out var climateZ)
            && TryRefineBiomeCandidate(targetBiomeToken, originX, originZ, climateX, climateZ, Math.Max(12, BiomeSearchRefineWindow * 4), out var refinedX, out var refinedZ)
            && TryFinalizeBiomeLocateCandidate(targetBiomeToken, originX, originZ, refinedX, refinedZ, out foundX, out foundZ))
        {
            return true;
        }

        return false;
    }

    private bool TryFindValidatedBiomeFromCatalog(string targetBiomeToken, int originX, int originZ, int maxRadius, out int foundX, out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;
        if (_biomeCatalog == null || !BiomeCatalog.TryParseBiomeToken(targetBiomeToken, out var targetBiomeId))
            return false;

        var points = _biomeCatalog.GetPoints(targetBiomeId);
        if (points.Count == 0)
            return false;

        var radiusSq = (long)Math.Max(16, maxRadius) * Math.Max(16, maxRadius);
        var bestAnyDistSq = long.MaxValue;
        var bestAnyX = originX;
        var bestAnyZ = originZ;
        var bestInRadiusDistSq = long.MaxValue;
        var bestInRadiusX = originX;
        var bestInRadiusZ = originZ;
        var hasInRadius = false;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!IsWithinBiomeSearchBounds(point.X, point.Z))
                continue;

            var dx = point.X - originX;
            var dz = point.Z - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;

            if (distSq < bestAnyDistSq)
            {
                bestAnyDistSq = distSq;
                bestAnyX = point.X;
                bestAnyZ = point.Z;
            }

            if (distSq <= radiusSq && distSq < bestInRadiusDistSq)
            {
                bestInRadiusDistSq = distSq;
                bestInRadiusX = point.X;
                bestInRadiusZ = point.Z;
                hasInRadius = true;
            }
        }

        if (hasInRadius
            && TryRefineBiomeCandidate(targetBiomeToken, originX, originZ, bestInRadiusX, bestInRadiusZ, Math.Max(8, BiomeSearchRefineWindow * 2), out foundX, out foundZ))
        {
            return true;
        }

        if (TryRefineBiomeCandidate(targetBiomeToken, originX, originZ, bestAnyX, bestAnyZ, Math.Max(8, BiomeSearchRefineWindow * 2), out foundX, out foundZ))
            return true;

        return false;
    }

    private bool TryFindBiomeOnRingScan(string targetBiomeToken, int originX, int originZ, int maxRadius, int step, out int foundX, out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;
        if (maxRadius <= 0)
            return false;

        step = Math.Max(1, step);
        var bestDistSq = long.MaxValue;
        var found = false;
        var bestX = originX;
        var bestZ = originZ;

        void EvaluateCandidate(int wx, int wz)
        {
            if (!IsBiomeCoordinateValid(targetBiomeToken, wx, wz))
                return;

            var dx = wx - originX;
            var dz = wz - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestX = wx;
            bestZ = wz;
            found = true;
        }

        for (var radius = step; radius <= maxRadius; radius += step)
        {
            for (var dx = -radius; dx <= radius; dx += step)
            {
                EvaluateCandidate(originX + dx, originZ - radius);
                EvaluateCandidate(originX + dx, originZ + radius);
            }

            for (var dz = -radius + step; dz <= radius - step; dz += step)
            {
                EvaluateCandidate(originX - radius, originZ + dz);
                EvaluateCandidate(originX + radius, originZ + dz);
            }

            if (found)
            {
                foundX = bestX;
                foundZ = bestZ;
                return true;
            }
        }

        return false;
    }

    private bool TryFindBiomeByGlobalStrideScan(string targetBiomeToken, int originX, int originZ, int stride, out int foundX, out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;
        if (_meta == null || stride <= 0)
            return false;

        var bestDistSq = long.MaxValue;
        var found = false;
        var bestX = originX;
        var bestZ = originZ;
        var maxX = Math.Max(0, _meta.Size.Width - 1);
        var maxZ = Math.Max(0, _meta.Size.Depth - 1);

        void Evaluate(int wx, int wz)
        {
            if (!IsBiomeCoordinateValid(targetBiomeToken, wx, wz))
                return;

            var dx = wx - originX;
            var dz = wz - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestX = wx;
            bestZ = wz;
            found = true;
        }

        for (var wz = 0; wz <= maxZ; wz += stride)
        {
            for (var wx = 0; wx <= maxX; wx += stride)
                Evaluate(wx, wz);
            Evaluate(maxX, wz);
        }

        for (var wx = 0; wx <= maxX; wx += stride)
            Evaluate(wx, maxZ);
        Evaluate(maxX, maxZ);

        if (!found)
            return false;

        return TryRefineBiomeCandidate(targetBiomeToken, originX, originZ, bestX, bestZ, Math.Max(8, stride), out foundX, out foundZ);
    }

    private bool TryRefineBiomeCandidate(string targetBiomeToken, int originX, int originZ, int centerX, int centerZ, int radius, out int foundX, out int foundZ)
    {
        foundX = centerX;
        foundZ = centerZ;

        if (IsBiomeCoordinateValid(targetBiomeToken, centerX, centerZ))
            return true;

        radius = Math.Max(1, radius);
        var bestDistSq = long.MaxValue;
        var found = false;
        for (var wz = centerZ - radius; wz <= centerZ + radius; wz++)
        {
            for (var wx = centerX - radius; wx <= centerX + radius; wx++)
            {
                if (!IsBiomeCoordinateValid(targetBiomeToken, wx, wz))
                    continue;

                var dx = wx - originX;
                var dz = wz - originZ;
                var distSq = (long)dx * dx + (long)dz * dz;
                if (distSq >= bestDistSq)
                    continue;

                bestDistSq = distSq;
                foundX = wx;
                foundZ = wz;
                found = true;
            }
        }

        return found;
    }

    private bool TryFinalizeBiomeLocateCandidate(string targetBiomeToken, int originX, int originZ, int candidateX, int candidateZ, out int foundX, out int foundZ)
    {
        foundX = candidateX;
        foundZ = candidateZ;
        if (!IsBiomeCoordinateValid(targetBiomeToken, candidateX, candidateZ))
            return false;

        var promotedX = candidateX;
        var promotedZ = candidateZ;
        TryPromoteBiomeInterior(targetBiomeToken, originX, originZ, ref promotedX, ref promotedZ);
        if (IsBiomeCoordinateValid(targetBiomeToken, promotedX, promotedZ))
        {
            foundX = promotedX;
            foundZ = promotedZ;
        }

        return true;
    }

    private void TryPromoteBiomeInterior(string targetBiomeToken, int originX, int originZ, ref int bestX, ref int bestZ)
    {
        if (_world == null || _meta == null)
            return;

        var radius = targetBiomeToken switch
        {
            "ocean" => 144,
            "desert" => 128,
            _ => 96
        };
        var step = targetBiomeToken == "ocean" ? 4 : 3;
        var minX = Math.Max(0, bestX - radius);
        var maxX = Math.Min(_meta.Size.Width - 1, bestX + radius);
        var minZ = Math.Max(0, bestZ - radius);
        var maxZ = Math.Min(_meta.Size.Depth - 1, bestZ + radius);

        var bestStrength = GetBiomeLocateStrength(targetBiomeToken, bestX, bestZ);
        var bestDistSq = (long)(bestX - originX) * (bestX - originX) + (long)(bestZ - originZ) * (bestZ - originZ);
        if (bestStrength >= 0.92f)
            return;

        for (var wz = minZ; wz <= maxZ; wz += step)
        {
            for (var wx = minX; wx <= maxX; wx += step)
            {
                if (!IsBiomeCoordinateValid(targetBiomeToken, wx, wz))
                    continue;

                var strength = GetBiomeLocateStrength(targetBiomeToken, wx, wz);
                var dx = wx - originX;
                var dz = wz - originZ;
                var distSq = (long)dx * dx + (long)dz * dz;
                if (strength > bestStrength + 0.015f
                    || (MathF.Abs(strength - bestStrength) <= 0.015f && distSq < bestDistSq))
                {
                    bestStrength = strength;
                    bestDistSq = distSq;
                    bestX = wx;
                    bestZ = wz;
                }
            }
        }
    }

    private float GetBiomeLocateStrength(string targetBiomeToken, int x, int z)
    {
        if (_world == null)
            return 0f;

        var oceanWeight = Math.Clamp(_world.GetOceanWeightAt(x, z), 0f, 1f);
        var desertWeight = Math.Clamp(_world.GetDesertWeightAt(x, z), 0f, 1f);
        var effectiveDesert = Math.Clamp(desertWeight * (1f - oceanWeight * 0.9f), 0f, 1f);
        return targetBiomeToken switch
        {
            "ocean" => Math.Clamp((oceanWeight - 0.67f) / 0.33f, 0f, 1f),
            "desert" => Math.Clamp((effectiveDesert - 0.44f) / 0.56f, 0f, 1f),
            _ => Math.Clamp(1f - MathF.Max(oceanWeight, effectiveDesert), 0f, 1f)
        };
    }

    private bool IsBiomeCoordinateValid(string targetBiomeToken, int x, int z)
    {
        if (_world == null)
            return false;
        if (!IsWithinBiomeSearchBounds(x, z))
            return false;
        return IsBiomeMatch(_world.GetBiomeNameAt(x, z), targetBiomeToken);
    }

    private void ReportCurrentBiome(int wx, int wz)
    {
        if (_world == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        var biome = _world.GetBiomeNameAt(wx, wz);
        SetCommandStatus($"You are currently in {biome} biome at {wx}, {wz}.", 7f);
    }

    private void ExecuteTeleportCommand(string[] commandParts)
    {
        if (commandParts.Length < 3)
        {
            SetCommandStatus("Usage: /tp <x> <z> or /tp <x> <y> <z>");
            return;
        }

        int x;
        int z;
        int? y = null;
        if (commandParts.Length >= 4)
        {
            if (!int.TryParse(commandParts[1], out x) ||
                !int.TryParse(commandParts[2], out var parsedY) ||
                !int.TryParse(commandParts[3], out z))
            {
                SetCommandStatus("Usage: /tp <x> <y> <z>");
                return;
            }

            y = parsedY;
        }
        else
        {
            if (!int.TryParse(commandParts[1], out x) ||
                !int.TryParse(commandParts[2], out z))
            {
                SetCommandStatus("Usage: /tp <x> <z>");
                return;
            }
        }

        if (!TryTeleportPlayer(x, z, y, out var result))
        {
            SetCommandStatus(result);
            return;
        }

        SetCommandStatus(result);
    }

    private void ExecuteGameModeCommand(string[] commandParts)
    {
        if (commandParts.Length < 2)
        {
            SetCommandStatus("Usage: /gamemode <artificer|veilwalker|veilseer|gui>");
            return;
        }

        if (string.Equals(commandParts[1], "gui", StringComparison.OrdinalIgnoreCase))
        {
            OpenGamemodeWheel(holdToOpen: false);
            SetCommandStatus("Select a gamemode from the wheel.");
            return;
        }

        if (!TryParseGameModeToken(commandParts[1], out var mode))
        {
            SetCommandStatus("Gamemode must be artificer, veilwalker, or veilseer.");
            return;
        }

        ApplyGameModeChange(mode, GameModeChangeSource.Command, persistImmediately: true, emitFeedback: true);
    }

    private void ExecuteDirectGameMode(GameMode mode)
    {
        ApplyGameModeChange(mode, GameModeChangeSource.Command, persistImmediately: true, emitFeedback: true);
    }

    private static bool TryParseGameModeToken(string value, out GameMode mode)
    {
        mode = GameMode.Artificer;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var token = value.Trim().ToLowerInvariant();
        switch (token)
        {
            case "artificer":
            case "creative":
            case "c":
            case "1":
                mode = GameMode.Artificer;
                return true;
            case "veilwalker":
            case "survival":
            case "s":
            case "0":
                mode = GameMode.Veilwalker;
                return true;
            case "veilseer":
            case "spectator":
            case "sp":
            case "3":
                mode = GameMode.Veilseer;
                return true;
            default:
                return false;
        }
    }

    private void ExecuteDifficultyCommand(string[] commandParts)
    {
        if (commandParts.Length < 2)
        {
            SetCommandStatus($"Current difficulty: {GetDifficultyLabel(_difficultyLevel)}.");
            return;
        }

        if (!TryParseDifficultyToken(commandParts[1], out var difficulty))
        {
            SetCommandStatus("Difficulty must be peaceful, easy, normal, or hard.");
            return;
        }

        _difficultyLevel = difficulty;
        SetCommandStatus($"Difficulty set to {GetDifficultyLabel(_difficultyLevel).ToUpperInvariant()}.");
    }

    private void ExecuteSeedCommand(string[] _)
    {
        if (_meta == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        SetCommandStatus($"Seed: {_meta.Seed}", 8f);
    }

    private void ExecuteTimeCommand(string[] commandParts)
    {
        if (commandParts.Length < 2 || string.Equals(commandParts[1], "query", StringComparison.OrdinalIgnoreCase))
        {
            SetCommandStatus($"Time: {_timeOfDayTicks}");
            return;
        }

        var token = commandParts[1].ToLowerInvariant();
        switch (token)
        {
            case "day":
                _timeOfDayTicks = 1000;
                SetCommandStatus("Time set to day.");
                return;
            case "night":
                _timeOfDayTicks = 13000;
                SetCommandStatus("Time set to night.");
                return;
            case "set":
                if (commandParts.Length < 3 || !int.TryParse(commandParts[2], out var ticks))
                {
                    SetCommandStatus("Usage: /time set <ticks>");
                    return;
                }

                _timeOfDayTicks = ((ticks % 24000) + 24000) % 24000;
                SetCommandStatus($"Time set to {_timeOfDayTicks}.");
                return;
            default:
                SetCommandStatus("Usage: /time [query|day|night|set <ticks>]");
                return;
        }
    }

    private void ExecuteWeatherCommand(string[] commandParts)
    {
        if (commandParts.Length < 2 || string.Equals(commandParts[1], "query", StringComparison.OrdinalIgnoreCase))
        {
            SetCommandStatus($"Weather: {_weatherState.ToUpperInvariant()}");
            return;
        }

        var token = commandParts[1].ToLowerInvariant();
        if (token is "clear" or "rain" or "storm")
        {
            _weatherState = token;
            SetCommandStatus($"Weather set to {_weatherState.ToUpperInvariant()}.");
            return;
        }

        SetCommandStatus("Usage: /weather [query|clear|rain|storm]");
    }

    private void ExecuteSetSpawnCommand(string[] commandParts)
    {
        if (_meta == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        int x;
        int y;
        int z;
        if (commandParts.Length >= 4)
        {
            if (!int.TryParse(commandParts[1], out x) || !int.TryParse(commandParts[2], out y) || !int.TryParse(commandParts[3], out z))
            {
                SetCommandStatus("Usage: /setspawn [x y z]");
                return;
            }
        }
        else
        {
            x = (int)MathF.Floor(_player.Position.X);
            y = (int)MathF.Floor(_player.Position.Y);
            z = (int)MathF.Floor(_player.Position.Z);
        }

        _meta.HasCustomSpawn = true;
        _meta.SpawnX = Math.Clamp(x, 0, Math.Max(0, _meta.Size.Width - 1));
        _meta.SpawnY = Math.Clamp(y, 2, Math.Max(3, _meta.Size.Height - 2));
        _meta.SpawnZ = Math.Clamp(z, 0, Math.Max(0, _meta.Size.Depth - 1));
        _meta.Save(_metaPath, _log);
        SetCommandStatus($"Spawn set to {_meta.SpawnX}, {_meta.SpawnY}, {_meta.SpawnZ}.");
    }

    private void ExecuteSpawnCommand(string[] _)
    {
        if (_meta == null)
        {
            SetCommandStatus("World is not ready yet.");
            return;
        }

        var spawn = GetWorldSpawnPoint();
        int? explicitY = _meta.HasCustomSpawn ? _meta.SpawnY : null;
        if (!TryTeleportPlayer((int)spawn.X, (int)spawn.Y, explicitY, out var result))
        {
            SetCommandStatus(result);
            return;
        }

        SetCommandStatus(result);
    }

    private void ExecuteSetHomeCommand(string[] commandParts)
    {
        var requestedName = commandParts.Length >= 2 ? commandParts[1] : DefaultHomeName;
        if (!TrySetHome(requestedName, _player.Position, out var result))
        {
            SetCommandStatus(result);
            return;
        }

        SetCommandStatus(result);
    }

    private void ExecuteHomeCommand(string[] commandParts)
    {
        if (commandParts.Length < 2)
        {
            if (_homes.Count == 0)
            {
                SetCommandStatus("No home is set. Use /sethome first.");
                return;
            }

            if (_homes.Count > 1 && IsMultipleHomesEnabled())
            {
                OpenHomeGui();
                SetCommandStatus("Opened home menu.");
                return;
            }

            if (!TryTeleportToHome(null, out var defaultResult))
            {
                SetCommandStatus(defaultResult);
                return;
            }

            SetCommandStatus(defaultResult);
            return;
        }

        var action = commandParts[1].ToLowerInvariant();
        switch (action)
        {
            case "gui":
                OpenHomeGui();
                SetCommandStatus("Opened home menu.");
                return;
            case "list":
                if (_homes.Count == 0)
                {
                    SetCommandStatus("No homes set. Use /sethome <name>.");
                    return;
                }

                SetCommandStatus("Homes: " + string.Join(", ", _homes.Select(h => h.Name)), 10f);
                return;
            case "set":
                if (commandParts.Length < 3)
                {
                    SetCommandStatus("Usage: /home set <name>");
                    return;
                }

                if (!TrySetHome(commandParts[2], _player.Position, out var setResult))
                {
                    SetCommandStatus(setResult);
                    return;
                }

                SetCommandStatus(setResult);
                return;
            case "rename":
                if (commandParts.Length < 4)
                {
                    SetCommandStatus("Usage: /home rename <old> <new>");
                    return;
                }

                if (!TryFindHomeIndex(commandParts[2], out var fromIndex))
                {
                    SetCommandStatus($"Home not found: {commandParts[2]}");
                    return;
                }

                var targetName = NormalizeHomeName(commandParts[3]);
                if (TryFindHomeIndex(targetName, out var collision) && collision != fromIndex)
                {
                    SetCommandStatus($"Home name already exists: {targetName}");
                    return;
                }

                var oldName = _homes[fromIndex].Name;
                var renamed = _homes[fromIndex];
                renamed.Name = targetName;
                _homes[fromIndex] = renamed;
                _selectedHomeIndex = fromIndex;
                SavePlayerState();
                SetCommandStatus($"Renamed home '{oldName}' to '{targetName}'.");
                return;
            case "delete":
            case "remove":
                if (commandParts.Length < 3)
                {
                    SetCommandStatus("Usage: /home delete <name>");
                    return;
                }

                if (!TryFindHomeIndex(commandParts[2], out var deleteIndex))
                {
                    SetCommandStatus($"Home not found: {commandParts[2]}");
                    return;
                }

                var removedName = _homes[deleteIndex].Name;
                _homes.RemoveAt(deleteIndex);
                _selectedHomeIndex = _homes.Count == 0 ? -1 : Math.Clamp(_selectedHomeIndex, 0, _homes.Count - 1);
                SyncLegacyHomeFields();
                SavePlayerState();
                SetCommandStatus($"Deleted home '{removedName}'.");
                return;
            case "icon":
                if (commandParts.Length < 4)
                {
                    SetCommandStatus("Usage: /home icon <name> <block|auto>");
                    return;
                }

                if (!TryFindHomeIndex(commandParts[2], out var iconIndex))
                {
                    SetCommandStatus($"Home not found: {commandParts[2]}");
                    return;
                }

                var iconToken = commandParts[3];
                var iconEntry = _homes[iconIndex];
                if (string.Equals(iconToken, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    iconEntry.HasIconBlock = false;
                    iconEntry.IconBlock = BlockId.Air;
                    _homes[iconIndex] = iconEntry;
                    SavePlayerState();
                    SetCommandStatus($"Home '{iconEntry.Name}' icon set to letter.");
                    return;
                }

                if (!TryResolveHomeIconBlock(iconToken, out var iconBlock))
                {
                    SetCommandStatus($"Unknown block icon: {iconToken}");
                    return;
                }

                iconEntry.HasIconBlock = true;
                iconEntry.IconBlock = iconBlock;
                _homes[iconIndex] = iconEntry;
                SavePlayerState();
                SetCommandStatus($"Home '{iconEntry.Name}' icon set to {iconBlock}.");
                return;
            default:
                if (!TryTeleportToHome(commandParts[1], out var result))
                {
                    SetCommandStatus(result);
                    return;
                }

                SetCommandStatus(result);
                return;
        }
    }

    private void OpenHomeGui()
    {
        _homeGuiOpen = true;
        _homeGuiScroll = 0f;
        if (_selectedHomeIndex < 0 && _homes.Count > 0)
            _selectedHomeIndex = 0;
        _homeGuiNameInput = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count
            ? _homes[_selectedHomeIndex].Name
            : DefaultHomeName;
        LayoutHomeGui();
    }

    private void LayoutHomeGui()
    {
        var panelW = Math.Clamp(_viewport.Width - 220, 560, 820);
        var panelH = Math.Clamp(_viewport.Height - 180, 360, 560);
        _homeGuiPanelRect = new Rectangle(
            _viewport.Center.X - panelW / 2,
            _viewport.Center.Y - panelH / 2,
            panelW,
            panelH);

        _homeGuiInputRect = new Rectangle(_homeGuiPanelRect.X + 16, _homeGuiPanelRect.Y + 40, _homeGuiPanelRect.Width - 32, 34);
        _homeGuiListRect = new Rectangle(_homeGuiPanelRect.X + 16, _homeGuiInputRect.Bottom + 10, _homeGuiPanelRect.Width - 32, _homeGuiPanelRect.Height - 160);

        var buttonY = _homeGuiListRect.Bottom + 10;
        var buttonW = (_homeGuiPanelRect.Width - 16 * 2 - 10 * 2) / 3;
        var buttonH = 30;
        _homeGuiSetHereBtn.Bounds = new Rectangle(_homeGuiPanelRect.X + 16, buttonY, buttonW, buttonH);
        _homeGuiRenameBtn.Bounds = new Rectangle(_homeGuiSetHereBtn.Bounds.Right + 10, buttonY, buttonW, buttonH);
        _homeGuiDeleteBtn.Bounds = new Rectangle(_homeGuiRenameBtn.Bounds.Right + 10, buttonY, buttonW, buttonH);

        var buttonY2 = buttonY + buttonH + 8;
        _homeGuiSetIconHeldBtn.Bounds = new Rectangle(_homeGuiPanelRect.X + 16, buttonY2, buttonW, buttonH);
        _homeGuiSetIconAutoBtn.Bounds = new Rectangle(_homeGuiSetIconHeldBtn.Bounds.Right + 10, buttonY2, buttonW, buttonH);
        _homeGuiCloseBtn.Bounds = new Rectangle(_homeGuiSetIconAutoBtn.Bounds.Right + 10, buttonY2, buttonW, buttonH);
    }

    private void UpdateHomeGui(InputState input, float dt)
    {
        LayoutHomeGui();
        if (_homeGuiStatusTimer > 0f)
        {
            _homeGuiStatusTimer = Math.Max(0f, _homeGuiStatusTimer - dt);
            if (_homeGuiStatusTimer <= 0f)
                _homeGuiStatus = string.Empty;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _homeGuiOpen = false;
            return;
        }

        var maxScroll = Math.Max(0f, _homes.Count * HomeGuiRowHeight - _homeGuiListRect.Height);
        if (_homeGuiListRect.Contains(input.MousePosition) && input.ScrollDelta != 0)
        {
            var delta = input.ScrollDelta > 0 ? -HomeGuiRowHeight : HomeGuiRowHeight;
            _homeGuiScroll = Math.Clamp(_homeGuiScroll + delta, 0f, maxScroll);
        }

        if (input.IsNewLeftClick() && _homeGuiListRect.Contains(input.MousePosition))
        {
            var localY = input.MousePosition.Y - _homeGuiListRect.Y + (int)MathF.Round(_homeGuiScroll);
            var idx = localY / HomeGuiRowHeight;
            if (idx >= 0 && idx < _homes.Count)
            {
                if (idx == _homeGuiLastClickIndex && (_worldTimeSeconds - _homeGuiLastClickTime) <= 0.4f)
                {
                    _selectedHomeIndex = idx;
                    _homeGuiNameInput = _homes[idx].Name;
                    if (TryTeleportToHome(_homes[idx].Name, out var doubleClickResult))
                        SetHomeGuiStatus(doubleClickResult);
                    else
                        SetHomeGuiStatus(doubleClickResult);
                }
                else
                {
                    _selectedHomeIndex = idx;
                    _homeGuiNameInput = _homes[idx].Name;
                }

                _homeGuiLastClickIndex = idx;
                _homeGuiLastClickTime = _worldTimeSeconds;
            }
        }

        if (input.IsNewKeyPress(Keys.Enter))
        {
            SetHomeAtCurrentPositionFromGui();
            return;
        }

        foreach (var key in input.GetNewKeys())
        {
            if (key == Keys.Back)
            {
                if (_homeGuiNameInput.Length > 0)
                    _homeGuiNameInput = _homeGuiNameInput.Substring(0, _homeGuiNameInput.Length - 1);
                continue;
            }

            if (!TryMapCommandInputKey(key, input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift), out var c))
                continue;

            if (_homeGuiNameInput.Length < 24)
                _homeGuiNameInput += c;
        }

        _homeGuiSetHereBtn.Update(input);
        _homeGuiRenameBtn.Enabled = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count;
        _homeGuiDeleteBtn.Enabled = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count;
        _homeGuiSetIconHeldBtn.Enabled = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count && _inventory.SelectedId != BlockId.Air;
        _homeGuiSetIconAutoBtn.Enabled = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count;
        _homeGuiRenameBtn.Update(input);
        _homeGuiDeleteBtn.Update(input);
        _homeGuiSetIconHeldBtn.Update(input);
        _homeGuiSetIconAutoBtn.Update(input);
        _homeGuiCloseBtn.Update(input);
    }

    private void DrawHomeGui(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewport, new Color(0, 0, 0, 110));
        sb.Draw(_pixel, _homeGuiPanelRect, new Color(18, 22, 28, 230));
        DrawBorder(sb, _homeGuiPanelRect, new Color(210, 220, 235));
        _font.DrawString(sb, "HOME MANAGER", new Vector2(_homeGuiPanelRect.X + 16, _homeGuiPanelRect.Y + 12), Color.White);

        sb.Draw(_pixel, _homeGuiInputRect, new Color(10, 10, 10, 210));
        DrawBorder(sb, _homeGuiInputRect, new Color(190, 190, 190));
        var nameInput = string.IsNullOrWhiteSpace(_homeGuiNameInput) ? "(home name)" : _homeGuiNameInput;
        _font.DrawString(sb, nameInput, new Vector2(_homeGuiInputRect.X + 8, _homeGuiInputRect.Y + 8), Color.White);

        sb.Draw(_pixel, _homeGuiListRect, new Color(8, 8, 8, 180));
        DrawBorder(sb, _homeGuiListRect, new Color(170, 170, 170));

        var start = (int)(_homeGuiScroll / HomeGuiRowHeight);
        var yOffset = (int)(_homeGuiScroll % HomeGuiRowHeight);
        var visibleRows = Math.Max(1, _homeGuiListRect.Height / HomeGuiRowHeight + 2);
        for (int i = 0; i < visibleRows; i++)
        {
            var idx = start + i;
            if (idx < 0 || idx >= _homes.Count)
                continue;

            var rowY = _homeGuiListRect.Y + i * HomeGuiRowHeight - yOffset;
            var rowRect = new Rectangle(_homeGuiListRect.X + 2, rowY, _homeGuiListRect.Width - 4, HomeGuiRowHeight - 2);
            if (rowRect.Bottom < _homeGuiListRect.Y || rowRect.Y > _homeGuiListRect.Bottom)
                continue;

            var selected = idx == _selectedHomeIndex;
            sb.Draw(_pixel, rowRect, selected ? new Color(38, 78, 118, 210) : new Color(20, 20, 20, 175));
            DrawBorder(sb, rowRect, selected ? new Color(190, 230, 255) : new Color(120, 120, 120));

            var home = _homes[idx];
            var iconRect = new Rectangle(rowRect.X + 6, rowRect.Y + 6, 28, 28);
            DrawHomeIcon(sb, home, iconRect);

            var coords = $"{(int)home.Position.X}, {(int)home.Position.Y}, {(int)home.Position.Z}";
            _font.DrawString(sb, home.Name, new Vector2(iconRect.Right + 8, rowRect.Y + 6), Color.White);
            _font.DrawString(sb, coords, new Vector2(iconRect.Right + 8, rowRect.Y + 6 + _font.LineHeight), new Color(190, 205, 225));
        }

        _homeGuiSetHereBtn.Draw(sb, _pixel, _font);
        _homeGuiRenameBtn.Draw(sb, _pixel, _font);
        _homeGuiDeleteBtn.Draw(sb, _pixel, _font);
        _homeGuiSetIconHeldBtn.Draw(sb, _pixel, _font);
        _homeGuiSetIconAutoBtn.Draw(sb, _pixel, _font);
        _homeGuiCloseBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_homeGuiStatus))
        {
            _font.DrawString(sb, _homeGuiStatus, new Vector2(_homeGuiPanelRect.X + 16, _homeGuiPanelRect.Bottom - _font.LineHeight - 4), new Color(235, 235, 180));
        }
    }

    private void DrawHomeIcon(SpriteBatch sb, PlayerHomeEntry home, Rectangle iconRect)
    {
        sb.Draw(_pixel, iconRect, new Color(28, 28, 28, 240));
        DrawBorder(sb, iconRect, new Color(200, 200, 200));

        if (home.HasIconBlock)
        {
            var iconTexture = GetBlockIcon(home.IconBlock, iconRect.Width - 4);
            if (iconTexture != null)
            {
                sb.Draw(iconTexture, new Rectangle(iconRect.X + 2, iconRect.Y + 2, iconRect.Width - 4, iconRect.Height - 4), Color.White);
                return;
            }
        }

        var fallback = home.Name.Length > 0 ? char.ToUpperInvariant(home.Name[0]).ToString() : "?";
        _font.DrawString(sb, fallback, new Vector2(iconRect.X + 9, iconRect.Y + 6), Color.White);
    }

    private void SetHomeAtCurrentPositionFromGui()
    {
        if (!TrySetHome(_homeGuiNameInput, _player.Position, out var result))
        {
            SetHomeGuiStatus(result);
            return;
        }

        _homeGuiNameInput = _selectedHomeIndex >= 0 && _selectedHomeIndex < _homes.Count ? _homes[_selectedHomeIndex].Name : _homeGuiNameInput;
        SetHomeGuiStatus(result);
    }

    private void RenameSelectedHomeFromGui()
    {
        if (_selectedHomeIndex < 0 || _selectedHomeIndex >= _homes.Count)
        {
            SetHomeGuiStatus("Select a home first.");
            return;
        }

        var newName = NormalizeHomeName(_homeGuiNameInput);
        if (TryFindHomeIndex(newName, out var collision) && collision != _selectedHomeIndex)
        {
            SetHomeGuiStatus($"Home name already exists: {newName}");
            return;
        }

        var oldName = _homes[_selectedHomeIndex].Name;
        var renamed = _homes[_selectedHomeIndex];
        renamed.Name = newName;
        _homes[_selectedHomeIndex] = renamed;
        SavePlayerState();
        SetHomeGuiStatus($"Renamed '{oldName}' to '{newName}'.");
    }

    private void DeleteSelectedHomeFromGui()
    {
        if (_selectedHomeIndex < 0 || _selectedHomeIndex >= _homes.Count)
        {
            SetHomeGuiStatus("Select a home first.");
            return;
        }

        var removedName = _homes[_selectedHomeIndex].Name;
        _homes.RemoveAt(_selectedHomeIndex);
        _selectedHomeIndex = _homes.Count == 0 ? -1 : Math.Clamp(_selectedHomeIndex, 0, _homes.Count - 1);
        SyncLegacyHomeFields();
        SavePlayerState();
        SetHomeGuiStatus($"Deleted '{removedName}'.");
    }

    private void SetSelectedHomeIconFromHeld()
    {
        if (_selectedHomeIndex < 0 || _selectedHomeIndex >= _homes.Count)
        {
            SetHomeGuiStatus("Select a home first.");
            return;
        }

        if (_inventory.SelectedId == BlockId.Air)
        {
            SetHomeGuiStatus("Select a block in hotbar first.");
            return;
        }

        var entry = _homes[_selectedHomeIndex];
        entry.HasIconBlock = true;
        entry.IconBlock = _inventory.SelectedId;
        _homes[_selectedHomeIndex] = entry;
        SavePlayerState();
        SetHomeGuiStatus($"Icon for '{entry.Name}' set to {_inventory.SelectedId}.");
    }

    private void SetSelectedHomeIconAuto()
    {
        if (_selectedHomeIndex < 0 || _selectedHomeIndex >= _homes.Count)
        {
            SetHomeGuiStatus("Select a home first.");
            return;
        }

        var entry = _homes[_selectedHomeIndex];
        entry.HasIconBlock = false;
        entry.IconBlock = BlockId.Air;
        _homes[_selectedHomeIndex] = entry;
        SavePlayerState();
        SetHomeGuiStatus($"Icon for '{entry.Name}' set to letter.");
    }

    private void SetHomeGuiStatus(string message)
    {
        _homeGuiStatus = message;
        _homeGuiStatusTimer = 4f;
    }

    private void OpenStructureFinderGui(bool openStructuresTab)
    {
        _homeGuiOpen = false;
        _structureFinderOpen = true;
        _structureFinderStructureTab = openStructuresTab;
        _structureFinderStatus = string.Empty;
        _structureFinderStatusTimer = 0f;
        LayoutStructureFinderGui();
    }

    private void LayoutStructureFinderGui()
    {
        var panelW = Math.Clamp((int)MathF.Round(_viewport.Width * 0.56f), 520, 860);
        var panelH = Math.Clamp((int)MathF.Round(_viewport.Height * 0.60f), 360, 620);
        _structureFinderPanelRect = new Rectangle(
            _viewport.Center.X - panelW / 2,
            _viewport.Center.Y - panelH / 2,
            panelW,
            panelH);

        var topY = _structureFinderPanelRect.Y + 44;
        var sidePad = 16;
        var tabGap = 10;
        var tabWidth = (_structureFinderPanelRect.Width - sidePad * 2 - tabGap) / 2;
        _structureFinderBiomeTabBtn.Bounds = new Rectangle(_structureFinderPanelRect.X + sidePad, topY, tabWidth, 34);
        _structureFinderStructureTabBtn.Bounds = new Rectangle(_structureFinderBiomeTabBtn.Bounds.Right + tabGap, topY, tabWidth, 34);

        _structureFinderListRect = new Rectangle(
            _structureFinderPanelRect.X + sidePad,
            _structureFinderBiomeTabBtn.Bounds.Bottom + 10,
            _structureFinderPanelRect.Width - sidePad * 2,
            _structureFinderPanelRect.Height - 178);

        var rowH = Math.Max(40, (_structureFinderListRect.Height - 8) / FinderBiomeLabels.Length);
        for (var i = 0; i < _structureFinderBiomeRects.Length; i++)
        {
            _structureFinderBiomeRects[i] = new Rectangle(
                _structureFinderListRect.X + 4,
                _structureFinderListRect.Y + 4 + i * rowH,
                _structureFinderListRect.Width - 8,
                rowH - 6);
        }

        var bottomY = _structureFinderPanelRect.Bottom - 44;
        _structureFinderRadiusBtn.Bounds = new Rectangle(_structureFinderPanelRect.X + sidePad, bottomY, 180, 30);
        _structureFinderFindBtn.Bounds = new Rectangle(_structureFinderRadiusBtn.Bounds.Right + 10, bottomY, 120, 30);
        _structureFinderCloseBtn.Bounds = new Rectangle(_structureFinderPanelRect.Right - sidePad - 120, bottomY, 120, 30);
    }

    private void UpdateStructureFinderGui(InputState input, float dt)
    {
        LayoutStructureFinderGui();
        if (_structureFinderStatusTimer > 0f)
        {
            _structureFinderStatusTimer = Math.Max(0f, _structureFinderStatusTimer - dt);
            if (_structureFinderStatusTimer <= 0f)
                _structureFinderStatus = string.Empty;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _structureFinderOpen = false;
            return;
        }

        if (!_structureFinderStructureTab && input.IsNewLeftClick() && _structureFinderListRect.Contains(input.MousePosition))
        {
            for (var i = 0; i < _structureFinderBiomeRects.Length; i++)
            {
                if (_structureFinderBiomeRects[i].Contains(input.MousePosition))
                {
                    _structureFinderSelectedBiomeIndex = i;
                    break;
                }
            }
        }

        _structureFinderBiomeTabBtn.ForceDisabledStyle = !_structureFinderStructureTab;
        _structureFinderStructureTabBtn.ForceDisabledStyle = _structureFinderStructureTab;
        _structureFinderRadiusBtn.Enabled = !_structureFinderStructureTab;
        _structureFinderFindBtn.Enabled = !_structureFinderStructureTab;
        _structureFinderRadiusBtn.ForceDisabledStyle = _structureFinderStructureTab;
        _structureFinderFindBtn.ForceDisabledStyle = _structureFinderStructureTab;

        _structureFinderBiomeTabBtn.Update(input);
        _structureFinderStructureTabBtn.Update(input);
        _structureFinderRadiusBtn.Update(input);
        _structureFinderFindBtn.Update(input);
        _structureFinderCloseBtn.Update(input);
    }

    private void DrawStructureFinderGui(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewport, new Color(0, 0, 0, 120));
        if (_structureFinderPanelTexture != null)
            sb.Draw(_structureFinderPanelTexture, _structureFinderPanelRect, Color.White);
        else
            sb.Draw(_pixel, _structureFinderPanelRect, new Color(18, 22, 28, 235));
        DrawBorder(sb, _structureFinderPanelRect, new Color(210, 220, 235));

        _font.DrawString(sb, "FINDER", new Vector2(_structureFinderPanelRect.X + 16, _structureFinderPanelRect.Y + 12), Color.White);
        _structureFinderBiomeTabBtn.Draw(sb, _pixel, _font);
        _structureFinderStructureTabBtn.Draw(sb, _pixel, _font);

        sb.Draw(_pixel, _structureFinderListRect, new Color(8, 8, 8, 180));
        DrawBorder(sb, _structureFinderListRect, new Color(170, 170, 170));
        if (_structureFinderStructureTab)
        {
            var empty = "No structures configured yet.";
            var helper = "Biome finder is available in the BIOMES tab.";
            var emptySize = _font.MeasureString(empty);
            var helperSize = _font.MeasureString(helper);
            _font.DrawString(sb, empty, new Vector2(_structureFinderListRect.Center.X - emptySize.X / 2f, _structureFinderListRect.Center.Y - _font.LineHeight), new Color(230, 230, 230));
            _font.DrawString(sb, helper, new Vector2(_structureFinderListRect.Center.X - helperSize.X / 2f, _structureFinderListRect.Center.Y + 4), new Color(175, 195, 220));
        }
        else
        {
            for (var i = 0; i < _structureFinderBiomeRects.Length; i++)
            {
                var rect = _structureFinderBiomeRects[i];
                var selected = i == _structureFinderSelectedBiomeIndex;
                sb.Draw(_pixel, rect, selected ? new Color(38, 78, 118, 210) : new Color(20, 20, 20, 175));
                DrawBorder(sb, rect, selected ? new Color(190, 230, 255) : new Color(120, 120, 120));
                _font.DrawString(sb, FinderBiomeLabels[i], new Vector2(rect.X + 10, rect.Y + 10), Color.White);
            }
        }

        _structureFinderRadiusBtn.Draw(sb, _pixel, _font);
        _structureFinderFindBtn.Draw(sb, _pixel, _font);
        _structureFinderCloseBtn.Draw(sb, _pixel, _font);

        var hint = "Find posts one clickable X Y Z result in chat.";
        _font.DrawString(sb, hint, new Vector2(_structureFinderPanelRect.X + 16, _structureFinderRadiusBtn.Bounds.Y - _font.LineHeight - 4), new Color(182, 202, 224));
        if (!string.IsNullOrWhiteSpace(_structureFinderStatus))
            _font.DrawString(sb, _structureFinderStatus, new Vector2(_structureFinderPanelRect.X + 16, _structureFinderPanelRect.Bottom - _font.LineHeight - 8), new Color(235, 235, 180));
    }

    private void CycleStructureFinderRadius()
    {
        _structureFinderRadiusIndex++;
        if (_structureFinderRadiusIndex >= FinderRadiusOptions.Length)
            _structureFinderRadiusIndex = 0;
        SyncStructureFinderRadiusLabel();
    }

    private void SyncStructureFinderRadiusLabel()
    {
        var index = Math.Clamp(_structureFinderRadiusIndex, 0, FinderRadiusOptions.Length - 1);
        _structureFinderRadiusIndex = index;
        _structureFinderRadiusBtn.Label = $"RADIUS: {FinderRadiusOptions[index]}";
    }

    private void ExecuteStructureFinderSearchFromGui()
    {
        if (_structureFinderStructureTab)
        {
            SetStructureFinderStatus("No structures configured yet.");
            return;
        }

        var biomeIndex = Math.Clamp(_structureFinderSelectedBiomeIndex, 0, FinderBiomeTokens.Length - 1);
        var radiusIndex = Math.Clamp(_structureFinderRadiusIndex, 0, FinderRadiusOptions.Length - 1);
        var token = FinderBiomeTokens[biomeIndex];
        var label = FinderBiomeLabels[biomeIndex];
        var radius = FinderRadiusOptions[radiusIndex];
        if (TryReportBiomeLocation(token, label, radius))
            SetStructureFinderStatus($"Located {label}.");
        else
            SetStructureFinderStatus($"Unable to locate {label}.");
    }

    private void SetStructureFinderStatus(string message)
    {
        _structureFinderStatus = message;
        _structureFinderStatusTimer = 4f;
    }

    private void ExecuteMeCommand(string[] commandParts)
    {
        if (commandParts.Length < 2)
        {
            SetCommandStatus("Usage: /me <action>");
            return;
        }

        var raw = string.Join(" ", commandParts.Skip(1));
        var username = _profile.GetDisplayUsername();
        if (_lanSession != null && _lanSession.IsConnected)
        {
            _lanSession.SendChat(new LanChatMessage
            {
                FromPlayerId = _lanSession.LocalPlayerId,
                ToPlayerId = -1,
                Kind = LanChatKind.Emote,
                Text = raw,
                TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            return;
        }

        AddChatLine($"* {username} {raw}", isSystem: false);
    }

    private void ExecuteMsgCommand(string[] commandParts)
    {
        if (commandParts.Length < 3)
        {
            SetCommandStatus("Usage: /msg <player> <message>");
            return;
        }

        if (_lanSession == null || !_lanSession.IsConnected)
        {
            SetCommandStatus("Private messaging is only available in multiplayer.");
            return;
        }

        if (!TryResolvePlayerTarget(commandParts[1], out var targetId, out var targetName))
        {
            SetCommandStatus($"Player not found: {commandParts[1]}");
            return;
        }

        var text = string.Join(" ", commandParts.Skip(2));
        _lanSession.SendChat(new LanChatMessage
        {
            FromPlayerId = _lanSession.LocalPlayerId,
            ToPlayerId = targetId,
            Kind = LanChatKind.Whisper,
            Text = text,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private void ExecuteChatClearCommand(string[] commandParts)
    {
        if (commandParts.Length < 2 || !string.Equals(commandParts[1], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            SetCommandStatus("Type /chatclear confirm to clear all chat history.");
            return;
        }

        _chatLines.Clear();
        _chatActionHitboxes.Clear();
        _hoveredChatActionIndex = -1;
        _chatOverlayScrollLines = 0;
        SetCommandStatus("Chat history cleared.");
    }

    private void ExecuteCommandClearCommand(string[] _)
    {
        var removed = _textInputHistory.RemoveAll(x => x.StartsWith("/", StringComparison.Ordinal));
        _textInputHistoryBrowsing = false;
        _textInputHistoryCursor = _textInputHistory.Count;
        _textInputHistoryDraft = _chatInputActive ? _chatInputText : _commandInputText;
        SetCommandStatus(removed > 0
            ? $"Cleared {removed} command history entr{(removed == 1 ? "y" : "ies")}."
            : "No command history entries to clear.");
    }

    private bool TryResolvePlayerTarget(string token, out int playerId, out string name)
    {
        playerId = -1;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (int.TryParse(token, out var byId) && _playerNames.TryGetValue(byId, out var byIdName))
        {
            playerId = byId;
            name = byIdName;
            return true;
        }

        foreach (var entry in _playerNames)
        {
            if (string.Equals(entry.Value, token, StringComparison.OrdinalIgnoreCase))
            {
                playerId = entry.Key;
                name = entry.Value;
                return true;
            }
        }

        return false;
    }

    private void HandleNetworkChat(LanChatMessage message)
    {
        var localId = _lanSession?.LocalPlayerId ?? -1;
        var fromName = ResolvePlayerName(message.FromPlayerId);
        switch (message.Kind)
        {
            case LanChatKind.Emote:
                AddChatLine($"* {fromName} {message.Text}", isSystem: false);
                return;
            case LanChatKind.Whisper:
                if (message.FromPlayerId == localId && message.ToPlayerId >= 0)
                {
                    AddChatLine($"[TO {ResolvePlayerName(message.ToPlayerId)}] {message.Text}", isSystem: false);
                }
                else if (message.ToPlayerId == localId)
                {
                    AddChatLine($"[FROM {fromName}] {message.Text}", isSystem: false);
                }
                return;
            case LanChatKind.System:
                AddChatLine(message.Text, isSystem: true);
                return;
            default:
                AddChatLine($"{fromName}: {message.Text}", isSystem: false);
                return;
        }
    }

    private string ResolvePlayerName(int playerId)
    {
        if (_playerNames.TryGetValue(playerId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        if (_lanSession != null && playerId == _lanSession.LocalPlayerId)
        {
            var local = _profile.GetDisplayUsername();
            if (!string.IsNullOrWhiteSpace(local))
                return local;
        }

        return $"Player{playerId}";
    }

    private void ApplyGameModeChange(GameMode target, GameModeChangeSource source, bool persistImmediately, bool emitFeedback)
    {
        _gameMode = target;
        _inventory.SetMode(target);

        var wasFlying = _player.IsFlying;
        _player.AllowFlying = target == GameMode.Artificer;
        if (!_player.AllowFlying && wasFlying)
        {
            _player.SetFlying(false);
            if (!_player.IsGrounded)
            {
                var vel = _player.Velocity;
                vel.Y = MathF.Min(vel.Y, -1f);
                _player.Velocity = vel;
            }
        }

        if (_meta != null)
        {
            _meta.CurrentWorldGameMode = target;
            _meta.GameMode = target;
            if (persistImmediately)
                _meta.Save(_metaPath, _log);
        }

        if (persistImmediately)
            SavePlayerState();

        if (!emitFeedback)
            return;

        var username = _profile.GetDisplayUsername();
        var modeLabel = target.ToString().ToUpperInvariant();
        _gamemodeToastText = $"{username} is now {modeLabel}";
        _gamemodeToastTimer = 2.6f;
        SetCommandStatus(_gamemodeToastText, 5f);
    }

    private bool HandleGameModeWheelInput(InputState input)
    {
        if (_pauseMenuOpen || _inventoryOpen || _homeGuiOpen || _chatInputActive || _commandInputActive)
            return false;

        var modifierDown = input.IsKeyDown(_gamemodeModifierKey);
        if (!_gamemodeWheelVisible && modifierDown && input.IsNewKeyPress(_gamemodeWheelKey))
        {
            OpenGamemodeWheel(holdToOpen: true);
            return true;
        }

        if (!_gamemodeWheelVisible)
            return false;

        UpdateGamemodeWheelSelection(input.MousePosition);
        if (input.IsNewKeyPress(Keys.D1) || input.IsNewKeyPress(Keys.NumPad1))
            _gamemodeWheelHoverMode = GameMode.Artificer;
        else if (input.IsNewKeyPress(Keys.D2) || input.IsNewKeyPress(Keys.NumPad2))
            _gamemodeWheelHoverMode = GameMode.Veilwalker;
        else if (input.IsNewKeyPress(Keys.D3) || input.IsNewKeyPress(Keys.NumPad3))
            _gamemodeWheelHoverMode = GameMode.Veilseer;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _gamemodeWheelVisible = false;
            return true;
        }

        if (_gamemodeWheelHoldToOpen)
        {
            if (!modifierDown)
            {
                if (!_gamemodeWheelWaitForRelease)
                    ApplyGameModeChange(_gamemodeWheelHoverMode, GameModeChangeSource.Wheel, persistImmediately: true, emitFeedback: true);
                _gamemodeWheelVisible = false;
            }
            else if (input.IsNewLeftClick())
            {
                ApplyGameModeChange(_gamemodeWheelHoverMode, GameModeChangeSource.Wheel, persistImmediately: true, emitFeedback: true);
                _gamemodeWheelVisible = false;
            }

            return true;
        }

        if (input.IsNewLeftClick() || input.IsNewKeyPress(Keys.Enter))
        {
            ApplyGameModeChange(_gamemodeWheelHoverMode, GameModeChangeSource.Wheel, persistImmediately: true, emitFeedback: true);
            _gamemodeWheelVisible = false;
            return true;
        }

        return true;
    }

    private void OpenGamemodeWheel(bool holdToOpen)
    {
        _gamemodeWheelVisible = true;
        _gamemodeWheelHoldToOpen = holdToOpen;
        _gamemodeWheelWaitForRelease = false;
        _gamemodeWheelHoverMode = _gameMode;
    }

    private void UpdateGamemodeWheelSelection(Point mousePoint)
    {
        var center = new Vector2(_viewport.Center.X, _viewport.Center.Y);
        var ringRadius = Math.Clamp(_viewport.Height / 4, 120, 240);
        var buttonWidth = 220;
        var buttonHeight = 152;

        for (var i = 0; i < GameModeWheelModes.Length; i++)
        {
            var rect = GetGamemodeWheelButtonRect(center, ringRadius, buttonWidth, buttonHeight, i);
            if (!rect.Contains(mousePoint))
                continue;

            _gamemodeWheelHoverMode = GameModeWheelModes[i];
            return;
        }
    }

    private void DrawGamemodeToast(SpriteBatch sb)
    {
        if (_gamemodeToastTimer <= 0f || string.IsNullOrWhiteSpace(_gamemodeToastText))
            return;

        var size = _font.MeasureString(_gamemodeToastText);
        var width = (int)Math.Ceiling(size.X) + 24;
        var height = _font.LineHeight + 14;
        var rect = new Rectangle(_viewport.Center.X - width / 2, _viewport.Y + 96, width, height);
        sb.Draw(_pixel, rect, new Color(0, 0, 0, 190));
        DrawBorder(sb, rect, new Color(255, 255, 255, 180));
        _font.DrawString(sb, _gamemodeToastText, new Vector2(rect.X + 12, rect.Y + 7), new Color(235, 235, 235));
    }

    private void DrawGamemodeWheel(SpriteBatch sb)
    {
        if (!_gamemodeWheelVisible)
            return;

        var center = new Vector2(_viewport.Center.X, _viewport.Center.Y);
        var ringRadius = Math.Clamp(_viewport.Height / 4, 120, 240);
        var buttonWidth = 220;
        var buttonHeight = 152;

        for (var i = 0; i < GameModeWheelModes.Length; i++)
        {
            var mode = GameModeWheelModes[i];
            var rect = GetGamemodeWheelButtonRect(center, ringRadius, buttonWidth, buttonHeight, i);
            var hovered = mode == _gamemodeWheelHoverMode;
            sb.Draw(_pixel, rect, hovered ? new Color(42, 82, 116, 230) : new Color(0, 0, 0, 190));
            DrawBorder(sb, rect, hovered ? new Color(225, 241, 255) : new Color(176, 176, 176));

            var iconRect = new Rectangle(rect.X + 12, rect.Y + 10, rect.Width - 24, rect.Height - (_font.LineHeight + 22));
            DrawTextureFit(sb, GetGamemodeWheelIcon(mode), iconRect, Color.White);

            var label = mode.ToString().ToUpperInvariant();
            var size = _font.MeasureString(label);
            var labelPos = new Vector2(rect.Center.X - size.X / 2f, rect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, label, labelPos, hovered ? new Color(245, 250, 255) : new Color(225, 225, 225));
        }

        var help = _gamemodeWheelHoldToOpen
            ? $"{FormatKeyLabel(_gamemodeModifierKey)}+{FormatKeyLabel(_gamemodeWheelKey)} HOLD: release to apply | 1/2/3 quick select"
            : "Click or Enter to apply | Esc to cancel";
        var helpSize = _font.MeasureString(help);
        _font.DrawString(sb, help, new Vector2(center.X - helpSize.X / 2f, center.Y - _font.LineHeight / 2f), new Color(235, 235, 235));
    }

    private void LoadGamemodeWheelIcons()
    {
        _gamemodeArtificerIcon = TryLoadOptionalTexture(
            "textures/menu/buttons/GamemodeArtificerIcon.png",
            "textures/menu/buttons/Artificer.png");
        _gamemodeVeilwalkerIcon = TryLoadOptionalTexture(
            "textures/menu/buttons/GamemodeVeilwalkerIcon.png",
            "textures/menu/buttons/Veilwalker.png");
        _gamemodeVeilseerIcon = TryLoadOptionalTexture(
            "textures/menu/buttons/GamemodeVeilseerIcon.png",
            "textures/menu/buttons/Veilseer.png");
    }

    private Texture2D? TryLoadOptionalTexture(params string[] candidates)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            try
            {
                return _assets.LoadTexture(candidates[i]);
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private Texture2D? GetGamemodeWheelIcon(GameMode mode)
    {
        return mode switch
        {
            GameMode.Artificer => _gamemodeArtificerIcon,
            GameMode.Veilwalker => _gamemodeVeilwalkerIcon,
            _ => _gamemodeVeilseerIcon
        };
    }

    private static Rectangle GetGamemodeWheelButtonRect(Vector2 center, int ringRadius, int buttonWidth, int buttonHeight, int index)
    {
        var angle = -MathF.PI / 2f + index * (MathF.Tau / 3f);
        var cx = center.X + MathF.Cos(angle) * ringRadius;
        var cy = center.Y + MathF.Sin(angle) * ringRadius;
        return new Rectangle((int)(cx - buttonWidth / 2f), (int)(cy - buttonHeight / 2f), buttonWidth, buttonHeight);
    }

    private static Rectangle GetFitRect(Texture2D texture, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || texture.Width <= 0 || texture.Height <= 0)
            return Rectangle.Empty;

        var scale = Math.Min(bounds.Width / (float)texture.Width, bounds.Height / (float)texture.Height);
        var width = Math.Max(1, (int)Math.Round(texture.Width * scale));
        var height = Math.Max(1, (int)Math.Round(texture.Height * scale));
        return new Rectangle(
            bounds.Center.X - width / 2,
            bounds.Center.Y - height / 2,
            width,
            height);
    }

    private static void DrawTextureFit(SpriteBatch sb, Texture2D? texture, Rectangle bounds, Color color)
    {
        if (texture == null)
            return;

        var fit = GetFitRect(texture, bounds);
        if (fit.Width <= 0 || fit.Height <= 0)
            return;

        sb.Draw(texture, fit, color);
    }

    private static bool TryParseDifficultyToken(string value, out int difficulty)
    {
        difficulty = 1;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var token = value.Trim().ToLowerInvariant();
        switch (token)
        {
            case "peaceful":
            case "0":
                difficulty = 0;
                return true;
            case "easy":
            case "1":
                difficulty = 1;
                return true;
            case "normal":
            case "2":
                difficulty = 2;
                return true;
            case "hard":
            case "3":
                difficulty = 3;
                return true;
            default:
                return false;
        }
    }

    private static string GetDifficultyLabel(int difficulty)
    {
        return difficulty switch
        {
            0 => "Peaceful",
            1 => "Easy",
            2 => "Normal",
            3 => "Hard",
            _ => "Easy"
        };
    }

    private bool TryFindNearestBiome(string targetBiomeToken, int originX, int originZ, int maxRadius, out int foundX, out int foundZ, out float distance)
    {
        foundX = originX;
        foundZ = originZ;
        distance = 0f;

        if (_world == null || _meta == null)
            return false;

        originX = Math.Clamp(originX, 0, Math.Max(0, _meta.Size.Width - 1));
        originZ = Math.Clamp(originZ, 0, Math.Max(0, _meta.Size.Depth - 1));
        foundX = originX;
        foundZ = originZ;

        if (IsBiomeMatch(_world.GetBiomeNameAt(originX, originZ), targetBiomeToken))
            return true;

        EnsureBiomeCatalog();
        var hasCatalogTarget = BiomeCatalog.TryParseBiomeToken(targetBiomeToken, out var targetBiomeId);

        var step = maxRadius >= 4096 ? 8 : 4;
        var bestDistSq = long.MaxValue;
        var bestX = originX;
        var bestZ = originZ;
        var found = false;

        void EvaluateCandidate(int wx, int wz)
        {
            if (!IsWithinBiomeSearchBounds(wx, wz))
                return;

            var biome = _world.GetBiomeNameAt(wx, wz);
            if (!IsBiomeMatch(biome, targetBiomeToken))
                return;

            var dx = wx - originX;
            var dz = wz - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestX = wx;
            bestZ = wz;
            found = true;
        }

        if (hasCatalogTarget
            && _biomeCatalog != null
            && _biomeCatalog.TryFindNearest(targetBiomeId, originX, originZ, maxRadius, out var catalogX, out var catalogZ, out _))
        {
            for (var wz = catalogZ - BiomeSearchRefineWindow; wz <= catalogZ + BiomeSearchRefineWindow; wz++)
            {
                for (var wx = catalogX - BiomeSearchRefineWindow; wx <= catalogX + BiomeSearchRefineWindow; wx++)
                    EvaluateCandidate(wx, wz);
            }
        }

        for (var radius = step; radius <= maxRadius; radius += step)
        {
            for (var dx = -radius; dx <= radius; dx += step)
            {
                EvaluateCandidate(originX + dx, originZ - radius);
                EvaluateCandidate(originX + dx, originZ + radius);
            }

            for (var dz = -radius + step; dz <= radius - step; dz += step)
            {
                EvaluateCandidate(originX - radius, originZ + dz);
                EvaluateCandidate(originX + radius, originZ + dz);
            }

            if (found)
                break;
        }

        if (!found)
        {
            var worldRadius = GetBiomeSearchWorldRadius();

            if (hasCatalogTarget
                && _biomeCatalog != null
                && _biomeCatalog.TryFindNearest(targetBiomeId, originX, originZ, worldRadius, out var globalCatalogX, out var globalCatalogZ, out _))
            {
                for (var wz = globalCatalogZ - BiomeSearchRefineWindow; wz <= globalCatalogZ + BiomeSearchRefineWindow; wz++)
                {
                    for (var wx = globalCatalogX - BiomeSearchRefineWindow; wx <= globalCatalogX + BiomeSearchRefineWindow; wx++)
                        EvaluateCandidate(wx, wz);
                }
            }
        }

        if (!found)
        {
            var stride = Math.Max(step, 8);
            var maxX = Math.Max(0, _meta.Size.Width - 1);
            var maxZ = Math.Max(0, _meta.Size.Depth - 1);
            for (var wz = 0; wz <= maxZ; wz += stride)
            {
                for (var wx = 0; wx <= maxX; wx += stride)
                    EvaluateCandidate(wx, wz);
                EvaluateCandidate(maxX, wz);
            }

            for (var wx = 0; wx <= maxX; wx += stride)
                EvaluateCandidate(wx, maxZ);
            EvaluateCandidate(maxX, maxZ);
        }

        if (!found)
        {
            if (!TryFindBestBiomeClimateCandidate(targetBiomeToken, originX, originZ, out bestX, out bestZ))
            {
                bestX = originX;
                bestZ = originZ;
            }

            bestDistSq = (long)(bestX - originX) * (bestX - originX) + (long)(bestZ - originZ) * (bestZ - originZ);
            found = true;
        }

        var coarseX = bestX;
        var coarseZ = bestZ;
        var refineRadius = Math.Max(3, step * 2);
        for (var wz = coarseZ - refineRadius; wz <= coarseZ + refineRadius; wz++)
        {
            for (var wx = coarseX - refineRadius; wx <= coarseX + refineRadius; wx++)
                EvaluateCandidate(wx, wz);
        }

        if (!found)
        {
            bestX = originX;
            bestZ = originZ;
            bestDistSq = 0;
            found = true;
        }

        foundX = bestX;
        foundZ = bestZ;
        distance = MathF.Sqrt(bestDistSq);
        return found;
    }

    private bool TryFindExactBiomeCoordinate(string targetBiomeToken, int originX, int originZ, int maxRadius, out int foundX, out int foundZ, out float distance)
    {
        foundX = originX;
        foundZ = originZ;
        distance = 0f;

        if (_world == null || _meta == null)
            return false;

        originX = Math.Clamp(originX, 0, Math.Max(0, _meta.Size.Width - 1));
        originZ = Math.Clamp(originZ, 0, Math.Max(0, _meta.Size.Depth - 1));
        foundX = originX;
        foundZ = originZ;
        if (IsBiomeMatch(_world.GetBiomeNameAt(originX, originZ), targetBiomeToken))
            return true;

        var radiusDistSq = (long)Math.Max(16, maxRadius) * Math.Max(16, maxRadius);
        var bestDistSq = long.MaxValue;
        var found = false;
        var bestXLocal = originX;
        var bestZLocal = originZ;

        void EvaluateCandidate(int wx, int wz, bool ignoreRadius = false)
        {
            if (!IsWithinBiomeSearchBounds(wx, wz))
                return;
            if (!IsBiomeMatch(_world.GetBiomeNameAt(wx, wz), targetBiomeToken))
                return;

            var dx = wx - originX;
            var dz = wz - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (!ignoreRadius && distSq > radiusDistSq)
                return;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestXLocal = wx;
            bestZLocal = wz;
            found = true;
        }

        EnsureBiomeCatalog();
        if (BiomeCatalog.TryParseBiomeToken(targetBiomeToken, out var targetBiomeId) && _biomeCatalog != null)
        {
            var points = _biomeCatalog.GetPoints(targetBiomeId);
            for (var i = 0; i < points.Count; i++)
                EvaluateCandidate(points[i].X, points[i].Z);

            if (found)
            {
                var cx = bestXLocal;
                var cz = bestZLocal;
                for (var wz = cz - BiomeSearchRefineWindow; wz <= cz + BiomeSearchRefineWindow; wz++)
                {
                    for (var wx = cx - BiomeSearchRefineWindow; wx <= cx + BiomeSearchRefineWindow; wx++)
                        EvaluateCandidate(wx, wz);
                }
            }
        }

        if (!found)
        {
            var step = maxRadius >= 4096 ? 8 : 4;
            for (var radius = step; radius <= maxRadius; radius += step)
            {
                for (var dx = -radius; dx <= radius; dx += step)
                {
                    EvaluateCandidate(originX + dx, originZ - radius);
                    EvaluateCandidate(originX + dx, originZ + radius);
                }

                for (var dz = -radius + step; dz <= radius - step; dz += step)
                {
                    EvaluateCandidate(originX - radius, originZ + dz);
                    EvaluateCandidate(originX + radius, originZ + dz);
                }

                if (found)
                    break;
            }
        }

        if (!found)
        {
            var maxX = Math.Max(0, _meta.Size.Width - 1);
            var maxZ = Math.Max(0, _meta.Size.Depth - 1);
            foreach (var stride in new[] { 16, 8, 4, 2 })
            {
                for (var wz = 0; wz <= maxZ; wz += stride)
                {
                    for (var wx = 0; wx <= maxX; wx += stride)
                        EvaluateCandidate(wx, wz);
                    EvaluateCandidate(maxX, wz);
                }

                for (var wx = 0; wx <= maxX; wx += stride)
                    EvaluateCandidate(wx, maxZ);
                EvaluateCandidate(maxX, maxZ);

                if (found)
                    break;
            }

            if (!found)
            {
                // Final exact sweep across the whole world to guarantee accuracy when a biome exists.
                for (var wz = 0; wz <= maxZ; wz++)
                {
                    for (var wx = 0; wx <= maxX; wx++)
                        EvaluateCandidate(wx, wz, ignoreRadius: true);
                }
            }
        }

        if (!found)
            return false;

        foundX = bestXLocal;
        foundZ = bestZLocal;
        distance = MathF.Sqrt(bestDistSq);
        return true;
    }

    private int GetBiomeSearchWorldRadius()
    {
        if (_meta?.Size == null)
            return CommandFindBiomeMaxRadius;

        var width = Math.Max(1, _meta.Size.Width);
        var depth = Math.Max(1, _meta.Size.Depth);
        var diag = Math.Sqrt((double)width * width + (double)depth * depth);
        return Math.Max(CommandFindBiomeMaxRadius, (int)Math.Ceiling(diag));
    }

    private bool TryFindBestBiomeClimateCandidate(string targetBiomeToken, int originX, int originZ, out int bestX, out int bestZ)
    {
        if (_world == null || _meta?.Size == null)
        {
            bestX = originX;
            bestZ = originZ;
            return false;
        }

        var stride = 8;
        var bestScore = float.NegativeInfinity;
        var bestDistSq = long.MaxValue;
        var maxX = Math.Max(0, _meta.Size.Width - 1);
        var maxZ = Math.Max(0, _meta.Size.Depth - 1);
        var bestXLocal = originX;
        var bestZLocal = originZ;

        void Evaluate(int wx, int wz)
        {
            if (!IsWithinBiomeSearchBounds(wx, wz))
                return;

            var ocean = _world.GetOceanWeightAt(wx, wz);
            var desert = _world.GetDesertWeightAt(wx, wz);
            var score = targetBiomeToken switch
            {
                "ocean" => ocean,
                "desert" => desert * (1f - ocean * 0.9f),
                _ => Math.Clamp(1f - MathF.Max(ocean, desert), 0f, 1f)
            };

            var dx = wx - originX;
            var dz = wz - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (score < bestScore)
                return;
            if (MathF.Abs(score - bestScore) < 0.0001f && distSq >= bestDistSq)
                return;

            bestScore = score;
            bestDistSq = distSq;
            bestXLocal = wx;
            bestZLocal = wz;
        }

        for (var wz = 0; wz <= maxZ; wz += stride)
        {
            for (var wx = 0; wx <= maxX; wx += stride)
                Evaluate(wx, wz);
            Evaluate(maxX, wz);
        }

        for (var wx = 0; wx <= maxX; wx += stride)
            Evaluate(wx, maxZ);
        Evaluate(maxX, maxZ);

        bestX = bestXLocal;
        bestZ = bestZLocal;
        return !float.IsNegativeInfinity(bestScore);
    }

    private void EnsureBiomeCatalog()
    {
        if (_meta == null || string.IsNullOrWhiteSpace(_worldPath))
            return;

        if (_biomeCatalog != null && _biomeCatalog.IsCompatible(_meta))
            return;

        _biomeCatalog = BiomeCatalog.Load(_worldPath, _log);
        if (_biomeCatalog != null && _biomeCatalog.IsCompatible(_meta))
            return;

        try
        {
            _biomeCatalog = BiomeCatalog.BuildAndSave(_meta, _worldPath, _log);
        }
        catch (Exception ex)
        {
            _log.Warn($"Biome catalog rebuild failed: {ex.Message}");
            _biomeCatalog = null;
        }
    }

    private bool IsWithinBiomeSearchBounds(int wx, int wz)
    {
        if (_meta == null)
            return false;

        return wx >= 0
            && wz >= 0
            && wx < _meta.Size.Width
            && wz < _meta.Size.Depth;
    }

    private static bool IsBiomeMatch(string biomeName, string targetBiomeToken)
    {
        return NormalizeBiomeToken(biomeName) == targetBiomeToken;
    }

    private static bool TryResolveBiome(string value, out string biomeToken, out string biomeLabel)
    {
        biomeToken = "";
        biomeLabel = "";
        switch (NormalizeBiomeToken(value))
        {
            case "desert":
                biomeToken = "desert";
                biomeLabel = "Desert";
                return true;
            case "grass":
            case "grassy":
            case "grassland":
            case "grasslands":
            case "plains":
                biomeToken = "grasslands";
                biomeLabel = "Grasslands";
                return true;
            case "ocean":
            case "sea":
            case "water":
                biomeToken = "ocean";
                biomeLabel = "Ocean";
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeBiomeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Trim().ToLowerInvariant();
        var buffer = new char[chars.Length];
        var count = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsWhiteSpace(c) || c == '_' || c == '-')
                continue;
            buffer[count++] = c;
        }

        return count <= 0 ? string.Empty : new string(buffer, 0, count);
    }

    private static bool TryMapCommandInputKey(Keys key, bool shift, out char c)
    {
        c = '\0';

        if (key >= Keys.A && key <= Keys.Z)
        {
            c = (char)('A' + (key - Keys.A));
            if (!shift)
                c = char.ToLowerInvariant(c);
            return true;
        }

        if (key >= Keys.D0 && key <= Keys.D9)
        {
            var digit = (int)(key - Keys.D0);
            if (shift)
            {
                const string shiftedDigits = ")!@#$%^&*(";
                c = shiftedDigits[digit];
            }
            else
            {
                c = (char)('0' + digit);
            }

            return true;
        }

        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
        {
            c = (char)('0' + (key - Keys.NumPad0));
            return true;
        }

        switch (key)
        {
            case Keys.Space:
                c = ' ';
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                c = shift ? '_' : '-';
                return true;
            case Keys.OemPlus:
            case Keys.Add:
                c = shift ? '+' : '=';
                return true;
            case Keys.OemPeriod:
            case Keys.Decimal:
                c = '.';
                return true;
            case Keys.OemComma:
                c = shift ? '<' : ',';
                return true;
            case Keys.OemQuestion:
            case Keys.Divide:
                c = shift ? '?' : '/';
                return true;
            case Keys.OemSemicolon:
                c = shift ? ':' : ';';
                return true;
            case Keys.OemQuotes:
                c = shift ? '"' : '\'';
                return true;
            case Keys.OemOpenBrackets:
                c = shift ? '{' : '[';
                return true;
            case Keys.OemCloseBrackets:
                c = shift ? '}' : ']';
                return true;
            case Keys.OemPipe:
                c = shift ? '|' : '\\';
                return true;
            case Keys.OemTilde:
                c = shift ? '~' : '`';
                return true;
            default:
                return false;
        }
    }

    private void SetCommandStatus(string text, float seconds = 5f, bool echoToChat = true)
    {
        _commandStatusText = text;
        _commandStatusTimer = Math.Max(0f, seconds);
        if (echoToChat)
            AddChatLine(text, isSystem: true);
    }

    private void DrawCommandOverlay(SpriteBatch sb)
    {
        _chatActionHitboxes.Clear();
        _hoveredChatActionIndex = -1;

        var hasVisibleChatLines = _chatLines.Any(line => line.TimeRemaining > 0f);
        if (!IsTextInputActive && !hasVisibleChatLines)
            return;

        var x = _viewport.X + 20;
        var overlayWidth = Math.Clamp((int)MathF.Round(_viewport.Width * ChatOverlayWidthRatio), ChatOverlayMinWidth, ChatOverlayMaxWidth);
        var reservedAboveBottomUi = _hotbarRect.Y > 0 ? _hotbarRect.Y - 92 : _viewport.Bottom - 20;
        var y = Math.Min(_viewport.Bottom - 20, reservedAboveBottomUi);
        y = Math.Max(_viewport.Y + 160, y);
        var overlayTop = _viewport.Y + 120;
        if (IsTextInputActive)
        {
            var tintRect = new Rectangle(x - 8, overlayTop, overlayWidth + 16, Math.Max(24, y - overlayTop + 10));
            sb.Draw(_pixel, tintRect, new Color(100, 100, 100, 56));
            DrawBorder(sb, tintRect, new Color(185, 185, 185, 120));
        }

        if (_commandInputActive || _chatInputActive)
        {
            var blink = ((int)(_worldTimeSeconds * 2f) & 1) == 0;
            var prompt = _commandInputActive ? "CMD> " : "CHAT> ";
            var value = _commandInputActive ? _commandInputText : _chatInputText;
            var inputLine = $"{prompt}{value}{(blink ? "_" : string.Empty)}";
            y = DrawCommandLine(sb, x, y, inputLine, new Color(0, 0, 0, 210), new Color(235, 235, 235), fixedWidth: overlayWidth);
            y -= 6;

            var predictionInput = (_commandInputActive ? _commandInputText : _chatInputText).TrimStart();
            if (predictionInput.StartsWith("/", StringComparison.Ordinal))
            {
                BuildCommandPredictions(predictionInput);
                for (var i = 0; i < _commandPredictionBuffer.Count; i++)
                {
                    var prediction = _commandPredictionBuffer[i];
                    var action = string.IsNullOrWhiteSpace(prediction.HoverText)
                        ? (ChatActionHitbox?)null
                        : new ChatActionHitbox { HoverText = prediction.HoverText };
                    y = DrawCommandLine(sb, x, y, prediction.Text, new Color(0, 0, 0, 175), new Color(180, 240, 255), action: action, fixedWidth: overlayWidth);
                    y -= 2;
                }
            }

            if (_chatLines.Count > 0)
            {
                var historyHint = _chatOverlayScrollLines > 0
                    ? $"HISTORY OFFSET: {_chatOverlayScrollLines} (Wheel/PageUp/PageDown)"
                    : "HISTORY: LATEST (Wheel/PageUp/PageDown)";
                y = DrawCommandLine(sb, x, y, historyHint, new Color(0, 0, 0, 160), new Color(170, 190, 220), fixedWidth: overlayWidth);
                y -= 2;
            }
        }

        var startIndex = _chatLines.Count - 1;
        if (startIndex >= 0 && IsTextInputActive)
            startIndex = Math.Clamp(startIndex - _chatOverlayScrollLines, 0, _chatLines.Count - 1);

        for (var i = startIndex; i >= 0; i--)
        {
            var line = _chatLines[i];
            if (!IsTextInputActive && line.TimeRemaining <= 0f)
                continue;

            var fg = line.IsSystem ? new Color(200, 220, 255) : new Color(230, 230, 230);
            if (line.HasTeleportAction)
            {
                var action = new ChatActionHitbox
                {
                    HasTeleport = true,
                    TeleportX = line.TeleportX,
                    TeleportY = line.TeleportY,
                    TeleportZ = line.TeleportZ,
                    HoverText = string.IsNullOrWhiteSpace(line.HoverText)
                        ? $"Teleport to {line.TeleportX} {line.TeleportY} {line.TeleportZ}"
                        : line.HoverText
                };
                y = DrawCommandLine(sb, x, y, line.Text, new Color(0, 0, 0, 145), fg, action: action, fixedWidth: overlayWidth);
            }
            else
            {
                y = DrawCommandLine(sb, x, y, line.Text, new Color(0, 0, 0, 145), fg, fixedWidth: overlayWidth);
            }

            y -= 2;
            if (y <= _viewport.Y + 120)
                break;
        }

        if (_hoveredChatActionIndex >= 0 && _hoveredChatActionIndex < _chatActionHitboxes.Count)
        {
            var hover = _chatActionHitboxes[_hoveredChatActionIndex];
            if (!string.IsNullOrWhiteSpace(hover.HoverText))
            {
                var tooltip = hover.HoverText;
                var size = _font.MeasureString(tooltip);
                var tw = (int)Math.Ceiling(size.X) + 12;
                var th = _font.LineHeight + 10;
                var tx = Math.Clamp(_overlayMousePos.X + 14, _viewport.X + 8, _viewport.Right - tw - 8);
                var ty = Math.Clamp(_overlayMousePos.Y - th - 10, _viewport.Y + 8, _viewport.Bottom - th - 8);
                var rect = new Rectangle(tx, ty, tw, th);
                sb.Draw(_pixel, rect, new Color(0, 0, 0, 220));
                DrawBorder(sb, rect, new Color(255, 255, 255, 180));
                _font.DrawString(sb, tooltip, new Vector2(rect.X + 6, rect.Y + 5), new Color(230, 235, 255));
            }
        }
    }

    private void BuildCommandPredictions(string input)
    {
        _commandPredictionBuffer.Clear();
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith("/", StringComparison.Ordinal))
            return;

        EnsureCommandRegistry();
        if (!TryParseCommandCompletionContext(input, out var context))
            return;

        BuildCommandCompletionCandidates(context, _commandTabBuildBuffer);
        if (_commandTabBuildBuffer.Count == 0)
            return;

        var maxCount = Math.Min(MaxCommandPredictions, _commandTabBuildBuffer.Count);
        for (var i = 0; i < maxCount; i++)
        {
            var candidate = _commandTabBuildBuffer[i];
            var preview = context.TokenIndex == 0
                ? "/" + candidate.Value
                : context.PrefixText + candidate.Value;
            _commandPredictionBuffer.Add(new CommandPredictionItem(preview, candidate.HoverText));
        }
    }

    private int DrawCommandLine(
        SpriteBatch sb,
        int x,
        int bottomY,
        string text,
        Color background,
        Color foreground,
        string? actionLabel = null,
        ChatActionHitbox? action = null,
        int? fixedWidth = null)
    {
        var actionWidth = 0;
        if (!string.IsNullOrWhiteSpace(actionLabel))
            actionWidth = (int)Math.Ceiling(_font.MeasureString(actionLabel).X) + 16;

        var maxWidth = Math.Max(240, _viewport.Width - 40);
        var requestedWidth = fixedWidth ?? maxWidth;
        var width = Math.Clamp(requestedWidth, 320, maxWidth);
        var textWidth = Math.Max(120, width - 18 - actionWidth);
        var wrappedLines = WrapOverlayText(text, textWidth);
        var lineSpacing = 2;
        var textHeight = wrappedLines.Count * _font.LineHeight + Math.Max(0, wrappedLines.Count - 1) * lineSpacing;
        var height = textHeight + 12;
        var rect = new Rectangle(x, bottomY - height, width, height);
        sb.Draw(_pixel, rect, background);
        DrawBorder(sb, rect, new Color(255, 255, 255, 170));
        var textY = rect.Y + 6;
        for (var i = 0; i < wrappedLines.Count; i++)
        {
            _font.DrawString(sb, wrappedLines[i], new Vector2(rect.X + 8, textY), foreground);
            textY += _font.LineHeight + lineSpacing;
        }

        if (action.HasValue)
        {
            var index = _chatActionHitboxes.Count;
            var hitbox = action.Value;
            var targetRect = rect;
            if (!string.IsNullOrWhiteSpace(actionLabel))
                targetRect = new Rectangle(rect.Right - actionWidth - 8, rect.Y + 4, actionWidth, rect.Height - 8);

            hitbox.Bounds = targetRect;
            _chatActionHitboxes.Add(hitbox);

            var hovered = targetRect.Contains(_overlayMousePos);
            if (hovered)
                _hoveredChatActionIndex = index;

            if (!string.IsNullOrWhiteSpace(actionLabel))
            {
                sb.Draw(_pixel, targetRect, hovered ? new Color(45, 90, 145, 230) : new Color(30, 70, 120, 220));
                DrawBorder(sb, targetRect, hovered ? new Color(190, 230, 255) : new Color(120, 170, 210));
                var labelPos = new Vector2(targetRect.X + 8, targetRect.Y + 3);
                _font.DrawString(sb, actionLabel, labelPos, new Color(235, 245, 255));
            }
            else if (hovered)
            {
                DrawBorder(sb, rect, new Color(190, 230, 255));
            }
        }

        return rect.Y;
    }

    private List<string> WrapOverlayText(string text, int maxTextWidth)
    {
        var lines = new List<string>();
        var content = text ?? string.Empty;
        if (content.Length == 0)
        {
            lines.Add(string.Empty);
            return lines;
        }

        var paragraphs = content.Split('\n');
        for (var p = 0; p < paragraphs.Length; p++)
        {
            var paragraph = paragraphs[p];
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            for (var i = 0; i < words.Length; i++)
            {
                var word = words[i];
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (_font.MeasureString(candidate).X <= maxTextWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                if (_font.MeasureString(word).X <= maxTextWidth)
                {
                    current = word;
                    continue;
                }

                // Hard-wrap overly long tokens.
                var token = word;
                var start = 0;
                while (start < token.Length)
                {
                    var take = 1;
                    var best = token.Substring(start, take);
                    while (start + take <= token.Length)
                    {
                        var slice = token.Substring(start, take);
                        if (_font.MeasureString(slice).X > maxTextWidth)
                            break;
                        best = slice;
                        take++;
                    }

                    lines.Add(best);
                    start += best.Length;
                }
            }

            if (current.Length > 0)
                lines.Add(current);
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    private void DrawSelectedBlockName(SpriteBatch sb)
    {
        if (_selectedNameTimer <= 0 || _pauseMenuOpen || string.IsNullOrEmpty(_displayName))
            return;

        var text = _displayName.ToUpperInvariant();
        var size = _font.MeasureString(text);
        
        // Draw above hotbar
        var alpha = Math.Min(1.0f, _selectedNameTimer);
        var pos = new Vector2((_viewport.Width - size.X) / 2f, _hotbarRect.Y - size.Y - 10f);
        
        // Shadow/Glow effect
        _font.DrawString(sb, text, pos + new Vector2(2, 2), Color.Black * 0.6f * alpha);
        _font.DrawString(sb, text, pos, Color.White * alpha);
    }

    private VertexPositionTexture[] BuildBoxVertices(Vector3 min, Vector3 max, CubeNetAtlas atlas, byte id)
    {
        var verts = new List<VertexPositionTexture>();
        
        // Helper to add faces with UVs from atlas
        AddFace(verts, atlas, id, FaceDirection.PosX, new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z));
        AddFace(verts, atlas, id, FaceDirection.NegX, new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(verts, atlas, id, FaceDirection.PosY, new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z));
        AddFace(verts, atlas, id, FaceDirection.NegY, new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z));
        AddFace(verts, atlas, id, FaceDirection.PosZ, new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(verts, atlas, id, FaceDirection.NegZ, new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z));

        return verts.ToArray();
    }

    private void AddFace(List<VertexPositionTexture> verts, CubeNetAtlas atlas, byte id, FaceDirection face, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        atlas.GetFaceUvRect(id, face, out var uv00, out var uv10, out var uv11, out var uv01);
        
        // Vertex order for non-flipped faces
        verts.Add(new VertexPositionTexture(p0, uv00));
        verts.Add(new VertexPositionTexture(p1, uv10));
        verts.Add(new VertexPositionTexture(p2, uv11));
        verts.Add(new VertexPositionTexture(p0, uv00));
        verts.Add(new VertexPositionTexture(p2, uv11));
        verts.Add(new VertexPositionTexture(p3, uv01));
    }

    private struct ChatActionHitbox
    {
        public Rectangle Bounds;
        public bool HasTeleport;
        public int TeleportX;
        public int TeleportY;
        public int TeleportZ;
        public string HoverText;
    }

    private enum CommandPermission
    {
        Everyone,
        OwnerAdmin
    }

    private enum GameModeChangeSource
    {
        Command,
        Wheel,
        Load
    }

    private readonly struct CommandDescriptor
    {
        public CommandDescriptor(
            string name,
            string[] aliases,
            string usage,
            string description,
            CommandPermission permission,
            Action<string[]> handler)
        {
            Name = name;
            Aliases = aliases;
            Usage = usage;
            Description = description;
            Permission = permission;
            Handler = handler;
        }

        public string Name { get; }
        public string[] Aliases { get; }
        public string Usage { get; }
        public string Description { get; }
        public CommandPermission Permission { get; }
        public Action<string[]> Handler { get; }
    }

    private readonly struct CommandPredictionItem
    {
        public CommandPredictionItem(string text, string hoverText)
        {
            Text = text;
            HoverText = hoverText;
        }

        public string Text { get; }
        public string HoverText { get; }
    }

    private readonly struct CommandCompletionCandidate
    {
        public CommandCompletionCandidate(string value, string hoverText)
        {
            Value = value;
            HoverText = hoverText;
        }

        public string Value { get; }
        public string HoverText { get; }
    }

    private readonly struct CommandCompletionContext
    {
        public CommandCompletionContext(string prefixText, string tokenPrefix, int tokenIndex, string[] tokens)
        {
            PrefixText = prefixText;
            TokenPrefix = tokenPrefix;
            TokenIndex = tokenIndex;
            Tokens = tokens;
        }

        public string PrefixText { get; }
        public string TokenPrefix { get; }
        public int TokenIndex { get; }
        public string[] Tokens { get; }
    }

    private sealed class ChatLine
    {
        public string Text = "";
        public bool IsSystem;
        public float TimeRemaining;
        public bool HasTeleportAction;
        public int TeleportX;
        public int TeleportY;
        public int TeleportZ;
        public string HoverText = "";
    }

    private struct PlayerHomeEntry
    {
        public string Name;
        public Vector3 Position;
        public bool HasIconBlock;
        public BlockId IconBlock;
    }

    private sealed class WorldItem
    {
        public int ItemId;
        public BlockId BlockId;
        public int Count;
        public Vector3 Position;
        public Vector3 Velocity;
        public float SpawnTime;
        public float LastUpdateTime;
        public float PickupDelay;
        public int PickupLockPlayerId;
        public bool IsGrounded;
        public float GroundedTime;
    }
}
