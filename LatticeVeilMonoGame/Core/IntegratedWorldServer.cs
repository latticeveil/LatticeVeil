using System;

namespace LatticeVeilMonoGame.Core;

public sealed class IntegratedWorldServer : IDisposable
{
    private readonly Logger _log;
    private readonly VoxelWorld _world;
    private readonly string _worldPath;
    private readonly string _hostName;
    private bool _disposed;

    public IntegratedWorldServer(Logger log, VoxelWorld world, string worldPath, string hostName)
    {
        _log = log;
        _world = world;
        _worldPath = worldPath;
        _hostName = string.IsNullOrWhiteSpace(hostName) ? "LocalPlayer" : hostName.Trim();
    }

    public bool IsRunning { get; private set; }

    public string WorldName => _world.Meta?.Name ?? "WORLD";

    public void Start()
    {
        if (_disposed || IsRunning)
            return;

        IsRunning = true;
        var mode = _world.Meta?.CurrentWorldGameMode.ToString() ?? "Unknown";
        _log.Info($"INTEGRATED_SERVER_START world={WorldName} mode={mode} host={_hostName} path={_worldPath}");
    }

    public void Stop(string reason)
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
        _log.Info($"INTEGRATED_SERVER_STOP world={WorldName} reason={normalizedReason}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop("dispose");
        _disposed = true;
    }
}
