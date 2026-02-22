using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Lan;

public sealed class LocalLanServer : IDisposable
{
    private readonly Logger _log;
    private readonly List<LocalLanConnection> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;
    public event Action<LocalLanConnection>? ClientConnected;

    public LocalLanServer(Logger log)
    {
        _log = log;
    }

    public void Start(int port)
    {
        if (IsRunning)
            return;

        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _log.Info($"Local LAN server listening on {port}.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        try { _listener?.Stop(); }
        catch { }
        _listener = null;

        lock (_connections)
        {
            foreach (var conn in _connections)
                conn.Dispose();
            _connections.Clear();
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                var conn = new LocalLanConnection(client);
                lock (_connections)
                    _connections.Add(conn);
                ClientConnected?.Invoke(conn);
                _log.Info($"LAN client connected: {conn.RemoteEndPoint}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"LAN server accept failed: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}

public sealed class LocalLanClient
{
    public async Task<LocalLanConnection> ConnectAsync(string host, int port, CancellationToken token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, token);
        return new LocalLanConnection(client);
    }
}

public sealed class LocalLanConnection : IDisposable
{
    public TcpClient Client { get; }
    public EndPoint? RemoteEndPoint => Client.Client.RemoteEndPoint;

    public LocalLanConnection(TcpClient client)
    {
        Client = client;
    }

    public void Dispose()
    {
        try { Client.Close(); }
        catch { }
    }
}
