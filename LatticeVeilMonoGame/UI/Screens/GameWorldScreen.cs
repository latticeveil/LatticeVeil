using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
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
    private readonly ILanSession? _lanSession;
    private readonly string _worldPath;
    private readonly string _metaPath;
    private GameSettings _settings = new();
    private DateTime _settingsStamp = DateTime.MinValue;

    private WorldMeta? _meta;
    private GameMode _gameMode = GameMode.Sandbox;
    private VoxelWorld? _world;
    private ChunkStreamingService? _streamingService;
    private WorldPregenerationService? _pregenerationService;
    private CubeNetAtlas? _atlas;
    private BlockModel? _blockModel;
    private BlockIconCache? _blockIconCache;
    private readonly ConcurrentDictionary<ChunkCoord, ChunkMesh> _chunkMeshes = new();
    private readonly List<ChunkCoord> _chunkOrder = new();
    private readonly HashSet<ChunkCoord> _activeChunks = new();
    private ChunkCoord _centerChunk;
    private int _activeRadiusChunks = 8;
    private const int MaxMeshBuildsPerFrame = 2;

    // Chunk streaming constants
    private const int MaxNewChunkRequestsPerFrame = 3;
    private const int MaxMeshBuildRequestsPerFrame = 2;
    private const int MaxApplyCompletedMeshesPerFrame = 3;
    private const int MaxApplyCompletedChunkLoadsPerFrame = 4;
    private const int MaxOutstandingChunkJobs = 64;
    private const int MaxOutstandingMeshJobs = 64;

    // Urgent remesh system for block edits
    private readonly Queue<ChunkCoord> _urgentMeshQueue = new();
    private const int MaxUrgentMeshesPerFrame = 2;
    private const float UrgentMeshTimeLimit = 2.0f; // Max 2ms per frame for urgent meshes
    private const int KeepRadiusBuffer = 2;
    private const int PrewarmRadius = 8; // Match render distance to prevent post-spawn loading

    // Streaming state
    private ChunkCoord _playerChunkCoord;
    private int _chunksRequestedThisFrame;
    private int _meshesRequestedThisFrame;
    private DateTime _lastStreamingStats = DateTime.UtcNow;
    private bool _spawnPrewarmComplete;
    private DateTime _spawnPrewarmStartTime = DateTime.UtcNow;
    
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
    
    // Reusable buffer to avoid allocations in QueueDirtyChunks
    private readonly List<VoxelChunkData> _chunkSnapshotBuffer = new();
    private readonly Queue<ChunkCoord> _meshQueue = new();
    private readonly HashSet<ChunkCoord> _meshQueued = new();
    private bool _loggedInvalidChunkMesh;
    private bool _loggedInvalidHandMesh;

    private BasicEffect? _effect;
    private BasicEffect? _lineEffect;
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

    public bool WantsMouseCapture => !_pauseMenuOpen && !_inventoryOpen && _spawnPrewarmComplete;
    public bool WorldReady => !_worldSyncInProgress && _hasLoadedWorld && AreSpawnChunksLoaded() && _spawnPrewarmComplete;

    private bool _pauseMenuOpen;
    private Texture2D? _pausePanel;
    private Rectangle _pauseRect;
    private Rectangle _pauseHeaderRect;
    private readonly Button _pauseResume;
    private readonly Button _pauseInvite;
    private readonly Button _pauseSettings;
    private readonly Button _pauseSaveExit;
    private EosClient? _eosClient;
    private bool _debugFaceOverlay;

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

    private static readonly float InteractRange = Scale.InteractionRange * Scale.BlockSize;
    private const float InteractCooldownSeconds = 0.15f;
    private float _interactCooldown;
    private readonly Queue<ChunkCoord> _priorityMeshQueue = new();
    private bool _worldSyncInProgress = true;
    private bool _hasLoadedWorld;
    private int _chunksReceived;
    private float _netSendAccumulator;

    private float _selectedNameTimer;
    private int _lastSelectedIndex = -1;
    private string _displayName = "";

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
        _activeRadiusChunks = Math.Clamp(_settings.RenderDistanceChunks, 4, 24);
        UpdateReticleSettings();
        _inventoryKey = GetKeybind("Inventory", Keys.E);
        _dropKey = GetKeybind("DropItem", Keys.Q);
        _giveKey = GetKeybind("GiveItem", Keys.F);
        _stackModifierKey = GetKeybind("Crouch", Keys.LeftShift);

        _pauseResume = new Button("RESUME", () => _pauseMenuOpen = false);
        _pauseInvite = new Button("SHARE CODE", OpenShareCode);
        _pauseSettings = new Button("SETTINGS", OpenSettings);
        _pauseSaveExit = new Button("SAVE & EXIT", SaveAndExit);
        _inventoryClose = new Button("CLOSE", CloseInventory);

        try
        {
            _pausePanel = _assets.LoadTexture("textures/menu/GUIS/Pause_GUI.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Pause menu asset load: {ex.Message}");
        }

        // Note: World load is deferred to the first Update() to allow "Loading..." screen to draw.
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        UpdatePauseMenuLayout();
        UpdateHotbarLayout();
        UpdateInventoryLayout();
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

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds; // Extract dt here
        _worldTimeSeconds = (float)gameTime.TotalGameTime.TotalSeconds;

        _fpsTimer += dt;
        _frameCount++;
        if (_fpsTimer >= 1.0f)
        {
            _fps = _frameCount / _fpsTimer;
            _frameCount = 0;
            _fpsTimer = 0;
        }

        if (input.IsNewKeyPress(Keys.F9))
            ExportAtlas();
        if (input.IsNewKeyPress(Keys.F3))
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
        _playerSaveTimer += dt;
        if (_playerSaveTimer >= 30.0f) // Save every 30 seconds
        {
            SavePlayerState();
            _playerSaveTimer = 0f;
            _log.Info("Player state auto-saved");
        }

        // Always update network layer, even if world sync is in progress
        UpdateNetwork(dt);

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
                _pauseInvite.Update(input);
                _pauseSettings.Update(input);
                _pauseSaveExit.Update(input);
            }
            return; // Skip all other game logic if sync is not complete
        }

        // PROCESS STREAMING RESULTS DURING LOADING
        if (!_spawnPrewarmComplete)
        {
            ProcessStreamingResults(aggressiveApply: true);
        }

        // CONTINUOUS MESH PROCESSING - Process mesh results to prevent graphical issues
        ProcessMeshJobsNonBlocking();
        
        // AGGRESSIVE: Process more chunks to prevent missing chunks
        if (!_spawnPrewarmComplete)
        {
            ProcessAdditionalChunks();
        }

        if (_inventoryOpen)
        {
            if (input.IsNewKeyPress(_inventoryKey) || input.IsNewKeyPress(Keys.Escape))
                CloseInventory();

            UpdateInventory(input);
            WarmBlockIcons();
            return;
        }

        if (!_pauseMenuOpen && input.IsNewKeyPress(_inventoryKey))
        {
            _inventoryOpen = true;
            return;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _pauseMenuOpen = !_pauseMenuOpen;
            return;
        }

        if (_pauseMenuOpen)
        {
            _pauseResume.Update(input);
            _pauseInvite.Update(input);
            _pauseSettings.Update(input);
            _pauseSaveExit.Update(input);
            WarmBlockIcons();
            return;
        }

        // Update spinner animation
        if (!_spawnPrewarmComplete)
        {
            _spinnerFrame = (_spinnerFrame + 1) % 8;
        }

        // BLOCK CONTROLS during spawn prewarm - wait for visual chunk loading
        if (!_spawnPrewarmComplete)
        {
            // Only allow pause menu during prewarm
            if (input.IsNewKeyPress(Keys.Escape))
            {
                _pauseMenuOpen = !_pauseMenuOpen;
            }
            if (_pauseMenuOpen)
            {
                _pauseResume.Update(input);
                _pauseInvite.Update(input);
                _pauseSettings.Update(input);
                _pauseSaveExit.Update(input);
            }
            return; // Skip all player/game controls until chunks are visually loaded
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
        HandleHotbarInput(input);
        UpdateSelectionTimer(dt);
        UpdateHandoffTarget(input);
        HandleDropAndGive(input);
        UpdateWorldItems();
        HandleBlockInteraction(gameTime, input);
        ProcessPriorityMeshQueue(); // High priority for block edits
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
        
        var sampler = _settings.QualityPreset switch
        {
            "LOW" => new SamplerState { Filter = TextureFilter.Point, MipMapLevelOfDetailBias = 2.0f, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp },
            "MEDIUM" => new SamplerState { Filter = TextureFilter.Point, MipMapLevelOfDetailBias = 1.0f, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp },
            "HIGH" => new SamplerState { Filter = TextureFilter.Point, MipMapLevelOfDetailBias = 0.5f, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp },
            "ULTRA" => new SamplerState { Filter = TextureFilter.Point, MipMapLevelOfDetailBias = 0.0f, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp },
            _ => SamplerState.PointClamp
        };
        device.SamplerStates[0] = sampler;
#if DEBUG
        _blockSamplerLabel = sampler.ToString() ?? "null";
#endif

        EnsureEffect(device);
        if (_effect == null)
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

        DrawWorldItems(device, view, proj);

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
        var mode = _meta?.GameMode ?? GameMode.Sandbox;
        _font.DrawString(sb, $"WORLD: {name}", new Vector2(_viewport.X + 20, _viewport.Y + 20), Color.White);
        _font.DrawString(sb, $"MODE: {mode.ToString().ToUpperInvariant()}", new Vector2(_viewport.X + 20, _viewport.Y + 20 + _font.LineHeight + 4), Color.White);
        _font.DrawString(sb, "WASD MOVE | SPACE JUMP | CTRL DOWN | DOUBLE TAP SPACE: FLY | ESC PAUSE", new Vector2(_viewport.X + 20, _viewport.Y + 20 + (_font.LineHeight + 4) * 2), Color.White);
        
        DrawHotbar(sb);
        DrawSelectedBlockName(sb);
        DrawPlayerList(sb);
#if DEBUG
        DrawDebugOverlay(sb);
#endif
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
        _gameMode = _meta.GameMode;
        _playerCollisionEnabled = _meta.PlayerCollision;

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
        
        // 1) Initialize chunk streaming service AFTER atlas is built
        _streamingService = new ChunkStreamingService(_world, _atlas, _log);
        _log.Info("Chunk streaming service initialized");
        
        // Initialize world pregeneration service
        _pregenerationService = new WorldPregenerationService(_world, _streamingService, _log);
        _log.Info("World pregeneration service initialized");
        
        InitBlockModel();
        
        _player.AllowFlying = _gameMode == GameMode.Sandbox;
        _inventory.SetMode(_gameMode);
        if (_gameMode == GameMode.Survival)
            _player.SetFlying(false);

        // Initialize performance optimizer for maximum GPU/CPU/RAM utilization
        AdvancedPerformanceOptimizer.Initialize();
        
        // RESTORE PLAYER POSITION FIRST - This determines where chunks need to be loaded
        RestoreOrCenterCamera();
        
        // AGGRESSIVE FIX: Generate solid ground immediately at player position
        GenerateSolidGroundAtPlayer();
        
        // CRITICAL: Force immediate chunk generation at player position BEFORE anything else
        ForceGeneratePlayerChunk();
        
        // Pre-load chunks around PLAYER'S SAVED POSITION (not just spawn)
        sw.Restart();
        PreloadChunksAroundPlayer();
        
        // Start automatic world pregeneration with OPTIMIZED area (non-blocking)
        if (_pregenerationService != null)
        {
            var optimalRadius = AdvancedPerformanceOptimizer.GetOptimalRenderDistance();
            _log.Info($"Starting OPTIMIZED world pregeneration for best experience...");
            _pregenerationService.StartPregeneration(optimalRadius); // Use hardware-optimal radius
            
            _log.Info($"World pregeneration started in background - {optimalRadius} chunk radius optimized for hardware");
        }
        sw.Stop();
        _log.Info($"World load: player position chunk pre-load {sw.ElapsedMilliseconds}ms");

        // CRITICAL: Wait for player's chunk to be FULLY generated and loaded
        WaitForPlayerChunkComplete();
        
        // OPTIMIZED: Force mesh generation without blocking to prevent freezes
        ForceGeneratePlayerChunkMeshOptimized();
        
        // AGGRESSIVE: Ensure all chunks around player are visible
        EnsureAllChunksVisible();
        
        // ULTRA AGGRESSIVE: Force load more chunks to prevent missing chunks
        ForceLoadAdditionalChunks();
        
        // RESEARCH-BASED: Implement spawn chunk system for guaranteed loading
        EnsureSpawnChunksLoaded();
        
        // RESEARCH-BASED: Implement force loading system like Minecraft's /forceload
        ForceLoadCriticalChunks();
        
        // AGGRESSIVE SAFETY: Ensure player has solid ground to stand on
        EnsureSolidGroundUnderPlayer();
        
        EnsurePlayerNotInsideSolid(forceLog: false);

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

    private void UpdateActiveChunks(bool force)
    {
        if (_world == null || _atlas == null || _streamingService == null)
            return;

        // Reset per-frame counters
        _chunksRequestedThisFrame = 0;
        _meshesRequestedThisFrame = 0;

        // A) Correct "load from player" logic - single source of truth
        var wx = (int)MathF.Floor(_player.Position.X);
        var wy = (int)MathF.Floor(_player.Position.Y);
        var wz = (int)MathF.Floor(_player.Position.Z);
        var center = VoxelWorld.WorldToChunk(wx, wy, wz, out _, out _, out _);
        
        // Update player chunk coord for debug
        _playerChunkCoord = center;
        
        // OPTIMIZATION: Only update if player moved to a new chunk OR forced
        if (!force && center.Equals(_centerChunk))
            return; // Player hasn't moved chunks, no need to update

        _centerChunk = center;
        _activeChunks.Clear();
        _chunkOrder.Clear();

        var renderRadius = Math.Max(1, _activeRadiusChunks);
        var keepRadius = renderRadius + KeepRadiusBuffer;
        var maxCy = _world.MaxChunkY;

        // B) PREDICTIVE CHUNK LOADING - Load chunks in direction of player movement
        Vector3 playerVelocity = _player.Velocity;
        Vector3 predictedPosition = _player.Position + playerVelocity * 2.0f; // Predict 2 seconds ahead
        var predictedChunk = VoxelWorld.WorldToChunk((int)predictedPosition.X, (int)predictedPosition.Y, (int)predictedPosition.Z, out _, out _, out _);

        // C) VIEW-BASED RENDERING - Calculate view frustum for culling
        var viewChunks = new List<ChunkCoord>();
        
        // Add chunks in player's view direction with higher priority
        for (var dx = -renderRadius; dx <= renderRadius; dx++)
        {
            for (var dz = -renderRadius; dz <= renderRadius; dz++)
            {
                var coord = new ChunkCoord(center.X + dx, center.Y, center.Z + dz);
                
                // Simple view frustum culling - only load chunks in front of player
                var toChunk = new Vector3(coord.X * VoxelChunkData.ChunkSizeX - _player.Position.X,
                                       0, 
                                       coord.Z * VoxelChunkData.ChunkSizeZ - _player.Position.Z);
                var distance = toChunk.Length();
                var angle = MathF.Acos(Vector3.Dot(toChunk, _player.Forward) / distance);
                
                // Load chunks within 120 degree view cone
                if (angle < MathF.PI * 2/3 || distance < renderRadius * VoxelChunkData.ChunkSizeX)
                {
                    viewChunks.Add(coord);
                }
            }
        }

        // D) PRIORITY SYSTEM - Spawn chunk > Predicted chunk > View chunks > Surrounding chunks
        var desiredChunks = new List<(ChunkCoord coord, int priority)>();
        
        // CRITICAL: Always ensure spawn chunk is loaded first (highest priority)
        var spawnPoint = GetWorldSpawnPoint();
        var spawnChunk = VoxelWorld.WorldToChunk((int)spawnPoint.X, 0, (int)spawnPoint.Y, out _, out _, out _);
        var surfaceSpawnChunk = new ChunkCoord(spawnChunk.X, spawnChunk.Y, spawnChunk.Z);
        
        // Add spawn chunk with highest priority
        for (var cy = 0; cy <= maxCy; cy++)
        {
            var coord = new ChunkCoord(surfaceSpawnChunk.X, cy, surfaceSpawnChunk.Z);
            desiredChunks.Add((coord, -10)); // Highest priority for spawn
        }
        
        // Add predicted movement chunk with high priority
        for (var cy = 0; cy <= maxCy; cy++)
        {
            var coord = new ChunkCoord(predictedChunk.X, cy, predictedChunk.Z);
            if (!desiredChunks.Contains((coord, -10))) // Avoid duplicate with spawn
            {
                desiredChunks.Add((coord, -5)); // High priority for predicted location
            }
        }
        
        // Add view chunks with medium priority
        foreach (var coord in viewChunks)
        {
            for (var cy = 0; cy <= maxCy; cy++)
            {
                var chunkCoord = new ChunkCoord(coord.X, cy, coord.Z);
                if (!desiredChunks.Any(d => d.coord.Equals(chunkCoord)))
                {
                    desiredChunks.Add((chunkCoord, 0)); // Medium priority for view chunks
                }
            }
        }
        
        // E) RING-BASED FALLBACK - Ensure complete coverage with surrounding chunks
        for (var ring = 0; ring <= renderRadius; ring++)
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
                        var coord = new ChunkCoord(center.X + dx, cy, center.Z + dz);
                        if (!desiredChunks.Any(d => d.coord.Equals(coord)))
                        {
                            desiredChunks.Add((coord, ring + 1)); // Lower priority for fallback
                        }
                    }
                }
            }
        }

        // Sort by priority (lower number = higher priority)
        desiredChunks.Sort((a, b) => a.priority.CompareTo(b.priority));

        // F) NO MISSING CHUNKS GUARANTEE - Ensure all critical chunks are loaded
        var criticalChunks = desiredChunks.Where(c => c.priority <= 0).ToList();
        var missingCriticalChunks = criticalChunks.Count(c => 
            !_world.TryGetChunk(c.coord, out var chunk) || chunk == null ||
            !_chunkMeshes.ContainsKey(c.coord));
        
        if (missingCriticalChunks > 0)
        {
            _log.Debug($"Critical chunks missing: {missingCriticalChunks}, prioritizing loading");
            // Focus only on critical chunks if any are missing
            desiredChunks = criticalChunks;
        }

        // Get current queue sizes to enforce caps
        var (loadQueueSize, meshQueueSize, _) = _streamingService.GetQueueSizes();
        var canRequestChunks = loadQueueSize < MaxOutstandingChunkJobs;
        var canRequestMeshes = meshQueueSize < MaxOutstandingMeshJobs;

        // G) OPTIMIZED scheduling - only request chunks that are actually missing
        foreach (var (coord, priority) in desiredChunks)
        {
            // Enqueue load jobs for missing chunks (with budget)
            if (!_world.TryGetChunk(coord, out _) && canRequestChunks && 
                _chunksRequestedThisFrame < MaxNewChunkRequestsPerFrame)
            {
                _streamingService.EnqueueLoadJob(coord, priority);
                _chunksRequestedThisFrame++;
                
                // Update queue size check
                if (_chunksRequestedThisFrame >= MaxNewChunkRequestsPerFrame)
                {
                    var (newLoadSize, _, _) = _streamingService.GetQueueSizes();
                    canRequestChunks = newLoadSize < MaxOutstandingChunkJobs;
                }
            }

            // Enqueue mesh jobs for loaded chunks that need meshing
            if (_world.TryGetChunk(coord, out var chunk) && chunk != null && canRequestMeshes &&
                _meshesRequestedThisFrame < MaxMeshBuildRequestsPerFrame)
            {
                if (!_chunkMeshes.ContainsKey(coord) || chunk.IsDirty)
                {
                    _streamingService.EnqueueMeshJob(coord, chunk, priority);
                    _meshesRequestedThisFrame++;
                    
                    // H) NEIGHBOR-AWARE MESHING - schedule mesh for neighbors too
                    if (priority <= 0) // Only for critical chunks
                    {
                        ScheduleNeighborMeshes(coord, priority);
                    }
                    
                    // Update queue size check
                    if (_meshesRequestedThisFrame >= MaxMeshBuildRequestsPerFrame)
                    {
                        var (_, newMeshSize, _) = _streamingService.GetQueueSizes();
                        canRequestMeshes = newMeshSize < MaxOutstandingMeshJobs;
                    }
                }
            }
            
            if (!_activeChunks.Contains(coord))
            {
                _activeChunks.Add(coord);
                _chunkOrder.Add(coord);
            }
        }

        TrimFarChunks(keepRadius);
    }

    private void ScheduleNeighborMeshes(ChunkCoord center, int priority)
    {
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
                _meshesRequestedThisFrame < MaxMeshBuildRequestsPerFrame)
            {
                if (!_chunkMeshes.ContainsKey(neighbor) || chunk.IsDirty)
                {
                    _streamingService.EnqueueMeshJob(neighbor, chunk, priority + 1);
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
        
        var newRadius = Math.Clamp(requestedRadius, 4, 24);
        
        // C1) Log warnings for high render distances
        if (requestedRadius > 16)
        {
            _log.Warn($"High render distance requested ({requestedRadius}). Consider using 16 or lower for better performance. Values >16 are considered 'Advanced'.");
        }
        else if (requestedRadius > 12)
        {
            _log.Info($"Render distance set to {requestedRadius} (moderate-high range)");
        }
        var newQuality = _settings.QualityPreset;
        _inventoryKey = GetKeybind("Inventory", Keys.E);
        _dropKey = GetKeybind("DropItem", Keys.Q);
        _giveKey = GetKeybind("GiveItem", Keys.F);
        _stackModifierKey = GetKeybind("Crouch", Keys.LeftShift);
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

        // A5) THROTTLE dirty chunk processing to prevent starving urgent edits
        const int MaxDirtyChunksPerFrame = 8; // Limit to prevent backlog flooding
        var dirtyChunksProcessed = 0;
        
        // 1) Use reusable buffer to avoid allocations and enumeration modification crash
        _chunkSnapshotBuffer.Clear();
        _chunkSnapshotBuffer.AddRange(_world.AllChunks());
        
        // A5a) Prioritize near-player chunks (ring <= 2) for dirty processing
        var nearPlayerChunks = new List<VoxelChunkData>();
        var farChunks = new List<VoxelChunkData>();
        
        foreach (var chunk in _chunkSnapshotBuffer)
        {
            if (!chunk.IsDirty)
                continue;
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(chunk.Coord))
                continue;
                
            // Calculate distance from player chunk
            var dx = Math.Abs(chunk.Coord.X - _centerChunk.X);
            var dz = Math.Abs(chunk.Coord.Z - _centerChunk.Z);
            var ring = Math.Max(dx, dz);
            
            if (ring <= 2)
                nearPlayerChunks.Add(chunk);
            else
                farChunks.Add(chunk);
        }
        
        // Process near chunks first, then far chunks (up to limit)
        var allDirtyChunks = new List<VoxelChunkData>();
        allDirtyChunks.AddRange(nearPlayerChunks);
        allDirtyChunks.AddRange(farChunks);
        
        foreach (var chunk in allDirtyChunks)
        {
            if (dirtyChunksProcessed >= MaxDirtyChunksPerFrame)
                break;
                
            QueueMeshBuild(chunk.Coord);
            dirtyChunksProcessed++;
        }
    }

    private void QueueMeshBuild(ChunkCoord coord)
    {
        if (_meshQueued.Add(coord))
            _meshQueue.Enqueue(coord);
    }

    private void ProcessPriorityMeshQueue()
    {
        if (_world == null || _atlas == null || _blockModel == null)
            return;
        
        // Process up to 3 priority meshes per frame (higher than normal queue)
        for (var i = 0; i < 3 && _priorityMeshQueue.Count > 0; i++)
        {
            var coord = _priorityMeshQueue.Dequeue();
            if (_activeChunks.Contains(coord))
            {
                if (!_world.TryGetChunk(coord, out var chunk) || chunk == null) continue;
                RebuildChunkMeshImmediate(coord, chunk);
            }
        }
    }

    private void RebuildChunkMeshImmediate(ChunkCoord coord, VoxelChunkData chunk)
    {
        // Use existing mesh building logic
        QueueMeshBuild(coord);
    }

    private ChunkMesh? BuildUrgentChunkMesh(ChunkCoord coord, VoxelChunkData chunk)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // A3) FAST mesh path for urgent updates
            var mesh = VoxelMesherGreedy.BuildChunkMesh(_world, chunk, _atlas, _log);
            
            if (!ValidateMesh(mesh))
            {
                _log.Warn($"Urgent mesh validation failed for {coord}, falling back to null");
                return null;
            }
            
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds > UrgentMeshTimeLimit)
            {
                _log.Debug($"Urgent mesh for {coord} took {stopwatch.ElapsedMilliseconds}ms (over limit)");
            }
            
            return mesh;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to build urgent mesh for {coord}: {ex.Message}");
            return null;
        }
    }

    private void ProcessStreamingResults(bool aggressiveApply = false)
    {
        if (_streamingService == null || _world == null)
            return;

        // Check if streaming service is stuck and force recovery
        if (_streamingService.IsStuck(TimeSpan.FromSeconds(10)))
        {
            _log.Warn("Chunk streaming service appears stuck, forcing recovery...");
            
            // Force completion of prewarm if stuck during loading
            if (!_spawnPrewarmComplete)
            {
                _log.Warn("Forcing prewarm completion due to stuck streaming service");
                _spawnPrewarmComplete = true;
                _visibilitySafe = true;
            }
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
                _chunkMeshes[result.Coord] = result.Mesh;
                if (!_chunkOrder.Contains(result.Coord))
                    _chunkOrder.Add(result.Coord);
                
                _completedMeshesAppliedThisFrame++;
                _log.Debug($"Applied placeholder mesh for chunk {result.Coord}");
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
            var (loadQueue, meshQueue, saveQueue) = _streamingService.GetQueueSizes();
            _log.Info($"Streaming: Player={_playerChunkCoord}, Loaded={_world.AllChunks().Count()}, " +
                     $"Queues(L/M/S)={loadQueue}/{meshQueue}/{saveQueue}, " +
                     $"Applied(L/M)={_completedLoadsAppliedThisFrame}/{_completedMeshesAppliedThisFrame}");
            _lastStreamingStats = now;
        }
    }

    private void ProcessMeshBuildQueue()
    {
        // Legacy mesh queue processing - mostly replaced by streaming service
        // but kept for compatibility with priority mesh queue
        if (_world == null || _atlas == null)
            return;

        var budget = Math.Max(1, MaxMeshBuildsPerFrame / 2); // Reduced budget since streaming handles most
        while (budget > 0 && _meshQueue.Count > 0)
        {
            var coord = _meshQueue.Dequeue();
            _meshQueued.Remove(coord);
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(coord))
                continue;
            
            if (!_world.TryGetChunk(coord, out var chunk) || chunk == null)
                continue;

            var mesh = VoxelMesherGreedy.BuildChunkMesh(_world, chunk, _atlas, _log);
            if (!ValidateMesh(mesh))
            {
                if (!_loggedInvalidChunkMesh)
                {
                    _log.Error($"Invalid chunk mesh for {coord}. Skipping draw to avoid artifacts.");
                    _loggedInvalidChunkMesh = true;
                }
                chunk.IsDirty = false;
                budget--;
                continue;
            }

            _chunkMeshes[coord] = mesh;
            if (!_chunkOrder.Contains(coord))
                _chunkOrder.Add(coord);
            chunk.IsDirty = false;
            budget--;
        }
    }

    private static bool ValidateMesh(ChunkMesh mesh)
    {
        if (mesh.OpaqueVertices.Length % 3 != 0 || mesh.TransparentVertices.Length % 3 != 0)
            return false;

        return VerticesFinite(mesh.OpaqueVertices) && VerticesFinite(mesh.TransparentVertices);
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
                if (id == BlockIds.Nullblock && _gameMode == GameMode.Survival)
                {
                    _interactCooldown = InteractCooldownSeconds;
                    return;
                }

                _world.SetBlock(hit.X, hit.Y, hit.Z, BlockIds.Air);
                _lanSession?.SendBlockSet(hit.X, hit.Y, hit.Z, BlockIds.Air);
                
                // A) URGENT REMESH for block edits - immediate visual feedback
                var editedChunk = VoxelWorld.WorldToChunk(hit.X, hit.Y, hit.Z, out _, out _, out _);
                
                // A1) Immediate mesh build for edited chunk (fast path)
                if (_world.TryGetChunk(editedChunk, out var editedChunkData) && editedChunkData != null)
                {
                    var urgentMesh = BuildUrgentChunkMesh(editedChunk, editedChunkData);
                    if (urgentMesh != null)
                    {
                        _chunkMeshes[editedChunk] = urgentMesh;
                        if (!_chunkOrder.Contains(editedChunk))
                            _chunkOrder.Add(editedChunk);
                        editedChunkData.IsDirty = false;
                    }
                }
                
                // A2) Urgent remesh for neighbors (only if they exist and are active)
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var neighborCoord = new ChunkCoord(editedChunk.X + dx, editedChunk.Y, editedChunk.Z + dz);
                        if (_activeChunks.Contains(neighborCoord) && _world.TryGetChunk(neighborCoord, out var neighborChunk) && neighborChunk != null)
                        {
                            var urgentNeighborMesh = BuildUrgentChunkMesh(neighborCoord, neighborChunk);
                            if (urgentNeighborMesh != null)
                            {
                                _chunkMeshes[neighborCoord] = urgentNeighborMesh;
                                if (!_chunkOrder.Contains(neighborCoord))
                                    _chunkOrder.Add(neighborCoord);
                                neighborChunk.IsDirty = false;
                            }
                        }
                    }
                }
                
                if (_gameMode == GameMode.Survival)
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
            var sandbox = _gameMode == GameMode.Sandbox;
            if (id == BlockIds.Air && selected != BlockId.Air && (sandbox || _inventory.SelectedCount > 0))
            {
                if (WouldBlockIntersectAnyPlayer(hit.PrevX, hit.PrevY, hit.PrevZ))
                {
                    _interactCooldown = InteractCooldownSeconds;
                    return;
                }
                _world.SetBlock(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
                _lanSession?.SendBlockSet(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
                
                // A) URGENT REMESH for block placement - immediate visual feedback
                var editedChunk = VoxelWorld.WorldToChunk(hit.PrevX, hit.PrevY, hit.PrevZ, out _, out _, out _);
                
                // A1) Immediate mesh build for edited chunk (fast path)
                if (_world.TryGetChunk(editedChunk, out var editedChunkData) && editedChunkData != null)
                {
                    var urgentMesh = BuildUrgentChunkMesh(editedChunk, editedChunkData);
                    if (urgentMesh != null)
                    {
                        _chunkMeshes[editedChunk] = urgentMesh;
                        if (!_chunkOrder.Contains(editedChunk))
                            _chunkOrder.Add(editedChunk);
                        editedChunkData.IsDirty = false;
                    }
                }
                
                // A2) Urgent remesh for neighbors (only if they exist and are active)
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var neighborCoord = new ChunkCoord(editedChunk.X + dx, editedChunk.Y, editedChunk.Z + dz);
                        if (_activeChunks.Contains(neighborCoord) && _world.TryGetChunk(neighborCoord, out var neighborChunk) && neighborChunk != null)
                        {
                            var urgentNeighborMesh = BuildUrgentChunkMesh(neighborCoord, neighborChunk);
                            if (urgentNeighborMesh != null)
                            {
                                _chunkMeshes[neighborCoord] = urgentNeighborMesh;
                                if (!_chunkOrder.Contains(neighborCoord))
                                    _chunkOrder.Add(neighborCoord);
                                neighborChunk.IsDirty = false;
                            }
                        }
                    }
                }
                
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
        _handoffTargetId = -1;
        _handoffTargetName = "";
        _handoffPromptVisible = false;
        _handoffFullStack = IsStackModifierDown(input);

        if (_pauseMenuOpen || _inventoryOpen || _lanSession == null || _gameMode != GameMode.Survival)
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
        if (_pauseMenuOpen || _inventoryOpen || _gameMode != GameMode.Survival)
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
            if (_inventory.Mode != GameMode.Sandbox)
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
        if (_inventory.Mode == GameMode.Sandbox)
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

        if (_gameMode == GameMode.Sandbox)
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
            lines.Add($"CHUNK: {_playerChunkCoord} | LOADED: {_world.AllChunks().Count()}");
            lines.Add($"QUEUES: L:{loadQueue} M:{meshQueue} S:{saveQueue}");
            lines.Add($"BUDGET: REQ({_chunksRequestedThisFrame}/{MaxNewChunkRequestsPerFrame}) MESH({_meshesRequestedThisFrame}/{MaxMeshBuildRequestsPerFrame})");
            lines.Add($"PREWARM: {(_spawnPrewarmComplete ? "DONE" : "LOADING")}");
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
            var biome = BiomeSystem.GetBiome(playerPos.X, playerPos.Z);
            var biomeChars = BiomeSystem.GetBiomeCharacteristics(playerPos.X, playerPos.Z);
            lines.Add($"BIOME: {biome} | Height:{biomeChars.BaseHeight}+/-{biomeChars.HeightVariation} | Trees:{(BiomeSystem.ShouldHaveTrees(playerPos.X, playerPos.Z) ? "Yes" : "No")}");
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
        _pauseInvite.Visible = showInvite;
        var buttonCount = showInvite ? 4 : 3;
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

        _pauseResume.Bounds = new Rectangle(centerX, startY, buttonW, buttonH);
        if (showInvite)
        {
            _pauseInvite.Bounds = new Rectangle(centerX, startY + buttonH + gap, buttonW, buttonH);
            _pauseSettings.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * 2, buttonW, buttonH);
            _pauseSaveExit.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * 3, buttonW, buttonH);
        }
        else
        {
            _pauseSettings.Bounds = new Rectangle(centerX, startY + buttonH + gap, buttonW, buttonH);
            _pauseSaveExit.Bounds = new Rectangle(centerX, startY + (buttonH + gap) * 2, buttonW, buttonH);
        }
    }

    private void DrawPauseMenu(SpriteBatch sb)
    {
        if (_viewport != UiLayout.Viewport)
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
        _pauseInvite.Draw(sb, _pixel, _font);
        _pauseSettings.Draw(sb, _pixel, _font);
        _pauseSaveExit.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void OpenSettings()
    {
        _pauseMenuOpen = false;
        _menus.Push(new OptionsScreen(_menus, _assets, _font, _pixel, _log, _graphics), _viewport);
    }




        private void OpenShareCode()
    {
        _pauseMenuOpen = false;

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
        SavePlayerState();
        _world?.SaveModifiedChunks();
        _streamingService?.Dispose();
        _streamingService = null;
        _lanSession?.Dispose();
        _blockIconCache?.Dispose();
        _blockIconCache = null;
    }

    private void SaveAndExit()
    {
        _pauseMenuOpen = false;
        _log.Info("Pause menu: Save & Exit requested.");
        SavePlayerState(); // Ensure inventory is saved when exiting via pause menu
        _menus.Pop();
    }

    private EosClient? EnsureEosClient()
    {
        if (_eosClient != null)
            return _eosClient;

        _eosClient = EosClientProvider.GetOrCreate(_log, "device", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("GameWorldScreen: EOS client not available.");
        return _eosClient;
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
            Username = username
        };

        if (_meta == null)
            return state;

        // Use dedicated spawn point instead of world center
        var spawnPoint = GetWorldSpawnPoint();
        
        // Find safe spawn height by scanning terrain at spawn point
        var safeHeight = FindSafeSpawnHeight(spawnPoint.X, spawnPoint.Y);
        var height = Math.Max(safeHeight + 2f, 6f); // Spawn 2 blocks above ground
        
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
    /// RESEARCH-BASED: Ensure spawn chunks are loaded like Minecraft's spawn chunk system
    /// Spawn chunks always tick even without players nearby
    /// </summary>
    private void EnsureSpawnChunksLoaded()
    {
        if (_world == null || _streamingService == null)
            return;

        _log.Info("RESEARCH-BASED: Ensuring spawn chunks are loaded like Minecraft");
        
        var spawnPoint = GetWorldSpawnPoint();
        var spawnChunk = VoxelWorld.WorldToChunk((int)spawnPoint.X, 0, (int)spawnPoint.Y, out _, out _, out _);
        var surfaceSpawnChunk = new ChunkCoord(spawnChunk.X, spawnChunk.Y, spawnChunk.Z);
        
        var maxCy = _world.MaxChunkY;
        var spawnChunksLoaded = 0;
        
        // Load spawn chunks in a 3x3 area (like Minecraft's spawn chunk system)
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
    /// RESEARCH-BASED: Force load critical chunks like Minecraft's /forceload command
    /// This keeps critical chunks loaded artificially to prevent missing chunks
    /// </summary>
    private void ForceLoadCriticalChunks()
    {
        if (_world == null || _streamingService == null || _player == null)
            return;

        _log.Info("RESEARCH-BASED: Force loading critical chunks like Minecraft's /forceload");
        
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
                _log.Debug($"Added mesh for {result.Coord}");
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
                    _log.Debug($"Force loading missing chunk {coord}");
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
                _log.Debug($"Added mesh for {result.Coord}");
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
        
        // Generate chunk IMMEDIATELY with highest priority
        var chunkData = new VoxelChunkData(playerChunk);
        OptimizedWorldGenerator.GenerateChunk(_world.Meta, playerChunk, chunkData);
        
        // Add chunk to world immediately
        _world.AddChunkDirect(playerChunk, chunkData);
        
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
        try
        {
            var emergencyChunk = new VoxelChunkData(playerChunk);
            OptimizedWorldGenerator.GenerateChunk(_world.Meta, playerChunk, emergencyChunk);
            _world.AddChunkDirect(playerChunk, emergencyChunk);
            _log.Info($"EMERGENCY: Generated fallback chunk for player at {playerChunk}");
        }
        catch (Exception ex)
        {
            _log.Error($"EMERGENCY chunk generation failed: {ex.Message}");
        }
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
        // Never show loading screen if spawn prewarm is complete
        if (_spawnPrewarmComplete)
            return false;
            
        // Only show during initial world loading
        return !_hasLoadedWorld || _worldSyncInProgress || !AreSpawnChunksLoaded();
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
                    (mesh.OpaqueVertices.Length == 0 && mesh.TransparentVertices.Length == 0))
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
                    AddFallbackCubeFace(opaque, worldX, worldY, worldZ, bs, blockId);
                }
            }
        }

        return new ChunkMesh(coord, opaque.ToArray(), transparent.ToArray(), 
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
            _log.Error($"VISIBILITY ASSERT FAILED: Player at {playerPos} - Solid ground: {hasSolidGround}, Too high: {isTooHigh}");
            _log.Error("Forcing re-enter Preparing World and rebuilding safety meshes");
            
            // Force re-enter prewarm state
            _spawnPrewarmComplete = false;
            _visibilitySafe = false;
            _firstGameplayFrame = false;
            _visibilityAssertLogged = true;
            
            // Rebuild safety meshes
            EnsureFallbackMeshes();
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
        _player.Position = new Vector3(state.PosX, state.PosY, state.PosZ);
        _player.Yaw = state.Yaw;
        _player.Pitch = state.Pitch;
        _player.Velocity = Vector3.Zero;
        
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

    private void SavePlayerState()
    {
        try
        {
            var username = _profile.GetDisplayUsername();
            var state = new PlayerWorldState
            {
                Username = username,
                PosX = _player.Position.X,
                PosY = _player.Position.Y,
                PosZ = _player.Position.Z,
                Yaw = _player.Yaw,
                Pitch = _player.Pitch,
                SelectedIndex = _inventory.SelectedIndex,
                Hotbar = _inventory.GetHotbarData()
            };
            state.Save(_worldPath, _log);
            _log.Info($"Player state saved: position ({state.PosX:F1}, {state.PosY:F1}, {state.PosZ:F1}), selected slot {state.SelectedIndex}, hotbar items {_inventory.GetHotbarData().Count(x => x.Id != BlockIds.Air)}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save player state: {ex.Message}");
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
