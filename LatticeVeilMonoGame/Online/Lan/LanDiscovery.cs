using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Lan;

public sealed class LanDiscovery : IDisposable
{
    private const int DefaultDiscoveryPort = 27036;
    private const int BeaconIntervalMs = 1000;
    private static readonly TimeSpan ServerTtl = TimeSpan.FromSeconds(3);

    private readonly Logger _log;
    private readonly int _port;
    private readonly Dictionary<string, LanServerEntry> _servers = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _listenCts;
    private CancellationTokenSource? _broadcastCts;

    public LanDiscovery(Logger log, int port = DefaultDiscoveryPort)
    {
        _log = log;
        _port = port;
    }

    public void StartListening()
    {
        if (_listenCts != null)
            return;

        _listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_listenCts.Token));
    }

    public void StopListening()
    {
        _listenCts?.Cancel();
        _listenCts = null;
    }

    public void StartBroadcast(string serverName, string gameVersion, int serverPort, string gameMode = "Survival")
    {
        StopBroadcast();
        _broadcastCts = new CancellationTokenSource();
        var safeName = Sanitize(serverName);
        var safeVersion = Sanitize(gameVersion);
        var safeMode = Sanitize(gameMode);
        _ = Task.Run(() => BroadcastLoop(safeName, safeVersion, serverPort, safeMode, _broadcastCts.Token));
    }

    public void StopBroadcast()
    {
        _broadcastCts?.Cancel();
        _broadcastCts = null;
    }

    public IReadOnlyList<LanServerEntry> GetServers()
    {
        var now = DateTime.UtcNow;
        var list = new List<LanServerEntry>();
        lock (_lock)
        {
            var stale = new List<string>();
            foreach (var pair in _servers)
            {
                if (now - pair.Value.LastSeenUtc > ServerTtl)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                list.Add(pair.Value);
            }

            foreach (var key in stale)
                _servers.Remove(key);
        }
        return list;
    }

    private async Task ListenLoop(CancellationToken token)
    {
        try
        {
            using var client = CreateListenClient(_port);
            while (!token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(token);
                if (!TryParseBeacon(result.Buffer, out var entry))
                    continue;

                entry.Endpoint = new IPEndPoint(result.RemoteEndPoint.Address, entry.ServerPort);
                entry.Host = result.RemoteEndPoint.Address.ToString();
                entry.LastSeenUtc = DateTime.UtcNow;

                var key = $"{entry.Endpoint.Address}:{entry.ServerPort}";
                lock (_lock)
                {
                    _servers[key] = entry;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"LAN discovery listen failed: {ex.Message}");
        }
    }

    private static UdpClient CreateListenClient(int port)
    {
        // On Windows, binding UDP ports can fail if another process has the port open.
        // We try to enable reuse to make local multi-instance testing easier.
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.ExclusiveAddressUse = false;
        }
        catch
        {
            // Not critical; best-effort.
        }

        sock.Bind(new IPEndPoint(IPAddress.Any, port));
        var client = new UdpClient { Client = sock, EnableBroadcast = true };
        return client;
    }

    private async Task BroadcastLoop(string serverName, string gameVersion, int serverPort, string gameMode, CancellationToken token)
    {
        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            var endpoint = new IPEndPoint(IPAddress.Broadcast, _port);
            var payload = BuildBeacon(serverName, gameVersion, serverPort, gameMode);

            while (!token.IsCancellationRequested)
            {
                await client.SendAsync(payload, payload.Length, endpoint);
                await Task.Delay(BeaconIntervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"LAN discovery broadcast failed: {ex.Message}");
        }
    }

    private static byte[] BuildBeacon(string serverName, string gameVersion, int serverPort, string gameMode)
    {
        var text = $"{serverName}|{gameVersion}|{serverPort}|{gameMode}";
        return Encoding.UTF8.GetBytes(text);
    }

    private static bool TryParseBeacon(byte[] data, out LanServerEntry entry)
    {
        entry = new LanServerEntry();
        var text = Encoding.UTF8.GetString(data);
        var parts = text.Split('|');
        if (parts.Length < 3)
            return false;

        if (!int.TryParse(parts[2], out var port))
            return false;

        entry.ServerName = parts[0].Trim();
        entry.GameVersion = parts[1].Trim();
        entry.ServerPort = port;
        if (parts.Length >= 4)
            entry.GameMode = parts[3].Trim();

        return true;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "server";

        return value.Replace("|", "/").Trim();
    }

    public void Dispose()
    {
        StopBroadcast();
        StopListening();
    }
}

public sealed class LanServerEntry
{
    public string ServerName { get; set; } = "server";
    public string GameVersion { get; set; } = "dev";
    public string GameMode { get; set; } = "Survival";
    public int ServerPort { get; set; } = 0;
    public string Host { get; set; } = "";
    public IPEndPoint? Endpoint { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
