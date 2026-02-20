using System;
using Microsoft.Xna.Framework;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.Online.Lan;

namespace LatticeVeilMonoGame.Core;

public static class WorldHostBootstrap
{
    public const int DefaultLanServerPort = 27037;

    public static WorldHostStartResult TryStartLanHost(
        Logger log,
        PlayerProfile profile,
        string worldPath,
        string metaPath,
        int serverPort = DefaultLanServerPort)
    {
        try
        {
            var world = VoxelWorld.Load(worldPath, metaPath, log);
            if (world == null)
                return WorldHostStartResult.Fail("WORLD FAILED TO LOAD");

            var meta = world.Meta;
            var hostName = profile.GetDisplayUsername();
            var hostSession = new LanHostSession(log, hostName, BuildWorldInfo(meta), world);
            hostSession.Start(serverPort);

            var discovery = new LanDiscovery(log);
            discovery.StartBroadcast(meta.Name, GetBuildVersion(), serverPort, meta.CurrentWorldGameMode.ToString());
            log.Info($"LAN_HOST_ADVERTISE_START world={meta.Name} port={serverPort} mode={meta.CurrentWorldGameMode}");

            ILanSession session = new LanDiscoveryHostSession(hostSession, discovery);
            return WorldHostStartResult.CreateSuccess(session, world);
        }
        catch (Exception ex)
        {
            log.Warn($"LAN host start failed: {ex.Message}");
            return WorldHostStartResult.Fail("LAN HOST FAILED");
        }
    }

    public static WorldHostStartResult TryStartEosHost(
        Logger log,
        PlayerProfile profile,
        EosClient? eosClient,
        string worldPath,
        string metaPath)
    {
        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(log, out var gateDenied))
            return WorldHostStartResult.Fail(gateDenied);

        if (eosClient == null)
            return WorldHostStartResult.Fail("EOS CLIENT UNAVAILABLE");
        if (!eosClient.IsLoggedIn)
            return WorldHostStartResult.Fail("EOS NOT READY");

        try
        {
            var world = VoxelWorld.Load(worldPath, metaPath, log);
            if (world == null)
                return WorldHostStartResult.Fail("WORLD FAILED TO LOAD");

            var meta = world.Meta;
            var hostName = profile.GetDisplayUsername();
            log.Info($"HOST_CLICKED world={meta.Name} mode={meta.CurrentWorldGameMode} transport=online");
            var session = new EosP2PHostSession(log, eosClient, hostName, BuildWorldInfo(meta), world);

            _ = eosClient.SetHostingPresenceAsync(meta.Name, true);
            _ = UpdateGatePresenceAsync(log, eosClient, meta, true);

            return WorldHostStartResult.CreateSuccess(session, world);
        }
        catch (Exception ex)
        {
            log.Warn($"EOS host start failed: {ex.Message}");
            return WorldHostStartResult.Fail("EOS HOST FAILED");
        }
    }

    public static WorldHostStartResult TryStartLanHost(
        Logger log,
        PlayerProfile profile,
        VoxelWorld world,
        int serverPort = DefaultLanServerPort)
    {
        if (world == null)
            return WorldHostStartResult.Fail("WORLD FAILED TO LOAD");

        try
        {
            var meta = world.Meta;
            var hostName = profile.GetDisplayUsername();
            var hostSession = new LanHostSession(log, hostName, BuildWorldInfo(meta), world);
            hostSession.Start(serverPort);

            var discovery = new LanDiscovery(log);
            discovery.StartBroadcast(meta.Name, GetBuildVersion(), serverPort, meta.CurrentWorldGameMode.ToString());
            log.Info($"LAN_HOST_ADVERTISE_START world={meta.Name} port={serverPort} mode={meta.CurrentWorldGameMode}");

            ILanSession session = new LanDiscoveryHostSession(hostSession, discovery);
            return WorldHostStartResult.CreateSuccess(session, world);
        }
        catch (Exception ex)
        {
            log.Warn($"LAN host start failed: {ex.Message}");
            return WorldHostStartResult.Fail("LAN HOST FAILED");
        }
    }

    public static WorldHostStartResult TryStartEosHost(
        Logger log,
        PlayerProfile profile,
        EosClient? eosClient,
        VoxelWorld world)
    {
        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(log, out var gateDenied))
            return WorldHostStartResult.Fail(gateDenied);

        if (eosClient == null)
            return WorldHostStartResult.Fail("EOS CLIENT UNAVAILABLE");
        if (!eosClient.IsLoggedIn)
            return WorldHostStartResult.Fail("EOS NOT READY");
        if (world == null)
            return WorldHostStartResult.Fail("WORLD FAILED TO LOAD");

        try
        {
            var meta = world.Meta;
            var hostName = profile.GetDisplayUsername();
            log.Info($"HOST_CLICKED world={meta.Name} mode={meta.CurrentWorldGameMode} transport=online");
            var session = new EosP2PHostSession(log, eosClient, hostName, BuildWorldInfo(meta), world);

            _ = eosClient.SetHostingPresenceAsync(meta.Name, true);
            _ = UpdateGatePresenceAsync(log, eosClient, meta, true);

            return WorldHostStartResult.CreateSuccess(session, world);
        }
        catch (Exception ex)
        {
            log.Warn($"EOS host start failed: {ex.Message}");
            return WorldHostStartResult.Fail("EOS HOST FAILED");
        }
    }

    public static void TryClearHostingPresence(Logger log, EosClient? eosClient)
    {
        if (eosClient == null)
            return;

        try
        {
            _ = eosClient.SetHostingPresenceAsync(null, false);
            _ = UpdateGatePresenceAsync(log, eosClient, null, false);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to clear EOS hosting presence: {ex.Message}");
        }
    }

    private static async Task UpdateGatePresenceAsync(Logger log, EosClient? eos, WorldMeta? meta, bool hosting)
    {
        if (eos == null || !eos.IsLoggedIn)
            return;

        try
        {
            var gate = OnlineGateClient.GetOrCreate();
            if (!gate.EnsureTicket(log))
            {
                log.Warn("HOST_ADVERTISE skipped: unable to ensure online ticket for presence signaling.");
                return;
            }

            var identity = EosIdentityStore.LoadOrCreate(log);
            var puid = eos.LocalProductUserId;
            var displayName = identity.GetDisplayNameOrDefault(puid ?? "Player");
            if (string.IsNullOrWhiteSpace(puid))
            {
                log.Warn("HOST_ADVERTISE skipped: local product user id is missing.");
                return;
            }

            bool ok;
            if (hosting)
            {
                ok = await gate.UpsertPresenceAsync(
                    productUserId: puid,
                    displayName: displayName,
                    isHosting: true,
                    worldName: meta?.Name,
                    gameMode: meta?.CurrentWorldGameMode.ToString(),
                    joinTarget: puid, // For P2P, join target is the host's PUID
                    status: $"Hosting {meta?.Name}",
                    cheats: false,
                    playerCount: 1,
                    maxPlayers: 8,
                    isInWorld: false);
            }
            else
            {
                ok = await gate.StopHostingAsync(puid);
            }

            log.Info($"HOST_ADVERTISE { (hosting ? "start" : "stop") } ok={ok} world={meta?.Name ?? "N/A"} transport=EOS_P2P+SUPABASE_PRESENCE");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to update online presence signaling: {ex.Message}");
        }
    }

    private static LanWorldInfo BuildWorldInfo(WorldMeta meta)
    {
        return new LanWorldInfo
        {
            WorldName = meta.Name,
            GameMode = meta.CurrentWorldGameMode,
            Width = meta.Size.Width,
            Height = meta.Size.Height,
            Depth = meta.Size.Depth,
            Seed = meta.Seed,
            PlayerCollision = meta.PlayerCollision,
            WorldId = meta.WorldId
        };
    }

    private static string GetBuildVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "dev" : version;
    }

    private sealed class LanDiscoveryHostSession : ILanSession
    {
        private readonly LanHostSession _inner;
        private readonly LanDiscovery _discovery;
        private bool _disposed;

        public LanDiscoveryHostSession(LanHostSession inner, LanDiscovery discovery)
        {
            _inner = inner;
            _discovery = discovery;
        }

        public bool IsHost => _inner.IsHost;
        public bool IsConnected => _inner.IsConnected;
        public int LocalPlayerId => _inner.LocalPlayerId;

        public void SendPlayerState(Vector3 position, float yaw, float pitch, byte status) => _inner.SendPlayerState(position, yaw, pitch, status);
        public void SendBlockSet(int x, int y, int z, byte id) => _inner.SendBlockSet(x, y, z, id);
        public void SendItemSpawn(LanItemSpawn item) => _inner.SendItemSpawn(item);
        public void SendItemPickup(int itemId) => _inner.SendItemPickup(itemId);
        public void SendChat(LanChatMessage message) => _inner.SendChat(message);
        public void SendPersistenceSnapshot(LanPlayerPersistenceSnapshot snapshot) => _inner.SendPersistenceSnapshot(snapshot);
        public bool SendPersistenceRestore(int targetPlayerId, LanPlayerPersistenceSnapshot snapshot) => _inner.SendPersistenceRestore(targetPlayerId, snapshot);
        public bool SendTeleport(int targetPlayerId, Vector3 position, float yaw, float pitch) => _inner.SendTeleport(targetPlayerId, position, yaw, pitch);
        public bool TryDequeuePlayerState(out LanPlayerState state) => _inner.TryDequeuePlayerState(out state);
        public bool TryDequeueBlockSet(out LanBlockSet block) => _inner.TryDequeueBlockSet(out block);
        public bool TryDequeueItemSpawn(out LanItemSpawn item) => _inner.TryDequeueItemSpawn(out item);
        public bool TryDequeueItemPickup(out LanItemPickup pickup) => _inner.TryDequeueItemPickup(out pickup);
        public bool TryDequeueChat(out LanChatMessage message) => _inner.TryDequeueChat(out message);
        public bool TryDequeuePlayerList(out LanPlayerList list) => _inner.TryDequeuePlayerList(out list);
        public bool TryDequeueChunkData(out LanChunkData chunk) => _inner.TryDequeueChunkData(out chunk);
        public bool TryDequeueWorldSyncComplete(out bool complete) => _inner.TryDequeueWorldSyncComplete(out complete);
        public bool TryDequeueTeleport(out LanTeleport teleport) => _inner.TryDequeueTeleport(out teleport);
        public bool TryDequeuePersistenceSnapshot(out LanPlayerPersistenceSnapshot snapshot) => _inner.TryDequeuePersistenceSnapshot(out snapshot);
        public bool TryDequeuePersistenceRestore(out LanPlayerPersistenceSnapshot snapshot) => _inner.TryDequeuePersistenceRestore(out snapshot);
        public bool TryDequeueDisconnectReason(out string reason) => _inner.TryDequeueDisconnectReason(out reason);
        public bool KickPlayer(int targetPlayerId, string reason) => _inner.KickPlayer(targetPlayerId, reason);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _discovery.StopBroadcast(); } catch { }
            try { _discovery.Dispose(); } catch { }
            _inner.Dispose();
        }
    }
}

public sealed class WorldHostStartResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public ILanSession? Session { get; init; }
    public VoxelWorld? World { get; init; }

    public static WorldHostStartResult Fail(string error) => new() { Success = false, Error = error };

    public static WorldHostStartResult CreateSuccess(ILanSession session, VoxelWorld world)
        => new()
        {
            Success = true,
            Session = session,
            World = world
        };
}
