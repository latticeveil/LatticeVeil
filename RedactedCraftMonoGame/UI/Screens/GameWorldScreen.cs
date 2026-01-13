using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RedactedCraftMonoGame.Core;
using RedactedCraftMonoGame.Online.Eos;
using RedactedCraftMonoGame.Online.Lan;
using RedactedCraftMonoGame.UI;

namespace RedactedCraftMonoGame.UI.Screens;

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
    private CubeNetAtlas? _atlas;
    private BlockModel? _blockModel;
    private BlockIconCache? _blockIconCache;
    private readonly Dictionary<ChunkCoord, ChunkMesh> _chunkMeshes = new();
    private readonly List<ChunkCoord> _chunkOrder = new();
    private readonly HashSet<ChunkCoord> _activeChunks = new();
    private ChunkCoord _centerChunk;
    private int _activeRadiusChunks = 8;
    private const int MaxMeshBuildsPerFrame = 2;
    private readonly Queue<ChunkCoord> _meshQueue = new();
    private readonly HashSet<ChunkCoord> _meshQueued = new();
    private bool _loggedInvalidChunkMesh;
    private bool _loggedInvalidHandMesh;

    private BasicEffect? _effect;
    private Rectangle _viewport;

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
    private readonly Button _inventoryClose;
    private Point _inventoryMousePos;
    private InventorySlotGroup _heldFrom = InventorySlotGroup.None;
    private int _heldIndex = -1;
    private FirstPersonHandRenderer? _handRenderer;
    private PlayerModel? _playerModel;
    private readonly Dictionary<int, PlayerModel> _remotePlayers = new();
    private readonly Dictionary<int, string> _playerNames = new();

    public bool WantsMouseCapture => !_pauseMenuOpen && !_inventoryOpen;

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

    private float _fpsTimer;
    private int _frameCount;
    private float _fps;

    private static readonly float InteractRange = Scale.InteractionRange * Scale.BlockSize;
    private const float InteractCooldownSeconds = 0.15f;
    private float _interactCooldown;
    private double _netSendAccumulator;
    private bool _worldSyncInProgress = true;
    private bool _hasLoadedWorld;
    private int _chunksReceived;

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
        _inventoryKey = GetKeybind("Inventory", Keys.E);

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

        // Always update network layer, even if world sync is in progress
        UpdateNetwork(dt);

        if (_worldSyncInProgress)
        {
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

#if DEBUG
        if (input.IsNewKeyPress(Keys.F10))
        {
            EnsurePlayerNotInsideSolid(forceLog: true);
            _player.SetFlying(true);
        }
#endif

        UpdatePlayer(gameTime, dt, input);
        UpdateActiveChunks(force: false);
        HandleHotbarInput(input);
        UpdateSelectionTimer(dt);
        HandleBlockInteraction(gameTime, input);
        QueueDirtyChunks();
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

        if (!_hasLoadedWorld || _worldSyncInProgress)
        {
            // Draw loading/sync screen
            sb.GraphicsDevice.Clear(new Color(20, 20, 20));
            sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

            var center = new Vector2(_viewport.Width / 2f, _viewport.Height / 2f);
            var title = !_hasLoadedWorld ? "LOADING WORLD..." : "SYNCHRONIZING WORLD DATA...";
            var titleSize = _font.MeasureString(title);
            _font.DrawString(sb, title, new Vector2(center.X - titleSize.X / 2f, center.Y - 40), Color.White);

            // Draw loading bar
            var barWidth = Math.Min(400, _viewport.Width - 60);
            var barHeight = 24;
            var barRect = new Rectangle((int)center.X - barWidth / 2, (int)center.Y, barWidth, barHeight);
            
            sb.Draw(_pixel, barRect, new Color(40, 40, 40));
            DrawBorder(sb, barRect, Color.Gray);

            // Indeterminate throbber for now, or fake progress
            var time = (float)DateTime.Now.TimeOfDay.TotalSeconds;
            var throbberPos = (float)(Math.Sin(time * 3f) * 0.5f + 0.5f);
            var fillW = (int)(barWidth * 0.3f);
            var fillX = barRect.X + (int)((barWidth - fillW) * throbberPos);
            sb.Draw(_pixel, new Rectangle(fillX, barRect.Y + 2, fillW, barRect.Height - 4), new Color(70, 160, 90));

            if (_worldSyncInProgress)
            {
                var status = $"{_chunksReceived} chunks received";
                var statusSize = _font.MeasureString(status);
                _font.DrawString(sb, status, new Vector2(center.X - statusSize.X / 2f, barRect.Bottom + 12), Color.LightGray);
            }

            sb.End();

            // Optionally, draw the pause menu if it's open
            if (_pauseMenuOpen)
                DrawPauseMenu(sb);
            return;
        }

        if (_atlas == null)
            return;

        var device = sb.GraphicsDevice;
        var clearColor = SkyColor;
#if DEBUG
        if (!_debugSkyClear)
            clearColor = Color.Black;
#endif
        device.Clear(clearColor);
        device.DepthStencilState = DepthStencilState.Default;
#if DEBUG
        device.RasterizerState = _debugWireframe ? WireframeState : RasterizerState.CullNone;
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
        _effect.FogStart = MathF.Max(4f, (_activeRadiusChunks - 2) * chunkWorld);
        _effect.FogEnd = MathF.Max(_effect.FogStart + 4f, (_activeRadiusChunks - 0.5f) * chunkWorld);
        _effect.View = view;
        _effect.Projection = proj;

        var frustum = new BoundingFrustum(view * proj);

#if DEBUG
        _debugWorldAtlasBound = _effect.Texture != null;
#endif
        foreach (var coord in _chunkOrder)
        {
            if (!_chunkMeshes.TryGetValue(coord, out var mesh))
                continue;
            if (!frustum.Intersects(mesh.Bounds))
                continue;

            var verts = mesh.OpaqueVertices;
            if (verts.Length == 0)
                continue;

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
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
        foreach (var coord in _chunkOrder)
        {
            if (!_chunkMeshes.TryGetValue(coord, out var mesh))
                continue;
            if (!frustum.Intersects(mesh.Bounds))
                continue;

            var verts = mesh.TransparentVertices;
            if (verts.Length == 0)
                continue;

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
            }
        }

        var drawHands = !_pauseMenuOpen;
#if DEBUG
        drawHands = drawHands && !_debugDisableHands;
        _debugOverlayAtlasBound = drawHands && _atlas?.Texture != null;
#endif
        if (drawHands)
            DrawFirstPersonHands(device, view, proj);

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
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
        _player.AllowFlying = _gameMode == GameMode.Sandbox;
        _inventory.SetMode(_gameMode);
        if (_gameMode == GameMode.Survival)
            _player.SetFlying(false);

        sw.Restart();
        _atlas = CubeNetAtlas.Build(_assets, _log, _settings.QualityPreset);
        sw.Stop();
        _log.Info($"World load: CubeNetAtlas.Build {sw.ElapsedMilliseconds}ms");
        InitBlockModel();

        RestoreOrCenterCamera();
        EnsurePlayerNotInsideSolid(forceLog: false);

        sw.Restart();
        UpdateActiveChunks(force: true);
        sw.Stop();
        _log.Info($"World load: initial active chunk scan {sw.ElapsedMilliseconds}ms");

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
        if (_world == null || _atlas == null)
            return;

        var wx = (int)MathF.Floor(_player.Position.X);
        var wy = (int)MathF.Floor(_player.Position.Y);
        var wz = (int)MathF.Floor(_player.Position.Z);
        var center = VoxelWorld.WorldToChunk(wx, wy, wz, out _, out _, out _);
        if (!force && center.Equals(_centerChunk))
            return;

        _centerChunk = center;
        _activeChunks.Clear();
        _chunkOrder.Clear();

        var radius = Math.Max(1, _activeRadiusChunks);
        var radiusSq = radius * radius;
        var maxCy = _world.MaxChunkY;
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz > radiusSq)
                    continue;

                for (var cy = 0; cy <= maxCy; cy++)
                {
                    var coord = new ChunkCoord(center.X + dx, cy, center.Z + dz);
                    _activeChunks.Add(coord);
                }
            }
        }

        foreach (var coord in _activeChunks)
        {
            // Avoid generating/loading all chunks synchronously on world entry.
            // Chunks are created lazily during the per-frame mesh build budget.
            var hasChunk = _world.TryGetChunk(coord, out var chunk) && chunk != null;
            if (!_chunkMeshes.ContainsKey(coord) || !hasChunk || chunk!.IsDirty)
                QueueMeshBuild(coord);
            _chunkOrder.Add(coord);
        }

        TrimFarChunks(radius + 2);
    }

    private void RefreshSettingsIfChanged()
    {
        var stamp = GetSettingsStamp();
        if (stamp == _settingsStamp)
            return;

        var oldQuality = _settings.QualityPreset;
        _settingsStamp = stamp;
        _settings = GameSettings.LoadOrCreate(_log);
        var newRadius = Math.Clamp(_settings.RenderDistanceChunks, 4, 24);
        var newQuality = _settings.QualityPreset;
        _inventoryKey = GetKeybind("Inventory", Keys.E);

        if (newQuality != oldQuality)
        {
            _log.Info($"Quality changed: {oldQuality} -> {newQuality}. Rebuilding atlas...");
            _atlas = CubeNetAtlas.Build(_assets, _log, newQuality);
            _world?.MarkAllChunksDirty();
        }

        if (newRadius != _activeRadiusChunks)
        {
            _activeRadiusChunks = newRadius;
            UpdateActiveChunks(force: true);
            _log.Info($"Render distance changed: {_activeRadiusChunks} chunks");
        }
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

        foreach (var chunk in _world.AllChunks())
        {
            if (!chunk.IsDirty)
                continue;
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(chunk.Coord))
                continue;
            QueueMeshBuild(chunk.Coord);
        }
    }

    private void QueueMeshBuild(ChunkCoord coord)
    {
        if (_meshQueued.Add(coord))
            _meshQueue.Enqueue(coord);
    }

    private void ProcessMeshBuildQueue()
    {
        if (_world == null || _atlas == null)
            return;

        var budget = MaxMeshBuildsPerFrame;
        while (budget > 0 && _meshQueue.Count > 0)
        {
            var coord = _meshQueue.Dequeue();
            _meshQueued.Remove(coord);
            if (_activeChunks.Count > 0 && !_activeChunks.Contains(coord))
                continue;
            // Create/generate chunks lazily here (per-frame budget) instead of in UpdateActiveChunks.
            var chunk = _world.GetOrCreateChunk(coord);

            var mesh = VoxelMesherGreedy.BuildChunkMesh(_world, chunk, _atlas);
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
        if (_world == null)
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

        _world.UnloadChunks(keep);

        var toRemove = new List<ChunkCoord>();
        foreach (var coord in _chunkMeshes.Keys)
        {
            if (!keep.Contains(coord))
                toRemove.Add(coord);
        }

        foreach (var coord in toRemove)
        {
            _chunkMeshes.Remove(coord);
            _chunkOrder.Remove(coord);
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
                _world.SetBlock(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
                _lanSession?.SendBlockSet(hit.PrevX, hit.PrevY, hit.PrevZ, (byte)selected);
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
                _inventory.Add(_inventoryHeld.Id, _inventoryHeld.Count);

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
        _handRenderer.Draw(view, proj, _player.Position + _player.HeadOffset, forward, up, _atlas, _inventory.SelectedId, _blockModel);
        device.DepthStencilState = DepthStencilState.Default;
        device.RasterizerState = prevRaster;
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
        _lanSession?.Dispose();
        _blockIconCache?.Dispose();
        _blockIconCache = null;
    }

    private void SaveAndExit()
    {
        _pauseMenuOpen = false;
        _log.Info("Pause menu: Save & Exit requested.");
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

        var centerX = _meta.Size.Width / 2f;
        var centerZ = _meta.Size.Depth / 2f;
        var height = Math.Max(6f, _meta.Size.Height + 4f);
        var distance = Math.Max(10f, _meta.Size.Depth * 0.75f);
        var pos = new Vector3(centerX, height, -distance);
        var target = new Vector3(centerX, 0f, centerZ);
        var dir = Vector3.Normalize(target - pos);

        state.PosX = pos.X;
        state.PosY = pos.Y;
        state.PosZ = pos.Z;
        state.Yaw = (float)Math.Atan2(dir.Z, dir.X);
        state.Pitch = (float)Math.Asin(dir.Y);

        return state;
    }

    private void ApplyPlayerState(PlayerWorldState state)
    {
        _player.Position = new Vector3(state.PosX, state.PosY, state.PosZ);
        _player.Yaw = state.Yaw;
        _player.Pitch = state.Pitch;
        _player.Velocity = Vector3.Zero;
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
                Pitch = _player.Pitch
            };
            state.Save(_worldPath, _log);
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
        if (_world == null)
            return false;

        const float eps = 0.001f;
        var half = PlayerController.ColliderHalfWidth;
        var height = PlayerController.ColliderHeight;
        var pos = _player.Position;
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
}
