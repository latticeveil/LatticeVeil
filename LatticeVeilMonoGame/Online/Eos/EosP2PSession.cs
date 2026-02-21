using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.Online.Lan;

#if EOS_SDK
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
#endif

namespace LatticeVeilMonoGame.Online.Eos;

public readonly struct EosJoinRequest
{
    public string ProductUserId { get; init; }
    public string DisplayName { get; init; }
}

public enum EosJoinApprovalStatus
{
    Approved,
    Pending,
    Declined,
    Failed
}

public readonly struct EosJoinApprovalResult
{
    public EosJoinApprovalStatus Status { get; init; }
    public string Message { get; init; }
}

public sealed class EosP2PHostSession : ILanSession
{
    public bool IsHost => true;
    public bool IsConnected { get; private set; }
    public int LocalPlayerId => 0;

#if EOS_SDK
    private readonly Logger _log;
    private readonly string _hostName;
    private readonly P2PInterface _p2p;
    private readonly ProductUserId _localUserId;
    private readonly SocketId _socketId;
    private readonly LanWorldInfo _worldInfo;
    private readonly ConcurrentQueue<LanPlayerState> _playerStates = new();
    private readonly ConcurrentQueue<LanBlockSet> _blockSets = new();
    private readonly ConcurrentQueue<LanItemSpawn> _itemSpawns = new();
    private readonly ConcurrentQueue<LanItemPickup> _itemPickups = new();
    private readonly ConcurrentQueue<LanChatMessage> _chatMessages = new();
    private readonly ConcurrentQueue<LanPlayerList> _playerLists = new();
    private readonly ConcurrentQueue<LanChunkData> _chunkData = new();
    private readonly ConcurrentQueue<bool> _worldSyncComplete = new();
    private readonly ConcurrentQueue<LanTeleport> _teleports = new();
    private readonly ConcurrentQueue<LanPlayerPersistenceSnapshot> _persistenceSnapshots = new();
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingJoinState> _pendingJoinByPeer = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sociallyApprovedPuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<EosJoinRequest> _pendingJoinQueue = new();
    private readonly object _clientLock = new();
    private int _nextId = 1;
    private ulong _notifyRequestId;
    private ulong _notifyClosedId;
    private bool _disposed;
    private readonly VoxelWorld _world;
    private readonly OnlineGateClient _onlineGate;
#endif

    public EosP2PHostSession(Logger log, EosClient eosClient, string hostName, LanWorldInfo worldInfo, VoxelWorld world)
    {
#if EOS_SDK
        _log = log;
        _hostName = string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName;
        _worldInfo = worldInfo;
        _world = world;
        if (eosClient.P2PInterface == null || eosClient.LocalProductUserIdHandle == null || !eosClient.LocalProductUserIdHandle.IsValid())
            throw new InvalidOperationException("EOS P2P not available.");

        _p2p = eosClient.P2PInterface;
        _localUserId = eosClient.LocalProductUserIdHandle;
        _socketId = EosP2PWire.CreateSocket();
        RegisterNotifications();
        IsConnected = true;
        _onlineGate = OnlineGateClient.GetOrCreate();
        _log.Info("EOS host session ready.");
#endif
    }

    public void PreApprovePuid(string puid)
    {
#if EOS_SDK
        if (string.IsNullOrWhiteSpace(puid)) return;
        lock (_clientLock)
        {
            if (_sociallyApprovedPuids.Add(puid.Trim()))
            {
                _log.Info($"PUID {puid} is now socially pre-approved for P2P join.");
            }

            // If they are already waiting in the native P2P queue, approve them NOW
            if (_pendingJoinByPeer.ContainsKey(puid.Trim()))
            {
                _log.Info($"PUID {puid} was already waiting for P2P approval; auto-approving now.");
                Task.Run(() => ApproveJoinRequest(puid));
            }
        }
#endif
    }

    public bool RevokePuidPreApproval(string puid)
    {
#if EOS_SDK
        if (string.IsNullOrWhiteSpace(puid))
            return false;

        var key = puid.Trim();
        lock (_clientLock)
            return _sociallyApprovedPuids.Remove(key);
#else
        return false;
#endif
    }

    public void SendBlockSet(int x, int y, int z, byte id)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var block = new LanBlockSet { X = x, Y = y, Z = z, Id = id };
        BroadcastBlockSet(block);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendItemSpawn(LanItemSpawn item)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        BroadcastItemSpawn(item);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendItemPickup(int itemId)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var pickup = new LanItemPickup { ItemId = itemId };
        BroadcastItemPickup(pickup);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendChat(LanChatMessage message)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var normalized = new LanChatMessage
        {
            FromPlayerId = LocalPlayerId,
            ToPlayerId = message.ToPlayerId,
            Kind = message.Kind,
            Text = message.Text ?? string.Empty,
            TimestampUtc = message.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : message.TimestampUtc
        };
        _chatMessages.Enqueue(normalized);
        BroadcastChat(normalized);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendPersistenceSnapshot(LanPlayerPersistenceSnapshot snapshot)
    {
        // Host does not send snapshots to itself.
    }

    public bool SendPersistenceRestore(int targetPlayerId, LanPlayerPersistenceSnapshot snapshot)
    {
#if EOS_SDK
        if (!IsConnected || targetPlayerId <= 0)
            return false;

        ClientState? target = null;
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
            {
                if (client.Id != targetPlayerId)
                    continue;
                target = client;
                break;
            }
        }

        if (target == null)
            return false;

        var normalized = new LanPlayerPersistenceSnapshot
        {
            PlayerId = targetPlayerId,
            Username = snapshot.Username ?? string.Empty,
            Payload = snapshot.Payload ?? Array.Empty<byte>(),
            TimestampUtc = snapshot.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : snapshot.TimestampUtc
        };
        EosP2PWire.SendPersistenceRestore(_p2p, _localUserId, target.PeerId, _socketId, normalized, _log);
        return true;
#else
        return false;
#endif
    }

    public void SendPlayerState(Vector3 position, float yaw, float pitch, byte status)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var state = new LanPlayerState
        {
            PlayerId = LocalPlayerId,
            Position = position,
            Yaw = yaw,
            Pitch = pitch,
            Status = status
        };
        BroadcastPlayerState(state);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public bool TryDequeuePlayerState(out LanPlayerState state)
    {
#if EOS_SDK
        return _playerStates.TryDequeue(out state);
#else
        state = default;
        return false;
#endif
    }

    public bool TryDequeueBlockSet(out LanBlockSet block)
    {
#if EOS_SDK
        return _blockSets.TryDequeue(out block);
#else
        block = default;
        return false;
#endif
    }

    public bool TryDequeueItemSpawn(out LanItemSpawn item)
    {
#if EOS_SDK
        return _itemSpawns.TryDequeue(out item);
#else
        item = default;
        return false;
#endif
    }

    public bool TryDequeueItemPickup(out LanItemPickup pickup)
    {
#if EOS_SDK
        return _itemPickups.TryDequeue(out pickup);
#else
        pickup = default;
        return false;
#endif
    }

    public bool TryDequeueChat(out LanChatMessage message)
    {
#if EOS_SDK
        return _chatMessages.TryDequeue(out message);
#else
        message = default;
        return false;
#endif
    }

    public bool TryDequeuePlayerList(out LanPlayerList list)
    {
#if EOS_SDK
        return _playerLists.TryDequeue(out list);
#else
        list = default;
        return false;
#endif
    }

    public bool TryDequeueChunkData(out LanChunkData chunk)
    {
#if EOS_SDK
        return _chunkData.TryDequeue(out chunk);
#else
        chunk = default;
        return false;
#endif
    }

    public bool TryDequeueWorldSyncComplete(out bool complete)
    {
#if EOS_SDK
        return _worldSyncComplete.TryDequeue(out complete);
#else
        complete = default;
        return false;
#endif
    }

    public bool TryDequeueTeleport(out LanTeleport teleport)
    {
#if EOS_SDK
        return _teleports.TryDequeue(out teleport);
#else
        teleport = default;
        return false;
#endif
    }

    public bool TryDequeuePersistenceSnapshot(out LanPlayerPersistenceSnapshot snapshot)
    {
#if EOS_SDK
        return _persistenceSnapshots.TryDequeue(out snapshot);
#else
        snapshot = default;
        return false;
#endif
    }

    public bool TryDequeuePersistenceRestore(out LanPlayerPersistenceSnapshot snapshot)
    {
        snapshot = default;
        return false;
    }

    public bool TryDequeueDisconnectReason(out string reason)
    {
        reason = string.Empty;
        return false;
    }

    public bool SendTeleport(int targetPlayerId, Vector3 position, float yaw, float pitch)
    {
#if EOS_SDK
        if (!IsConnected || targetPlayerId <= 0)
            return false;

        ClientState? target = null;
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
            {
                if (client.Id != targetPlayerId)
                    continue;
                target = client;
                break;
            }
        }

        if (target == null)
            return false;

        var teleport = new LanTeleport
        {
            TargetPlayerId = targetPlayerId,
            X = (int)MathF.Floor(position.X),
            Y = (int)MathF.Floor(position.Y),
            Z = (int)MathF.Floor(position.Z),
            Yaw = yaw,
            Pitch = pitch
        };
        EosP2PWire.SendTeleport(_p2p, _localUserId, target.PeerId, _socketId, teleport, _log);
        return true;
#else
        return false;
#endif
    }

    public bool KickPlayer(int targetPlayerId, string reason)
    {
#if EOS_SDK
        if (!IsConnected || targetPlayerId <= 0)
            return false;

        ClientState? target = null;
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
            {
                if (client.Id != targetPlayerId)
                    continue;
                target = client;
                break;
            }
        }

        if (target == null)
            return false;

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Kicked by host."
            : $"Kicked by host: {reason.Trim()}";

        try
        {
            EosP2PWire.SendHostShutdown(_p2p, _localUserId, target.PeerId, _socketId, normalizedReason, _log);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to send kick notice to EOS client {targetPlayerId}: {ex.Message}");
        }

        ClosePeerConnection(target.PeerId);
        RemoveClient(target.PeerId);

        return true;
#else
        return false;
#endif
    }

    public bool TryDequeueJoinRequest(out EosJoinRequest request)
    {
#if EOS_SDK
        return _pendingJoinQueue.TryDequeue(out request);
#else
        request = default;
        return false;
#endif
    }

    public bool ApproveJoinRequest(string peerProductUserId)
    {
#if EOS_SDK
        if (string.IsNullOrWhiteSpace(peerProductUserId))
            return false;

        PendingJoinState? pending;
        ClientState? approved = null;
        lock (_clientLock)
        {
            if (!_pendingJoinByPeer.TryGetValue(peerProductUserId.Trim(), out pending))
                return false;

            _sociallyApprovedPuids.Add(peerProductUserId.Trim());
            _pendingJoinByPeer.Remove(pending.Key);
            var id = Interlocked.Increment(ref _nextId);
            approved = new ClientState(pending.PeerId, id)
            {
                Name = string.IsNullOrWhiteSpace(pending.Name) ? $"Player{id}" : pending.Name
            };
            _clients[approved.Key] = approved;
        }

        SendWelcome(approved);
        SendWorldInfo(approved, _worldInfo);
        SendInitialWorldData(approved);
        _log.Info($"EOS join request approved ({pending!.Key}).");
        BroadcastPlayerList();

        return true;
#else
        return false;
#endif
    }

    public bool DeclineJoinRequest(string peerProductUserId, string reason = "Host declined join request.")
    {
#if EOS_SDK
        if (string.IsNullOrWhiteSpace(peerProductUserId))
            return false;

        PendingJoinState? pending;
        lock (_clientLock)
        {
            if (!_pendingJoinByPeer.TryGetValue(peerProductUserId.Trim(), out pending))
                return false;

            _pendingJoinByPeer.Remove(pending.Key);
        }

        EosP2PWire.SendJoinDenied(_p2p, _localUserId, pending!.PeerId, _socketId, reason, _log);
        _log.Info($"EOS join request declined ({pending!.Key}).");
        ClosePeerConnection(pending.PeerId);

        return true;
#else
        return false;
#endif
    }

#if EOS_SDK
    private void RegisterNotifications()
    {
        var requestOptions = new AddNotifyPeerConnectionRequestOptions
        {
            LocalUserId = _localUserId,
            SocketId = _socketId
        };
        _notifyRequestId = _p2p.AddNotifyPeerConnectionRequest(ref requestOptions, null, OnConnectionRequest);

        var closeOptions = new AddNotifyPeerConnectionClosedOptions
        {
            LocalUserId = _localUserId,
            SocketId = _socketId
        };
        _notifyClosedId = _p2p.AddNotifyPeerConnectionClosed(ref closeOptions, null, OnConnectionClosed);
    }

    private void OnConnectionRequest(ref OnIncomingConnectionRequestInfo info)
    {
        if (_disposed)
            return;

        if (info.RemoteUserId == null || !info.RemoteUserId.IsValid())
            return;

        if (!EosP2PWire.SocketMatches(info.SocketId, _socketId))
            return;

        var acceptOptions = new AcceptConnectionOptions
        {
            LocalUserId = _localUserId,
            RemoteUserId = info.RemoteUserId,
            SocketId = _socketId
        };

        var result = _p2p.AcceptConnection(ref acceptOptions);
        if (result != Result.Success)
            _log.Warn($"EOS accept failed: {result}");
    }

    private void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
    {
        if (info.RemoteUserId == null || !info.RemoteUserId.IsValid())
            return;

        if (!EosP2PWire.SocketMatches(info.SocketId, _socketId))
            return;

        RemoveClient(info.RemoteUserId);
    }

    public void PumpIncoming(int maxPackets = 128)
    {
#if EOS_SDK
        if (_disposed || !IsConnected)
            return;

        var processed = 0;
        try
        {
            while (processed < maxPackets &&
                   EosP2PWire.TryReceivePacket(_p2p, _localUserId, _socketId, out var peerId, out var msg))
            {
                processed++;
                if (peerId == null || !peerId.IsValid())
                    continue;

                switch (msg.Type)
                {
                    case LanMessageType.Hello:
                        HandleHello(peerId, msg.Name, msg.GateTicket);
                        break;
                    case LanMessageType.PlayerState:
                        if (!TryGetClient(peerId, out _))
                            break;
                        _playerStates.Enqueue(msg.PlayerState);
                        BroadcastPlayerState(msg.PlayerState);
                        break;
                    case LanMessageType.BlockSet:
                        if (!TryGetClient(peerId, out _))
                            break;

                        // Validate the block placement on the host before broadcasting.
                        var b = msg.BlockSet;
                        if (_world.SetBlock(b.X, b.Y, b.Z, b.Id))
                        {
                            // The change is valid, so enqueue it for the host's own world
                            // and broadcast it to all other clients.
                            _blockSets.Enqueue(b);
                            BroadcastBlockSet(b);
                        }
                        break;
                    case LanMessageType.ItemSpawn:
                        if (!TryGetClient(peerId, out _))
                            break;
                        var item = msg.ItemSpawn;
                        if (item.Count > 0 && item.BlockId != (byte)BlockId.Air)
                        {
                            _itemSpawns.Enqueue(item);
                            BroadcastItemSpawn(item);
                        }
                        break;
                    case LanMessageType.ItemPickup:
                        if (!TryGetClient(peerId, out _))
                            break;
                        var pickup = msg.ItemPickup;
                        _itemPickups.Enqueue(pickup);
                        BroadcastItemPickup(pickup);
                        break;
                    case LanMessageType.Chat:
                        if (!TryGetClient(peerId, out var sender))
                            break;
                        var chat = msg.Chat;
                        var normalized = new LanChatMessage
                        {
                            FromPlayerId = sender.Id,
                            ToPlayerId = chat.ToPlayerId,
                            Kind = chat.Kind,
                            Text = chat.Text ?? string.Empty,
                            TimestampUtc = chat.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : chat.TimestampUtc
                        };
                        if (normalized.ToPlayerId < 0 || normalized.ToPlayerId == LocalPlayerId || normalized.FromPlayerId == LocalPlayerId)
                            _chatMessages.Enqueue(normalized);
                        BroadcastChat(normalized);
                        break;
                    case LanMessageType.PersistenceSnapshot:
                        if (!TryGetClient(peerId, out var snapshotSender))
                            break;
                        var snapshot = msg.PersistenceSnapshot;
                        _persistenceSnapshots.Enqueue(new LanPlayerPersistenceSnapshot
                        {
                            PlayerId = snapshotSender.Id,
                            Username = snapshot.Username ?? snapshotSender.Name ?? string.Empty,
                            Payload = snapshot.Payload ?? Array.Empty<byte>(),
                            TimestampUtc = snapshot.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : snapshot.TimestampUtc
                        });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS host pump failed: {ex.Message}");
        }
#endif
    }
    private void HandleHello(ProductUserId peerId, string? name, string? gateTicket)
    {
        if (TryGetClient(peerId, out var approvedClient))
        {
            if (!string.IsNullOrWhiteSpace(name))
                approvedClient.Name = name;

            SendWelcome(approvedClient);
            SendWorldInfo(approvedClient, _worldInfo);
            SendInitialWorldData(approvedClient);
            return;
        }

        if (!TryAuthorizePeer(peerId, gateTicket))
            return;

        var key = peerId.ToString();
        lock (_clientLock)
        {
            if (_sociallyApprovedPuids.Contains(key))
            {
                if (!_pendingJoinByPeer.TryGetValue(key, out var pendingApproved))
                {
                    pendingApproved = new PendingJoinState(peerId, name);
                    _pendingJoinByPeer[key] = pendingApproved;
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    pendingApproved.Name = name.Trim();
                }

                if (pendingApproved.ApprovalQueued)
                    return;

                pendingApproved.ApprovalQueued = true;
                _log.Info($"EOS P2P: Auto-approving pre-approved peer {key}.");

                // Run on a thread to avoid deadlocking the pump. Keep single-flight semantics per peer.
                Task.Run(() =>
                {
                    var ok = ApproveJoinRequest(key);
                    if (ok)
                        return;

                    lock (_clientLock)
                    {
                        if (_pendingJoinByPeer.TryGetValue(key, out var pending) && pending != null)
                            pending.ApprovalQueued = false;
                    }
                });
                return;
            }

            PendingJoinState? pending;
            if (!_pendingJoinByPeer.TryGetValue(key, out pending))
            {
                pending = new PendingJoinState(peerId, name);
                _pendingJoinByPeer[pending.Key] = pending;
                _pendingJoinQueue.Enqueue(new EosJoinRequest
                {
                    ProductUserId = pending.Key,
                    DisplayName = pending.Name
                });
                _log.Info($"EOS join request queued ({pending.Key}, name={pending.Name}).");
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                pending.Name = name.Trim();
            }
        }
    }

    private bool TryAuthorizePeer(ProductUserId peerId, string? gateTicket)
    {
        if (!_onlineGate.IsGateRequired)
            return true;

        if (_onlineGate.ValidatePeerTicket(gateTicket, _log, out var denialReason, TimeSpan.FromSeconds(4)))
            return true;

        _log.Warn($"EOS peer rejected by gate validation ({peerId}): {denialReason}");
        EosP2PWire.SendJoinDenied(_p2p, _localUserId, peerId, _socketId, denialReason, _log);
        ClosePeerConnection(peerId);
        return false;
    }

    private void ClosePeerConnection(ProductUserId peerId)
    {
        try
        {
            var closeOptions = new CloseConnectionOptions
            {
                LocalUserId = _localUserId,
                RemoteUserId = peerId,
                SocketId = _socketId
            };

            var result = _p2p.CloseConnection(ref closeOptions);
            if (result != Result.Success)
                _log.Warn($"EOS close peer failed for {peerId}: {result}");
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS close peer exception for {peerId}: {ex.Message}");
        }
    }

    private bool TryGetClient(ProductUserId peerId, out ClientState client)
    {
        var key = peerId.ToString();
        lock (_clientLock)
            return _clients.TryGetValue(key, out client!);
    }

    private void BroadcastPlayerState(LanPlayerState state)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                EosP2PWire.SendPlayerState(_p2p, _localUserId, client.PeerId, _socketId, state, _log);
        }
    }

    private void BroadcastBlockSet(LanBlockSet block)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                EosP2PWire.SendBlockSet(_p2p, _localUserId, client.PeerId, _socketId, block, _log);
        }
    }

    private void BroadcastItemSpawn(LanItemSpawn item)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                EosP2PWire.SendItemSpawn(_p2p, _localUserId, client.PeerId, _socketId, item, _log);
        }
    }

    private void BroadcastItemPickup(LanItemPickup pickup)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
                EosP2PWire.SendItemPickup(_p2p, _localUserId, client.PeerId, _socketId, pickup, _log);
        }
    }

    private void BroadcastChat(LanChatMessage chat)
    {
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
            {
                if (chat.ToPlayerId >= 0 && chat.ToPlayerId != client.Id && chat.FromPlayerId != client.Id)
                    continue;

                EosP2PWire.SendChat(_p2p, _localUserId, client.PeerId, _socketId, chat, _log);
            }
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
                EosP2PWire.SendPlayerList(_p2p, _localUserId, client.PeerId, _socketId, list, _log);
        }
    }

    private void SendWelcome(ClientState client)
    {
        EosP2PWire.SendWelcome(_p2p, _localUserId, client.PeerId, _socketId, client.Id, _log);
    }

    private void SendWorldInfo(ClientState client, LanWorldInfo info)
    {
        EosP2PWire.SendWorldInfo(_p2p, _localUserId, client.PeerId, _socketId, info, _log);
    }

    private void SendInitialWorldData(ClientState client)
    {
        // Use snapshot to avoid enumeration modification crash
        var chunkSnapshot = _world.AllChunks().ToArray();
        
        foreach (var chunk in chunkSnapshot)
        {
            if (chunk == null) continue;
            var chunkData = new LanChunkData { X = chunk.Coord.X, Y = chunk.Coord.Y, Z = chunk.Coord.Z, Blocks = chunk.Blocks };
            EosP2PWire.SendChunkData(_p2p, _localUserId, client.PeerId, _socketId, chunkData, _log);
        }
        EosP2PWire.SendWorldSyncComplete(_p2p, _localUserId, client.PeerId, _socketId, _log);
    }

    private void RemoveClient(ProductUserId peerId)
    {
        ClientState? removed = null;
        PendingJoinState? removedPending = null;
        var key = peerId.ToString();
        lock (_clientLock)
        {
            if (_clients.TryGetValue(key, out removed))
                _clients.Remove(key);
            if (_pendingJoinByPeer.TryGetValue(key, out removedPending))
                _pendingJoinByPeer.Remove(key);
        }

        if (removed != null)
        {
            _log.Info($"EOS client disconnected (id={removed.Id})");
            BroadcastPlayerList();
        }
        else if (removedPending != null)
        {
            _log.Info($"EOS pending join request removed ({removedPending.Key}).");
        }
    }
#endif

    public void Dispose()
    {
#if EOS_SDK
        if (_disposed)
            return;

        _disposed = true;
        IsConnected = false;

        var shutdownReason = "Host disconnected. Returning to menu.";
        lock (_clientLock)
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    EosP2PWire.SendHostShutdown(_p2p, _localUserId, client.PeerId, _socketId, shutdownReason, _log);
                }
                catch
                {
                    // Best effort before closing all connections.
                }
            }
        }

        if (_notifyRequestId != 0)
            _p2p.RemoveNotifyPeerConnectionRequest(_notifyRequestId);
        if (_notifyClosedId != 0)
            _p2p.RemoveNotifyPeerConnectionClosed(_notifyClosedId);

        var closeOptions = new CloseConnectionsOptions
        {
            LocalUserId = _localUserId,
            SocketId = _socketId
        };
        _p2p.CloseConnections(ref closeOptions);

        lock (_clientLock)
        {
            _clients.Clear();
            _pendingJoinByPeer.Clear();
        }
#endif
    }

#if EOS_SDK
    private sealed class ClientState
    {
        public ClientState(ProductUserId peerId, int id)
        {
            PeerId = peerId;
            Id = id;
            Key = peerId.ToString();
        }

        public ProductUserId PeerId { get; }
        public int Id { get; }
        public string Name { get; set; } = string.Empty;
        public string Key { get; }
    }

    private sealed class PendingJoinState
    {
        public PendingJoinState(ProductUserId peerId, string? name)
        {
            PeerId = peerId;
            Key = peerId.ToString();
            Name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        }

        public ProductUserId PeerId { get; }
        public string Key { get; }
        public string Name { get; set; }
        public bool ApprovalQueued { get; set; }
    }
#endif
}

public sealed class EosP2PClientSession : ILanSession
{
    public bool IsHost => false;
    public bool IsConnected { get; private set; }
    public int LocalPlayerId { get; private set; } = -1;

    public LanWorldInfo WorldInfo { get; private set; }
    public string HostProductUserId { get; private set; } = string.Empty;

#if EOS_SDK
    private readonly Logger _log;
    private readonly P2PInterface _p2p;
    private readonly ProductUserId _localUserId;
    private readonly ProductUserId _hostUserId;
    private readonly SocketId _socketId;
    private readonly ConcurrentQueue<LanPlayerState> _playerStates = new();
    private readonly ConcurrentQueue<LanBlockSet> _blockSets = new();
    private readonly ConcurrentQueue<LanItemSpawn> _itemSpawns = new();
    private readonly ConcurrentQueue<LanItemPickup> _itemPickups = new();
    private readonly ConcurrentQueue<LanChatMessage> _chatMessages = new();
    private readonly ConcurrentQueue<LanPlayerList> _playerLists = new();
    private readonly ConcurrentQueue<LanChunkData> _chunkData = new();
    private readonly ConcurrentQueue<bool> _worldSyncComplete = new();
    private readonly ConcurrentQueue<LanTeleport> _teleports = new();
    private readonly ConcurrentQueue<LanPlayerPersistenceSnapshot> _persistenceRestores = new();
    private readonly ConcurrentQueue<string> _disconnectReasons = new();
    private ulong _notifyClosedId;
    private bool _disposed;
    private int _disconnectReasonSet;
#endif

    private EosP2PClientSession(
#if EOS_SDK
        Logger log,
        P2PInterface p2p,
        ProductUserId localUserId,
        ProductUserId hostUserId,
        SocketId socketId,
        int playerId,
        LanWorldInfo worldInfo
#endif
        )
    {
#if EOS_SDK
        _log = log;
        _p2p = p2p;
        _localUserId = localUserId;
        _hostUserId = hostUserId;
        _socketId = socketId;
        LocalPlayerId = playerId;
        WorldInfo = worldInfo;
        HostProductUserId = hostUserId.ToString();
        IsConnected = true;
        RegisterNotifications();
#endif
    }

    public static async Task<LanClientConnectResult> ConnectAsync(Logger log, EosClient eosClient, string hostJoinInfo, string username, TimeSpan timeout)
    {
#if EOS_SDK
        if (eosClient.P2PInterface == null || eosClient.LocalProductUserIdHandle == null || !eosClient.LocalProductUserIdHandle.IsValid())
            return new LanClientConnectResult { Success = false, Error = "EOS not ready." };

        var hostId = ParseProductUserId(hostJoinInfo);
        if (hostId == null || !hostId.IsValid())
            return new LanClientConnectResult { Success = false, Error = "Invalid host join info." };

        var p2p = eosClient.P2PInterface;
        var localId = eosClient.LocalProductUserIdHandle;
        var socketId = EosP2PWire.CreateSocket();
        var onlineGate = OnlineGateClient.GetOrCreate();
        string? gateTicket = null;

        if (!onlineGate.CanUseOfficialOnline(log, out var gateDenied))
            return new LanClientConnectResult { Success = false, Error = gateDenied };

        if (onlineGate.IsGateRequired)
        {
            if (!onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _)
                && !onlineGate.EnsureTicket(log, TimeSpan.FromSeconds(6)))
            {
                return new LanClientConnectResult
                {
                    Success = false,
                    Error = string.IsNullOrWhiteSpace(onlineGate.DenialReason)
                        ? "Online gate ticket unavailable."
                        : onlineGate.DenialReason
                };
            }

            if (!onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _))
                return new LanClientConnectResult { Success = false, Error = "Online gate ticket unavailable." };
        }
        else
        {
            onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _);
        }

        var acceptOptions = new AcceptConnectionOptions
        {
            LocalUserId = localId,
            RemoteUserId = hostId,
            SocketId = socketId
        };
        p2p.AcceptConnection(ref acceptOptions);

        var helloPayload = EosP2PWire.BuildHelloPayload(username ?? "Player", gateTicket, log);
        if (!EosP2PWire.SendRaw(p2p, localId, hostId, socketId, helloPayload, PacketReliability.ReliableOrdered, log))
            return new LanClientConnectResult { Success = false, Error = "Failed to send hello." };
		var nextHelloResend = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        var playerId = -1;
        LanWorldInfo? worldInfo = null;
        var deadline = DateTime.UtcNow + timeout;
        log.Info($"EOS P2P: Entering connection wait loop (timeout={timeout.TotalSeconds}s)...");
        while (DateTime.UtcNow < deadline && (playerId < 0 || worldInfo == null))
        {
			// In lossy / relay scenarios the initial HELLO can get dropped.
			// Re-send periodically until we get the welcome.
			if (playerId < 0 && DateTime.UtcNow >= nextHelloResend)
			{
				log.Info("EOS P2P: Re-sending HELLO...");
				EosP2PWire.SendRaw(p2p, localId, hostId, socketId, helloPayload, PacketReliability.ReliableOrdered, log);
				nextHelloResend = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
			}

            if (!EosP2PWire.TryReceivePacket(p2p, localId, socketId, out var peerId, out var msg))
            {
                await Task.Delay(50);
                continue;
            }

            if (peerId == null || !peerId.IsValid() || !peerId.Equals(hostId))
                continue;

            log.Info($"EOS P2P: Received message from host: {msg.Type}");
            if (msg.Type == LanMessageType.Welcome)
            {
                playerId = msg.PlayerId;
                log.Info($"EOS P2P: Received Welcome (PlayerId={playerId})");
            }
            else if (msg.Type == LanMessageType.WorldInfo)
            {
                worldInfo = msg.WorldInfo;
                log.Info($"EOS P2P: Received WorldInfo (World='{worldInfo.Value.WorldName}')");
            }
            else if (msg.Type == LanMessageType.JoinDenied)
            {
                var reason = string.IsNullOrWhiteSpace(msg.JoinDeniedReason)
                    ? "Host denied join request."
                    : msg.JoinDeniedReason;
                return new LanClientConnectResult { Success = false, Error = reason };
            }
        }

        if (playerId < 0 || worldInfo == null)
            return new LanClientConnectResult { Success = false, Error = "Join request pending or timed out. Ask host to approve." };

        var session = new EosP2PClientSession(log, p2p, localId, hostId, socketId, playerId, worldInfo.Value);
        return new LanClientConnectResult { Success = true, Session = session, WorldInfo = worldInfo.Value };
#else
        return new LanClientConnectResult { Success = false, Error = "EOS SDK not available." };
#endif
    }

    public static async Task<EosJoinApprovalResult> RequestJoinApprovalAsync(
        Logger log,
        EosClient eosClient,
        string hostJoinInfo,
        string username,
        TimeSpan timeout)
    {
#if EOS_SDK
        if (eosClient.P2PInterface == null || eosClient.LocalProductUserIdHandle == null || !eosClient.LocalProductUserIdHandle.IsValid())
            return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = "EOS not ready." };

        var hostId = ParseProductUserId(hostJoinInfo);
        if (hostId == null || !hostId.IsValid())
            return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = "Invalid host join info." };

        var p2p = eosClient.P2PInterface;
        var localId = eosClient.LocalProductUserIdHandle;
        var socketId = EosP2PWire.CreateSocket();
        var onlineGate = OnlineGateClient.GetOrCreate();
        string? gateTicket = null;

        if (!onlineGate.CanUseOfficialOnline(log, out var gateDenied))
            return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = gateDenied };

        if (onlineGate.IsGateRequired)
        {
            if (!onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _)
                && !onlineGate.EnsureTicket(log, TimeSpan.FromSeconds(6)))
            {
                return new EosJoinApprovalResult
                {
                    Status = EosJoinApprovalStatus.Failed,
                    Message = string.IsNullOrWhiteSpace(onlineGate.DenialReason)
                        ? "Online gate ticket unavailable."
                        : onlineGate.DenialReason
                };
            }

            if (!onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _))
                return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = "Online gate ticket unavailable." };
        }
        else
        {
            onlineGate.TryGetValidTicketForChildProcess(out gateTicket, out _);
        }

        try
        {
            var acceptOptions = new AcceptConnectionOptions
            {
                LocalUserId = localId,
                RemoteUserId = hostId,
                SocketId = socketId
            };
            p2p.AcceptConnection(ref acceptOptions);

            var helloPayload = EosP2PWire.BuildHelloPayload(username ?? "Player", gateTicket, log);
            if (!EosP2PWire.SendRaw(p2p, localId, hostId, socketId, helloPayload, PacketReliability.ReliableOrdered, log))
                return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = "Failed to send join request." };

            var nextHelloResend = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            var deadline = DateTime.UtcNow + timeout;
            log.Info($"EOS_JOIN_REQUEST sent host={hostId} timeout={timeout.TotalSeconds:0}s");

            while (DateTime.UtcNow < deadline)
            {
                if (DateTime.UtcNow >= nextHelloResend)
                {
                    EosP2PWire.SendRaw(p2p, localId, hostId, socketId, helloPayload, PacketReliability.ReliableOrdered, log);
                    nextHelloResend = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
                }

                if (!EosP2PWire.TryReceivePacket(p2p, localId, socketId, out var peerId, out var msg))
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    continue;
                }

                if (peerId == null || !peerId.IsValid() || !peerId.Equals(hostId))
                    continue;

                if (msg.Type == LanMessageType.JoinDenied)
                {
                    var reason = string.IsNullOrWhiteSpace(msg.JoinDeniedReason)
                        ? "Host declined join request."
                        : msg.JoinDeniedReason!;
                    log.Info($"EOS_JOIN_REQUEST_RESULT host={hostId} status=declined");
                    return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Declined, Message = reason };
                }

                if (msg.Type == LanMessageType.Welcome || msg.Type == LanMessageType.WorldInfo)
                {
                    log.Info($"EOS_JOIN_REQUEST_RESULT host={hostId} status=approved");
                    return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Approved, Message = "approved" };
                }
            }

            log.Info($"EOS_JOIN_REQUEST_RESULT host={hostId} status=pending");
            return new EosJoinApprovalResult
            {
                Status = EosJoinApprovalStatus.Pending,
                Message = "Request sent. Waiting for host approval."
            };
        }
        finally
        {
            try
            {
                var closeOptions = new CloseConnectionsOptions
                {
                    LocalUserId = localId,
                    SocketId = socketId
                };
                p2p.CloseConnections(ref closeOptions);
            }
            catch (Exception ex)
            {
                log.Warn($"EOS_JOIN_REQUEST cleanup failed: {ex.Message}");
            }
        }
#else
        await Task.CompletedTask;
        return new EosJoinApprovalResult { Status = EosJoinApprovalStatus.Failed, Message = "EOS SDK not available." };
#endif
    }

    public void SendPlayerState(Vector3 position, float yaw, float pitch, byte status)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var state = new LanPlayerState
        {
            PlayerId = LocalPlayerId,
            Position = position,
            Yaw = yaw,
            Pitch = pitch,
            Status = status
        };
        EosP2PWire.SendPlayerState(_p2p, _localUserId, _hostUserId, _socketId, state, _log);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendBlockSet(int x, int y, int z, byte id)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var block = new LanBlockSet { X = x, Y = y, Z = z, Id = id };
        EosP2PWire.SendBlockSet(_p2p, _localUserId, _hostUserId, _socketId, block, _log);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendItemSpawn(LanItemSpawn item)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        EosP2PWire.SendItemSpawn(_p2p, _localUserId, _hostUserId, _socketId, item, _log);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendItemPickup(int itemId)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var pickup = new LanItemPickup { ItemId = itemId };
        EosP2PWire.SendItemPickup(_p2p, _localUserId, _hostUserId, _socketId, pickup, _log);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendChat(LanChatMessage message)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var normalized = new LanChatMessage
        {
            FromPlayerId = LocalPlayerId,
            ToPlayerId = message.ToPlayerId,
            Kind = message.Kind,
            Text = message.Text ?? string.Empty,
            TimestampUtc = message.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : message.TimestampUtc
        };
        EosP2PWire.SendChat(_p2p, _localUserId, _hostUserId, _socketId, normalized, _log);
#else
        // No-op when EOS SDK is not available
#endif
    }

    public void SendPersistenceSnapshot(LanPlayerPersistenceSnapshot snapshot)
    {
#if EOS_SDK
        if (!IsConnected)
            return;

        var normalized = new LanPlayerPersistenceSnapshot
        {
            PlayerId = LocalPlayerId,
            Username = snapshot.Username ?? string.Empty,
            Payload = snapshot.Payload ?? Array.Empty<byte>(),
            TimestampUtc = snapshot.TimestampUtc <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : snapshot.TimestampUtc
        };

        EosP2PWire.SendPersistenceSnapshot(_p2p, _localUserId, _hostUserId, _socketId, normalized, _log);
#endif
    }

    public bool SendPersistenceRestore(int targetPlayerId, LanPlayerPersistenceSnapshot snapshot)
    {
        return false;
    }


    public bool TryDequeuePlayerState(out LanPlayerState state)
    {
#if EOS_SDK
        return _playerStates.TryDequeue(out state);
#else
        state = default;
        return false;
#endif
    }

    public bool TryDequeueBlockSet(out LanBlockSet block)
    {
#if EOS_SDK
        return _blockSets.TryDequeue(out block);
#else
        block = default;
        return false;
#endif
    }

    public bool TryDequeueItemSpawn(out LanItemSpawn item)
    {
#if EOS_SDK
        return _itemSpawns.TryDequeue(out item);
#else
        item = default;
        return false;
#endif
    }

    public bool TryDequeueItemPickup(out LanItemPickup pickup)
    {
#if EOS_SDK
        return _itemPickups.TryDequeue(out pickup);
#else
        pickup = default;
        return false;
#endif
    }

    public bool TryDequeueChat(out LanChatMessage message)
    {
#if EOS_SDK
        return _chatMessages.TryDequeue(out message);
#else
        message = default;
        return false;
#endif
    }

    public bool TryDequeuePlayerList(out LanPlayerList list)
    {
#if EOS_SDK
        return _playerLists.TryDequeue(out list);
#else
        list = default;
        return false;
#endif
    }

    public bool TryDequeueChunkData(out LanChunkData chunk)
    {
#if EOS_SDK
        return _chunkData.TryDequeue(out chunk);
#else
        chunk = default;
        return false;
#endif
    }

    public bool TryDequeueWorldSyncComplete(out bool complete)
    {
#if EOS_SDK
        return _worldSyncComplete.TryDequeue(out complete);
#else
        complete = default;
        return false;
#endif
    }

    public bool TryDequeueTeleport(out LanTeleport teleport)
    {
#if EOS_SDK
        return _teleports.TryDequeue(out teleport);
#else
        teleport = default;
        return false;
#endif
    }

    public bool TryDequeuePersistenceSnapshot(out LanPlayerPersistenceSnapshot snapshot)
    {
        snapshot = default;
        return false;
    }

    public bool TryDequeuePersistenceRestore(out LanPlayerPersistenceSnapshot snapshot)
    {
#if EOS_SDK
        return _persistenceRestores.TryDequeue(out snapshot);
#else
        snapshot = default;
        return false;
#endif
    }

    public bool TryDequeueDisconnectReason(out string reason)
    {
#if EOS_SDK
        if (_disconnectReasons.TryDequeue(out var value))
        {
            reason = value;
            return true;
        }
#endif
        reason = string.Empty;
        return false;
    }

    public bool SendTeleport(int targetPlayerId, Vector3 position, float yaw, float pitch)
    {
        return false;
    }

    public bool KickPlayer(int targetPlayerId, string reason)
    {
        return false;
    }

#if EOS_SDK
    private void RegisterNotifications()
    {
        var closeOptions = new AddNotifyPeerConnectionClosedOptions
        {
            LocalUserId = _localUserId,
            SocketId = _socketId
        };
        _notifyClosedId = _p2p.AddNotifyPeerConnectionClosed(ref closeOptions, null, OnConnectionClosed);
    }

    private void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
    {
        if (!EosP2PWire.SocketMatches(info.SocketId, _socketId))
            return;

        if (info.RemoteUserId != null && info.RemoteUserId.IsValid() && info.RemoteUserId.Equals(_hostUserId))
        {
            SetDisconnectReasonOnce("Host disconnected. Returning to menu.");
            IsConnected = false;
        }
    }

    public void PumpIncoming(int maxPackets = 128)
    {
#if EOS_SDK
        if (_disposed || !IsConnected)
            return;

        var processed = 0;
        try
        {
            while (processed < maxPackets &&
                   EosP2PWire.TryReceivePacket(_p2p, _localUserId, _socketId, out var peerId, out var msg))
            {
                processed++;
                if (peerId == null || !peerId.IsValid() || !peerId.Equals(_hostUserId))
                    continue;

                switch (msg.Type)
                {
                    case LanMessageType.PlayerState:
                        _playerStates.Enqueue(msg.PlayerState);
                        break;
                    case LanMessageType.BlockSet:
                        _blockSets.Enqueue(msg.BlockSet);
                        break;
                    case LanMessageType.ItemSpawn:
                        _itemSpawns.Enqueue(msg.ItemSpawn);
                        break;
                    case LanMessageType.ItemPickup:
                        _itemPickups.Enqueue(msg.ItemPickup);
                        break;
                    case LanMessageType.Chat:
                        _chatMessages.Enqueue(msg.Chat);
                        break;
                    case LanMessageType.PlayerList:
                        _playerLists.Enqueue(msg.PlayerList);
                        break;
                    case LanMessageType.ChunkData:
                        _chunkData.Enqueue(msg.ChunkData);
                        break;
                    case LanMessageType.WorldSyncComplete:
                        _worldSyncComplete.Enqueue(true);
                        break;
                    case LanMessageType.Teleport:
                        _teleports.Enqueue(msg.Teleport);
                        break;
                    case LanMessageType.PersistenceRestore:
                        _persistenceRestores.Enqueue(msg.PersistenceSnapshot);
                        break;
                    case LanMessageType.JoinDenied:
                        SetDisconnectReasonOnce(string.IsNullOrWhiteSpace(msg.JoinDeniedReason)
                            ? "Host denied join request."
                            : msg.JoinDeniedReason!);
                        IsConnected = false;
                        _log.Warn($"EOS join denied by host: {msg.JoinDeniedReason}");
                        break;
                    case LanMessageType.HostShutdown:
                        SetDisconnectReasonOnce(string.IsNullOrWhiteSpace(msg.DisconnectReason)
                            ? "Host disconnected. Returning to menu."
                            : msg.DisconnectReason!);
                        IsConnected = false;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _log.Warn($"EOS client pump failed: {ex.Message}");
                SetDisconnectReasonOnce("Connection to host lost.");
            }
        }
#endif
    }
    private static ProductUserId? ParseProductUserId(string joinInfo)
    {
        if (string.IsNullOrWhiteSpace(joinInfo))
            return null;

        var value = joinInfo.Trim();
        if (value.StartsWith("latticeveil://join/", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("latticeveil://join/".Length).Trim();
        if (value.StartsWith("puid=", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(5).Trim();

        return ProductUserId.FromString(value);
    }

    private void SetDisconnectReasonOnce(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectReasonSet, 1) != 0)
            return;

        var text = string.IsNullOrWhiteSpace(reason)
            ? "Host disconnected. Returning to menu."
            : reason.Trim();
        _disconnectReasons.Enqueue(text);
    }
#endif

    public void Dispose()
    {
#if EOS_SDK
        if (_disposed)
            return;

        _disposed = true;
        IsConnected = false;

        if (_notifyClosedId != 0)
            _p2p.RemoveNotifyPeerConnectionClosed(_notifyClosedId);

        var closeOptions = new CloseConnectionsOptions
        {
            LocalUserId = _localUserId,
            SocketId = _socketId
        };
        _p2p.CloseConnections(ref closeOptions);
#endif
    }
}

#if EOS_SDK
internal static class EosP2PWire
{
    private const byte Channel = 0;
    private const string SocketName = "rc_mp";

    public static SocketId CreateSocket()
    {
        var socket = new SocketId { SocketName = SocketName };
        return socket;
    }

    public static bool SocketMatches(SocketId? incoming, SocketId expected)
    {
        if (!incoming.HasValue)
            return false;
        return string.Equals(incoming.Value.SocketName, expected.SocketName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SocketMatches(SocketId incoming, SocketId expected)
    {
        return string.Equals(incoming.SocketName, expected.SocketName, StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] BuildHelloPayload(string username, string? gateTicket, Logger log)
    {
        return BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.Hello);
            bw.Write(username ?? "Player");
            bw.Write(gateTicket ?? string.Empty);
        }, log);
    }

    public static void SendWelcome(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, int playerId, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.Welcome);
            bw.Write(playerId);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendWorldInfo(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanWorldInfo info, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.WorldInfo);
            bw.Write(info.WorldName ?? "WORLD");
            bw.Write((byte)info.GameMode);
            bw.Write(info.Width);
            bw.Write(info.Height);
            bw.Write(info.Depth);
            bw.Write(info.Seed);
            bw.Write(info.PlayerCollision);
            bw.Write(info.WorldId ?? string.Empty);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendPlayerState(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanPlayerState state, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.PlayerState);
            bw.Write(state.PlayerId);
            bw.Write(state.Position.X);
            bw.Write(state.Position.Y);
            bw.Write(state.Position.Z);
            bw.Write(state.Yaw);
            bw.Write(state.Pitch);
            bw.Write(state.Status);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.UnreliableUnordered, log);
    }

    public static void SendBlockSet(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanBlockSet block, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.BlockSet);
            bw.Write(block.X);
            bw.Write(block.Y);
            bw.Write(block.Z);
            bw.Write(block.Id);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendItemSpawn(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanItemSpawn item, Logger log)
    {
        var payload = BuildPayload(bw =>
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
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendItemPickup(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanItemPickup pickup, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.ItemPickup);
            bw.Write(pickup.ItemId);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendJoinDenied(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, string? reason, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.JoinDenied);
            bw.Write(string.IsNullOrWhiteSpace(reason) ? "Host denied join request." : reason.Trim());
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendChat(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanChatMessage chat, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.Chat);
            bw.Write(chat.FromPlayerId);
            bw.Write(chat.ToPlayerId);
            bw.Write((byte)chat.Kind);
            bw.Write(chat.TimestampUtc);
            bw.Write(chat.Text ?? string.Empty);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendPersistenceSnapshot(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanPlayerPersistenceSnapshot snapshot, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.PersistenceSnapshot);
            bw.Write(snapshot.PlayerId);
            bw.Write(snapshot.Username ?? string.Empty);
            bw.Write(snapshot.TimestampUtc);
            var snapshotPayload = snapshot.Payload ?? Array.Empty<byte>();
            bw.Write(snapshotPayload.Length);
            bw.Write(snapshotPayload);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendPersistenceRestore(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanPlayerPersistenceSnapshot snapshot, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.PersistenceRestore);
            bw.Write(snapshot.PlayerId);
            bw.Write(snapshot.Username ?? string.Empty);
            bw.Write(snapshot.TimestampUtc);
            var snapshotPayload = snapshot.Payload ?? Array.Empty<byte>();
            bw.Write(snapshotPayload.Length);
            bw.Write(snapshotPayload);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendPlayerList(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanPlayerList list, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.PlayerList);
            var count = list.Players?.Length ?? 0;
            bw.Write(count);
            for (var i = 0; i < count; i++)
            {
                bw.Write(list.Players[i].PlayerId);
                bw.Write(list.Players[i].Name ?? string.Empty);
            }
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendChunkData(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanChunkData chunk, Logger log)
    {
        var payload = BuildPayload(bw =>
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
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendWorldSyncComplete(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.WorldSyncComplete);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendTeleport(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, LanTeleport teleport, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.Teleport);
            bw.Write(teleport.TargetPlayerId);
            bw.Write(teleport.X);
            bw.Write(teleport.Y);
            bw.Write(teleport.Z);
            bw.Write(teleport.Yaw);
            bw.Write(teleport.Pitch);
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static void SendHostShutdown(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, string reason, Logger log)
    {
        var payload = BuildPayload(bw =>
        {
            bw.Write((byte)LanMessageType.HostShutdown);
            bw.Write(string.IsNullOrWhiteSpace(reason) ? "Host disconnected." : reason.Trim());
        }, log);
        SendRaw(p2p, localUserId, remoteUserId, socketId, payload, PacketReliability.ReliableOrdered, log);
    }

    public static bool SendRaw(P2PInterface p2p, ProductUserId localUserId, ProductUserId remoteUserId, SocketId socketId, byte[] payload, PacketReliability reliability, Logger log)
    {
        var options = new SendPacketOptions
        {
            LocalUserId = localUserId,
            RemoteUserId = remoteUserId,
            SocketId = socketId,
            Channel = Channel,
            Data = new ArraySegment<byte>(payload),
            Reliability = reliability,
            AllowDelayedDelivery = false,
            DisableAutoAcceptConnection = false
        };

        var result = p2p.SendPacket(ref options);
        if (result != Result.Success)
        {
            log.Warn($"EOS send failed: {result}");
            return false;
        }

        return true;
    }

    public static bool TryReceivePacket(P2PInterface p2p, ProductUserId localUserId, SocketId socketId, out ProductUserId peerId, out LanMessage msg)
    {
        msg = default;
        peerId = null!;

        var sizeOptions = new GetNextReceivedPacketSizeOptions
        {
            LocalUserId = localUserId,
            RequestedChannel = Channel
        };

        var sizeResult = p2p.GetNextReceivedPacketSize(ref sizeOptions, out var size);
        if (sizeResult == Result.NotFound || size == 0)
            return false;

        if (sizeResult != Result.Success)
            return false;

        if (size > int.MaxValue)
            return false;

        var buffer = new byte[(int)size];
        var receiveOptions = new ReceivePacketOptions
        {
            LocalUserId = localUserId,
            MaxDataSizeBytes = size,
            RequestedChannel = Channel
        };

        var outSocket = new SocketId();
        byte outChannel;
        uint bytesWritten;
        var recvResult = p2p.ReceivePacket(ref receiveOptions, ref peerId, ref outSocket, out outChannel, new ArraySegment<byte>(buffer), out bytesWritten);
        if (recvResult != Result.Success || bytesWritten == 0)
            return false;

        if (!SocketMatches(outSocket, socketId))
            return false;

        var payload = bytesWritten == buffer.Length ? buffer : buffer.AsSpan(0, (int)bytesWritten).ToArray();
        return TryParseMessage(payload, out msg);
    }

    private static byte[] BuildPayload(Action<BinaryWriter> write, Logger log)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
            write(bw);

        var payload = ms.ToArray();
        if (payload.Length + 4 > P2PInterface.MAX_PACKET_SIZE)
        {
            log.Warn($"EOS payload too large: {payload.Length + 4} bytes.");
        }

        var len = BitConverter.GetBytes(payload.Length);
        using var wrapper = new MemoryStream();
        wrapper.Write(len, 0, len.Length);
        wrapper.Write(payload, 0, payload.Length);
        return wrapper.ToArray();
    }

    private static bool TryParseMessage(byte[] packet, out LanMessage msg)
    {
        msg = default;
        if (packet.Length < 5)
            return false;

        var len = BitConverter.ToInt32(packet, 0);
        if (len <= 0 || len > packet.Length - 4)
            return false;

        using var ms = new MemoryStream(packet, 4, len);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        var type = (LanMessageType)br.ReadByte();
        switch (type)
        {
            case LanMessageType.Welcome:
                msg = new LanMessage { Type = type, PlayerId = br.ReadInt32() };
                return true;
            case LanMessageType.WorldInfo:
                msg = new LanMessage
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
                        PlayerCollision = br.BaseStream.Position < br.BaseStream.Length ? br.ReadBoolean() : true,
                        WorldId = br.BaseStream.Position < br.BaseStream.Length ? br.ReadString() : string.Empty
                    }
                };
                return true;
            case LanMessageType.PlayerState:
                msg = new LanMessage
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
                return true;
            case LanMessageType.BlockSet:
                msg = new LanMessage
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
                return true;
            case LanMessageType.ItemSpawn:
                msg = new LanMessage
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
                return true;
            case LanMessageType.ItemPickup:
                msg = new LanMessage
                {
                    Type = type,
                    ItemPickup = new LanItemPickup
                    {
                        ItemId = br.ReadInt32()
                    }
                };
                return true;
            case LanMessageType.Chat:
                msg = new LanMessage
                {
                    Type = type,
                    Chat = new LanChatMessage
                    {
                        FromPlayerId = br.ReadInt32(),
                        ToPlayerId = br.ReadInt32(),
                        Kind = (LanChatKind)br.ReadByte(),
                        TimestampUtc = br.ReadInt64(),
                        Text = br.ReadString()
                    }
                };
                return true;
            case LanMessageType.PlayerList:
                var count = br.ReadInt32();
                if (count < 0 || count > 256)
                    return false;

                var players = new LanPlayerInfo[count];
                for (var i = 0; i < count; i++)
                {
                    players[i] = new LanPlayerInfo
                    {
                        PlayerId = br.ReadInt32(),
                        Name = br.ReadString()
                    };
                }

                msg = new LanMessage
                {
                    Type = type,
                    PlayerList = new LanPlayerList { Players = players }
                };
                return true;
            case LanMessageType.Hello:
                {
                    var name = br.ReadString();
                    string? gateTicket = null;
                    if (br.BaseStream.Position < br.BaseStream.Length)
                        gateTicket = br.ReadString();

                    msg = new LanMessage
                    {
                        Type = type,
                        Name = name,
                        GateTicket = gateTicket
                    };
                }
                return true;
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
                    msg = new LanMessage
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
                    return true;
                }
            case LanMessageType.WorldSyncComplete:
                msg = new LanMessage { Type = type, WorldSyncComplete = true };
                return true;
            case LanMessageType.JoinDenied:
                msg = new LanMessage
                {
                    Type = type,
                    JoinDeniedReason = br.ReadString()
                };
                return true;
            case LanMessageType.Teleport:
                msg = new LanMessage
                {
                    Type = type,
                    Teleport = new LanTeleport
                    {
                        TargetPlayerId = br.ReadInt32(),
                        X = br.ReadInt32(),
                        Y = br.ReadInt32(),
                        Z = br.ReadInt32(),
                        Yaw = br.ReadSingle(),
                        Pitch = br.ReadSingle()
                    }
                };
                return true;
            case LanMessageType.PersistenceSnapshot:
            case LanMessageType.PersistenceRestore:
                {
                    var playerId = br.ReadInt32();
                    var username = br.ReadString();
                    var timestampUtc = br.ReadInt64();
                    var payloadLen = br.ReadInt32();
                    if (payloadLen < 0 || payloadLen > 1024 * 1024)
                        return false;
                    var payload = br.ReadBytes(payloadLen);
                    if (payload.Length != payloadLen)
                        return false;

                    msg = new LanMessage
                    {
                        Type = type,
                        PersistenceSnapshot = new LanPlayerPersistenceSnapshot
                        {
                            PlayerId = playerId,
                            Username = username,
                            TimestampUtc = timestampUtc,
                            Payload = payload
                        }
                    };
                    return true;
                }
            case LanMessageType.HostShutdown:
                msg = new LanMessage
                {
                    Type = type,
                    DisconnectReason = br.ReadString()
                };
                return true;
            default:
                return false;
        }
    }
}
#endif
