using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeilMonoGame.Core;

public sealed class LauncherOnlineHandshakeSession : IDisposable
{
    private readonly Logger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task<bool> _acceptTask;
    private bool _disposed;

    private LauncherOnlineHandshakeSession(Logger log, string pipeName, string token)
    {
        _log = log;
        PipeName = pipeName;
        Token = token;
        _acceptTask = AcceptAsync(_cts.Token);
    }

    public string PipeName { get; }
    public string Token { get; }

    public static LauncherOnlineHandshakeSession Create(Logger log)
    {
        var pipe = "lv_online_" + Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        return new LauncherOnlineHandshakeSession(log, pipe, token);
    }

    public async Task<bool> WaitForConfirmationAsync(int timeoutMs = 0)
    {
        var effectiveTimeout = timeoutMs <= 0
            ? OnlineLaunchHandshakeGuard.ResolveHandshakeTimeoutMs()
            : timeoutMs;
        var timeout = effectiveTimeout < 100 ? 100 : effectiveTimeout;
        var completed = await Task.WhenAny(_acceptTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != _acceptTask)
            return false;
        return await _acceptTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private async Task<bool> AcceptAsync(CancellationToken ct)
    {
        try
        {
            using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

            var received = await reader.ReadLineAsync().ConfigureAwait(false);
            var ok = string.Equals(received, Token, StringComparison.Ordinal);
            await writer.WriteLineAsync(ok ? "OK" : "DENY").ConfigureAwait(false);
            return ok;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"Launcher online handshake server failed: {ex.Message}");
            return false;
        }
    }
}

public static class OnlineLaunchHandshakeGuard
{
    public static int ResolveHandshakeTimeoutMs()
    {
        var configured = Environment.GetEnvironmentVariable("LV_LAUNCHER_HANDSHAKE_TIMEOUT_MS");
        if (int.TryParse(configured, out var parsed))
            return Math.Clamp(parsed, 5000, 300000);

        // Release single-file startup can be slower on first run.
        return 120000;
    }

    public static bool ValidateForGameStart(string? pipeName, string? token, Logger log)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token))
        {
            log.Warn("Online disabled: launcher handshake arguments missing.");
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName.Trim(), PipeDirection.InOut, PipeOptions.None);
            var connected = false;
            var started = DateTime.UtcNow;
            var timeoutMs = ResolveHandshakeTimeoutMs();
            var deadline = started.AddMilliseconds(timeoutMs);
            var connectTimeoutMs = Math.Clamp(timeoutMs / 20, 250, 1200);
            Exception? lastConnectError = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    client.Connect(connectTimeoutMs);
                    connected = true;
                    break;
                }
                catch (TimeoutException ex)
                {
                    lastConnectError = ex;
                    Thread.Sleep(150);
                }
                catch (IOException ex)
                {
                    lastConnectError = ex;
                    Thread.Sleep(150);
                }
            }

            if (!connected)
            {
                var reason = lastConnectError != null ? lastConnectError.Message : "connect timeout";
                log.Warn($"Online disabled: launcher handshake connect timeout after {timeoutMs}ms ({reason}).");
                return false;
            }

            using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

            writer.WriteLine(token.Trim());
            var response = reader.ReadLine();
            var ok = string.Equals(response, "OK", StringComparison.Ordinal);
            if (!ok)
                log.Warn("Online disabled: launcher handshake denied.");
            return ok;
        }
        catch (Exception ex)
        {
            log.Warn($"Online disabled: launcher handshake failed ({ex.Message}).");
            return false;
        }
    }
}
