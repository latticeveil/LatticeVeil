using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Lan;

public enum LanMessageType : byte
{
    Hello = 1,
    Welcome = 2,
    WorldInfo = 3,
    PlayerState = 4,
    BlockSet = 5,
    PlayerList = 6,
    ChunkData = 7,
    WorldSyncComplete = 8,
    ItemSpawn = 9,
    ItemPickup = 10
}

public readonly struct LanWorldInfo
{
    public string WorldName { get; init; }
    public GameMode GameMode { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public int Seed { get; init; }
    public bool PlayerCollision { get; init; }
}

public readonly struct LanPlayerState
{
    public int PlayerId { get; init; }
    public Vector3 Position { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public byte Status { get; init; }
}

public readonly struct LanBlockSet
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public byte Id { get; init; }
}

public readonly struct LanItemSpawn
{
    public int ItemId { get; init; }
    public byte BlockId { get; init; }
    public int Count { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float VelX { get; init; }
    public float VelY { get; init; }
    public float VelZ { get; init; }
    public float PickupDelay { get; init; }
    public int PickupLockPlayerId { get; init; }
}

public readonly struct LanItemPickup
{
    public int ItemId { get; init; }
}

public readonly struct LanPlayerInfo
{
    public int PlayerId { get; init; }
    public string Name { get; init; }
}

public readonly struct LanPlayerList
{
    public LanPlayerInfo[] Players { get; init; }
}

public readonly struct LanChunkData
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public byte[] Blocks { get; init; }
}

public interface ILanSession : IDisposable
{
    bool IsHost { get; }
    bool IsConnected { get; }
    int LocalPlayerId { get; }
    void SendPlayerState(Vector3 position, float yaw, float pitch, byte status);
    void SendBlockSet(int x, int y, int z, byte id);
    void SendItemSpawn(LanItemSpawn item);
    void SendItemPickup(int itemId);
    bool TryDequeuePlayerState(out LanPlayerState state);
    bool TryDequeueBlockSet(out LanBlockSet block);
    bool TryDequeueItemSpawn(out LanItemSpawn item);
    bool TryDequeueItemPickup(out LanItemPickup pickup);
    bool TryDequeuePlayerList(out LanPlayerList list);
    bool TryDequeueChunkData(out LanChunkData chunk);
    bool TryDequeueWorldSyncComplete(out bool complete);
}

public sealed class LanHostSession : ILanSession
{
    private readonly Logger _log;
    private readonly string _hostName;
    private readonly LanWorldInfo _worldInfo;
    private readonly LocalLanServer _server;
    private readonly ConcurrentQueue<LanPlayerState> _playerStates = new();
    private readonly ConcurrentQueue<LanBlockSet> _blockSets = new();
    private readonly ConcurrentQueue<LanItemSpawn> _itemSpawns = new();
    private readonly ConcurrentQueue<LanItemPickup> _itemPickups = new();
    private readonly ConcurrentQueue<LanPlayerList> _playerLists = new();
    private readonly ConcurrentQueue<LanChunkData> _chunkData = new();
    private readonly ConcurrentQueue<bool> _worldSyncComplete = new();
    private readonly Dictionary<int, ClientState> _clients = new();
    private readonly object _clientLock = new();
    private CancellationTokenSource? _cts;
    private int _nextId = 1;
    private readonly VoxelWorld _world;

    public LanHostSession(Logger log, string hostName, LanWorldInfo worldInfo, VoxelWorld world)
    {
        _log = log;
        _hostName = string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName;
        _worldInfo = worldInfo;
        _world = world;
        _server = new LocalLanServer(log);
    }

    public bool IsHost => true;
    public bool IsConnected => _server.IsRunning;
    public int LocalPlayerId => 0;

    public void Start(int port)
    {
        if (_server.IsRunning)
            return;

        _cts = new CancellationTokenSource();
        _server.ClientConnected += HandleClientConnected;
        _server.Start(port);
    }

    public void SendPlayerState(Vector3 position, float yaw, float pitch, byte status)
    {
        var state = new LanPlayerState
        {
            PlayerId = LocalPlayerId,
            Position = position,
            Yaw = yaw,
            Pitch = pitch,
            Status = status
        };
        BroadcastPlayerState(state);
    }

    public void SendBlockSet(int x, int y, int z, byte id)
    {
        var block = new LanBlockSet { X = x, Y = y, Z = z, Id = id };
        BroadcastBlockSet(block);
    }

    public void SendItemSpawn(LanItemSpawn item)
    {
        BroadcastItemSpawn(item);
    }

    public void SendItemPickup(int itemId)
    {
        var pickup = new LanItemPickup { ItemId = itemId };
        BroadcastItemPickup(pickup);
    }

    public bool TryDequeuePlayerState(out LanPlayerState state) => _playerStates.TryDequeue(out state);
    public bool TryDequeueBlockSet(out LanBlockSet block) => _blockSets.TryDequeue(out block);
    public bool TryDequeueItemSpawn(out LanItemSpawn item) => _itemSpawns.TryDequeue(out item);
    public bool TryDequeueItemPickup(out LanItemPickup pickup) => _itemPickups.TryDequeue(out pickup);
    public bool TryDequeuePlayerList(out LanPlayerList list) => _playerLists.TryDequeue(out list);
    public bool TryDequeueChunkData(out LanChunkData chunk) => _chunkData.TryDequeue(out chunk);
    public bool TryDequeueWorldSyncComplete(out bool complete) => _worldSyncComplete.TryDequeue(out complete);

    private void HandleClientConnected(LocalLanConnection connection)
    {
        var id = Interlocked.Increment(ref _nextId);
        var state = new ClientState(connection, id) { Name = $"Player{id}" };

        lock (_clientLock)
            _clients[id] = state;

        _log.Info($"LAN client connected (id={id}) {connection.RemoteEndPoint}");
        _ = Task.Run(() => ClientReadLoop(state, _cts?.Token ?? CancellationToken.None));
        SendWelcomeAndWorldInfo(state);
        SendInitialWorldData(state);
        BroadcastPlayerList();
    }

    private async Task ClientReadLoop(ClientState client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var msg = await LanReader.ReadMessageAsync(client.Stream, token);
                if (msg == null)
                    break;

                switch (msg.Value.Type)
                {
                    case LanMessageType.Hello:
                        client.Name = msg.Value.Name ?? $"Player{client.Id}";
                        _log.Info($"LAN hello from id={client.Id} name={client.Name}");
                        BroadcastPlayerList();
                        break;
                    case LanMessageType.PlayerState:
                        var state = msg.Value.PlayerState;
                        _playerStates.Enqueue(state);
                        BroadcastPlayerState(state);
                        break;
                    case LanMessageType.BlockSet:
                        var block = msg.Value.BlockSet;
                        // Validate and apply to host world
                        if (_world.SetBlock(block.X, block.Y, block.Z, block.Id))
                        {
                            _blockSets.Enqueue(block);
                            BroadcastBlockSet(block);
                        }
                        break;
                    case LanMessageType.ItemSpawn:
                        var item = msg.Value.ItemSpawn;
                        if (item.Count > 0 && item.BlockId != (byte)BlockId.Air)
                        {
                            _itemSpawns.Enqueue(item);
                            BroadcastItemSpawn(item);
                        }
                        break;
                    case LanMessageType.ItemPickup:
                        var pickup = msg.Value.ItemPickup;
                        _itemPickups.Enqueue(pickup);
                        BroadcastItemPickup(pickup);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"LAN client {client.Id} read failed: {ex.Message}");
        }
        finally
        {
            RemoveClient(client.Id);
        }
    }

    private void SendWelcomeAndWorldInfo(ClientState client)
    {
        LanWire.SendWelcome(client, client.Id);
        LanWire.SendWorldInfo(client, _worldInfo);
    }

    private void SendInitialWorldData(ClientState client)
    {
        // Use snapshot to avoid enumeration modification crash
        var chunkSnapshot = _world.AllChunks().ToArray();
        
        foreach (var chunk in chunkSnapshot)
        {
            if (chunk == null) continue;
            var chunkData = new LanChunkData { X = chunk.Coord.X, Y = chunk.Coord.Y, Z = chunk.Coord.Z, Blocks = chunk.Blocks };
            LanWire.SendChunkData(client, chunkData);
        }
        LanWire.SendWorldSyncComplete(client);
    }

    private void BroadcastPlayerState(LanPlayerState state)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                LanWire.SendPlayerState(client, state);
        }
    }

    private void BroadcastBlockSet(LanBlockSet block)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                LanWire.SendBlockSet(client, block);
        }
    }

    private void BroadcastItemSpawn(LanItemSpawn item)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                LanWire.SendItemSpawn(client, item);
        }
    }

    private void BroadcastItemPickup(LanItemPickup pickup)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                LanWire.SendItemPickup(client, pickup);
        }
    }

    private void BroadcastPlayerList()
    {
        lock (_clientLock)
        {
            var players = new List<LanPlayerInfo>(_clients.Count + 1)
            {
                new LanPlayerInfo { PlayerId = LocalPlayerId, Name = _hostName }
            };

            foreach (var client in _clients.Values)
            {
                var name = string.IsNullOrWhiteSpace(client.Name) ? $"Player{client.Id}" : client.Name;
                players.Add(new LanPlayerInfo { PlayerId = client.Id, Name = name });
            }

            var list = new LanPlayerList { Players = players.ToArray() };
            _playerLists.Enqueue(list);

            foreach (var client in _clients.Values)
                LanWire.SendPlayerList(client, list);
        }
    }

    private void RemoveClient(int id)
    {
        ClientState? removed = null;
        lock (_clientLock)
        {
            if (_clients.TryGetValue(id, out removed))
                _clients.Remove(id);
        }

        if (removed != null)
        {
            _log.Info($"LAN client disconnected (id={id})");
            removed.Dispose();
            BroadcastPlayerList();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
        _server.ClientConnected -= HandleClientConnected;
        _server.Stop();

        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
        }
    }

    private sealed class ClientState : IDisposable
    {
        public int Id { get; }
        public string Name { get; set; } = string.Empty;
        public LocalLanConnection Connection { get; }
        public NetworkStream Stream { get; }
        public object SendLock { get; } = new();

        public ClientState(LocalLanConnection connection, int id)
        {
            Connection = connection;
            Id = id;
            Stream = connection.Client.GetStream();
        }

        public void Dispose()
        {
            try { Stream.Close(); } catch { }
            Connection.Dispose();
        }
    }

    private static class LanWire
    {
        public static void SendWelcome(ClientState client, int playerId)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.Welcome);
                bw.Write(playerId);
            });
        }

        public static void SendWorldInfo(ClientState client, LanWorldInfo info)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.WorldInfo);
                bw.Write(info.WorldName ?? "WORLD");
                bw.Write((byte)info.GameMode);
                bw.Write(info.Width);
                bw.Write(info.Height);
                bw.Write(info.Depth);
                bw.Write(info.Seed);
                bw.Write(info.PlayerCollision);
            });
        }

        public static void SendPlayerState(ClientState client, LanPlayerState state)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.PlayerState);
                bw.Write(state.PlayerId);
                bw.Write(state.Position.X);
                bw.Write(state.Position.Y);
                bw.Write(state.Position.Z);
                bw.Write(state.Yaw);
                bw.Write(state.Pitch);
                bw.Write(state.Status);
            });
        }

        public static void SendBlockSet(ClientState client, LanBlockSet block)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.BlockSet);
                bw.Write(block.X);
                bw.Write(block.Y);
                bw.Write(block.Z);
                bw.Write(block.Id);
            });
        }

        public static void SendItemSpawn(ClientState client, LanItemSpawn item)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.ItemSpawn);
                bw.Write(item.ItemId);
                bw.Write(item.BlockId);
                bw.Write(item.Count);
                bw.Write(item.X);
                bw.Write(item.Y);
                bw.Write(item.Z);
                bw.Write(item.VelX);
                bw.Write(item.VelY);
                bw.Write(item.VelZ);
                bw.Write(item.PickupDelay);
                bw.Write(item.PickupLockPlayerId);
            });
        }

        public static void SendItemPickup(ClientState client, LanItemPickup pickup)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.ItemPickup);
                bw.Write(pickup.ItemId);
            });
        }

        public static void SendPlayerList(ClientState client, LanPlayerList list)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.PlayerList);
                var count = list.Players?.Length ?? 0;
                bw.Write(count);
                for (var i = 0; i < count; i++)
                {
                    bw.Write(list.Players[i].PlayerId);
                    bw.Write(list.Players[i].Name ?? string.Empty);
                }
            });
        }

        public static void SendChunkData(ClientState client, LanChunkData chunk)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.ChunkData);
                bw.Write(chunk.X);
                bw.Write(chunk.Y);
                bw.Write(chunk.Z);
                
                using var ms = new MemoryStream();
                using (var deflateStream = new DeflateStream(ms, CompressionMode.Compress))
                {
                    deflateStream.Write(chunk.Blocks, 0, chunk.Blocks.Length);
                }
                var compressed = ms.ToArray();
                bw.Write(compressed.Length);
                bw.Write(compressed);
            });
        }

        public static void SendWorldSyncComplete(ClientState client)
        {
            Write(client, bw =>
            {
                bw.Write((byte)LanMessageType.WorldSyncComplete);
            });
        }

        private static void Write(ClientState client, Action<BinaryWriter> write)
        {
            lock (client.SendLock)
            {
                WritePayload(client.Stream, write);
            }
        }

        private static void WritePayload(NetworkStream stream, Action<BinaryWriter> write)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
                write(bw);

            var payload = ms.ToArray();
            var len = BitConverter.GetBytes(payload.Length);
            stream.Write(len, 0, len.Length);
            stream.Write(payload, 0, payload.Length);
        }
    }
}

public sealed class LanClientConnectResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public ILanSession? Session { get; init; }
    public LanWorldInfo WorldInfo { get; init; }
}

public sealed class LanClientSession : ILanSession
{
    private readonly Logger _log;
    private readonly LocalLanConnection _connection;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly ConcurrentQueue<LanPlayerState> _playerStates = new();
    private readonly ConcurrentQueue<LanBlockSet> _blockSets = new();
    private readonly ConcurrentQueue<LanItemSpawn> _itemSpawns = new();
    private readonly ConcurrentQueue<LanItemPickup> _itemPickups = new();
    private readonly ConcurrentQueue<LanPlayerList> _playerLists = new();
    private readonly ConcurrentQueue<LanChunkData> _chunkData = new();
    private readonly ConcurrentQueue<bool> _worldSyncComplete = new();
    private CancellationTokenSource? _cts;

    public LanWorldInfo WorldInfo { get; }
    public int LocalPlayerId { get; }
    public bool IsHost => false;
    public bool IsConnected { get; private set; }

    private LanClientSession(Logger log, LocalLanConnection connection, NetworkStream stream, int playerId, LanWorldInfo worldInfo)
    {
        _log = log;
        _connection = connection;
        _stream = stream;
        LocalPlayerId = playerId;
        WorldInfo = worldInfo;
        IsConnected = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    public static async Task<LanClientConnectResult> ConnectAsync(Logger log, string host, int port, string username, TimeSpan timeout)
    {
        try
        {
            log.Info($"LAN connect: {host}:{port}");
            var client = new LocalLanClient();
            using var cts = new CancellationTokenSource(timeout);
            var connection = await client.ConnectAsync(host, port, cts.Token);
            var stream = connection.Client.GetStream();
            WritePayload(stream, bw =>
            {
                bw.Write((byte)LanMessageType.Hello);
                bw.Write(username ?? "Player");
            });

            int playerId = -1;
            LanWorldInfo? worldInfo = null;
            while (!cts.IsCancellationRequested && (playerId < 0 || worldInfo == null))
            {
                var msg = await LanReader.ReadMessageAsync(stream, cts.Token);
                if (msg == null)
                    break;

                if (msg.Value.Type == LanMessageType.Welcome)
                    playerId = msg.Value.PlayerId;
                else if (msg.Value.Type == LanMessageType.WorldInfo)
                    worldInfo = msg.Value.WorldInfo;
            }

            if (playerId < 0 || worldInfo == null)
            {
                connection.Dispose();
                return new LanClientConnectResult { Success = false, Error = "Handshake failed." };
            }

            log.Info($"LAN connected: id={playerId} world={worldInfo.Value.WorldName}");
            var session = new LanClientSession(log, connection, stream, playerId, worldInfo.Value);
            return new LanClientConnectResult { Success = true, Session = session, WorldInfo = worldInfo.Value };
        }
        catch (Exception ex)
        {
            log.Warn($"LAN connect failed: {ex.Message}");
            return new LanClientConnectResult { Success = false, Error = ex.Message };
        }
    }

    public void SendPlayerState(Vector3 position, float yaw, float pitch, byte status)
    {
        if (!IsConnected)
            return;

        Write(bw =>
        {
            bw.Write((byte)LanMessageType.PlayerState);
            bw.Write(LocalPlayerId);
            bw.Write(position.X);
            bw.Write(position.Y);
            bw.Write(position.Z);
            bw.Write(yaw);
            bw.Write(pitch);
            bw.Write(status);
        });
    }

    public void SendBlockSet(int x, int y, int z, byte id)
    {
        if (!IsConnected)
            return;

        Write(bw =>
        {
            bw.Write((byte)LanMessageType.BlockSet);
            bw.Write(x);
            bw.Write(y);
            bw.Write(z);
            bw.Write(id);
        });
    }

    public bool TryDequeuePlayerState(out LanPlayerState state) => _playerStates.TryDequeue(out state);
    public bool TryDequeueBlockSet(out LanBlockSet block) => _blockSets.TryDequeue(out block);
    public bool TryDequeueItemSpawn(out LanItemSpawn item) => _itemSpawns.TryDequeue(out item);
    public bool TryDequeueItemPickup(out LanItemPickup pickup) => _itemPickups.TryDequeue(out pickup);
    public bool TryDequeuePlayerList(out LanPlayerList list) => _playerLists.TryDequeue(out list);
    public bool TryDequeueChunkData(out LanChunkData chunk) => _chunkData.TryDequeue(out chunk);
    public bool TryDequeueWorldSyncComplete(out bool complete) => _worldSyncComplete.TryDequeue(out complete);

    public void SendItemSpawn(LanItemSpawn item)
    {
        if (!IsConnected)
            return;

        Write(bw =>
        {
            bw.Write((byte)LanMessageType.ItemSpawn);
            bw.Write(item.ItemId);
            bw.Write(item.BlockId);
            bw.Write(item.Count);
            bw.Write(item.X);
            bw.Write(item.Y);
            bw.Write(item.Z);
            bw.Write(item.VelX);
            bw.Write(item.VelY);
            bw.Write(item.VelZ);
            bw.Write(item.PickupDelay);
            bw.Write(item.PickupLockPlayerId);
        });
    }

    public void SendItemPickup(int itemId)
    {
        if (!IsConnected)
            return;

        Write(bw =>
        {
            bw.Write((byte)LanMessageType.ItemPickup);
            bw.Write(itemId);
        });
    }

    private async Task ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var msg = await LanReader.ReadMessageAsync(_stream, token);
                if (msg == null)
                    break;

                switch (msg.Value.Type)
                {
                    case LanMessageType.PlayerState:
                        _playerStates.Enqueue(msg.Value.PlayerState);
                        break;
                    case LanMessageType.BlockSet:
                        _blockSets.Enqueue(msg.Value.BlockSet);
                        break;
                    case LanMessageType.ItemSpawn:
                        _itemSpawns.Enqueue(msg.Value.ItemSpawn);
                        break;
                    case LanMessageType.ItemPickup:
                        _itemPickups.Enqueue(msg.Value.ItemPickup);
                        break;
                    case LanMessageType.PlayerList:
                        _playerLists.Enqueue(msg.Value.PlayerList);
                        break;
                    case LanMessageType.ChunkData:
                        _chunkData.Enqueue(msg.Value.ChunkData);
                        break;
                    case LanMessageType.WorldSyncComplete:
                        _worldSyncComplete.Enqueue(true);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"LAN read failed: {ex.Message}");
        }
        finally
        {
            IsConnected = false;
        }
    }

    private void Write(Action<BinaryWriter> write)
    {
        lock (_sendLock)
        {
            WritePayload(_stream, write);
        }
    }

    private static void WritePayload(NetworkStream stream, Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
            write(bw);

        var payload = ms.ToArray();
        var len = BitConverter.GetBytes(payload.Length);
        stream.Write(len, 0, len.Length);
        stream.Write(payload, 0, payload.Length);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
        try { _stream.Close(); } catch { }
        _connection.Dispose();
        IsConnected = false;
    }

    // LanMessage defined at namespace scope.
}

public readonly struct LanMessage
{
    public LanMessageType Type { get; init; }
    public int PlayerId { get; init; }
    public string? Name { get; init; }
    public LanWorldInfo WorldInfo { get; init; }
    public LanPlayerState PlayerState { get; init; }
    public LanBlockSet BlockSet { get; init; }
    public LanItemSpawn ItemSpawn { get; init; }
    public LanItemPickup ItemPickup { get; init; }
    public LanPlayerList PlayerList { get; init; }
    public LanChunkData ChunkData { get; init; }
    public bool WorldSyncComplete { get; init; }
}

public static class LanReader
{
    public static async Task<LanMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        var lenBuf = await ReadExactAsync(stream, 4, token);
        if (lenBuf == null)
            return null;

        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 64 * 1024)
            return null;

        var payload = await ReadExactAsync(stream, len, token);
        if (payload == null)
            return null;

        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        var type = (LanMessageType)br.ReadByte();
        switch (type)
        {
            case LanMessageType.Welcome:
                return new LanMessage { Type = type, PlayerId = br.ReadInt32() };
            case LanMessageType.WorldInfo:
                return new LanMessage
                {
                    Type = type,
                    WorldInfo = new LanWorldInfo
                    {
                        WorldName = br.ReadString(),
                        GameMode = (GameMode)br.ReadByte(),
                        Width = br.ReadInt32(),
                        Height = br.ReadInt32(),
                        Depth = br.ReadInt32(),
                        Seed = br.ReadInt32(),
                        PlayerCollision = br.BaseStream.Position < br.BaseStream.Length ? br.ReadBoolean() : true
                    }
                };
            case LanMessageType.PlayerState:
                return new LanMessage
                {
                    Type = type,
                    PlayerState = new LanPlayerState
                    {
                        PlayerId = br.ReadInt32(),
                        Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        Yaw = br.ReadSingle(),
                        Pitch = br.ReadSingle(),
                        Status = br.ReadByte()
                    }
                };
            case LanMessageType.BlockSet:
                return new LanMessage
                {
                    Type = type,
                    BlockSet = new LanBlockSet
                    {
                        X = br.ReadInt32(),
                        Y = br.ReadInt32(),
                        Z = br.ReadInt32(),
                        Id = br.ReadByte()
                    }
                };
            case LanMessageType.ItemSpawn:
                return new LanMessage
                {
                    Type = type,
                    ItemSpawn = new LanItemSpawn
                    {
                        ItemId = br.ReadInt32(),
                        BlockId = br.ReadByte(),
                        Count = br.ReadInt32(),
                        X = br.ReadSingle(),
                        Y = br.ReadSingle(),
                        Z = br.ReadSingle(),
                        VelX = br.ReadSingle(),
                        VelY = br.ReadSingle(),
                        VelZ = br.ReadSingle(),
                        PickupDelay = br.ReadSingle(),
                        PickupLockPlayerId = br.ReadInt32()
                    }
                };
            case LanMessageType.ItemPickup:
                return new LanMessage
                {
                    Type = type,
                    ItemPickup = new LanItemPickup
                    {
                        ItemId = br.ReadInt32()
                    }
                };
            case LanMessageType.PlayerList:
                var count = br.ReadInt32();
                if (count < 0 || count > 256)
                    return null;

                var players = new LanPlayerInfo[count];
                for (var i = 0; i < count; i++)
                {
                    players[i] = new LanPlayerInfo
                    {
                        PlayerId = br.ReadInt32(),
                        Name = br.ReadString()
                    };
                }

                return new LanMessage
                {
                    Type = type,
                    PlayerList = new LanPlayerList { Players = players }
                };
            case LanMessageType.Hello:
                return new LanMessage { Type = type, Name = br.ReadString() };
            case LanMessageType.ChunkData:
                {
                    var x = br.ReadInt32();
                    var y = br.ReadInt32();
                    var z = br.ReadInt32();
                    var chunkLen = br.ReadInt32();
                    var compressed = br.ReadBytes(chunkLen);
                    using var chunkMs = new MemoryStream(compressed);
                    using var ds = new DeflateStream(chunkMs, CompressionMode.Decompress);
                    var blocks = new byte[VoxelChunkData.Volume];
                    ds.Read(blocks, 0, blocks.Length);
                    return new LanMessage
                    {
                        Type = type,
                        ChunkData = new LanChunkData
                        {
                            X = x,
                            Y = y,
                            Z = z,
                            Blocks = blocks
                        }
                    };
                }
            case LanMessageType.WorldSyncComplete:
                return new LanMessage { Type = type, WorldSyncComplete = true };
            default:
                return null;
        }
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer, offset, length - offset, token);
            if (read <= 0)
                return null;
            offset += read;
        }
        return buffer;
    }
}
