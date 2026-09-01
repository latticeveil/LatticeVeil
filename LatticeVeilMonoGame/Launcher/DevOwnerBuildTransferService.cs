using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

public sealed class DevOwnerRemoteBuildInfo
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public string FileName { get; init; } = "LatticeVeilMonoGame.exe";
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public enum DevOwnerTransferDirection
{
    Sending,
    Receiving
}

public sealed class DevOwnerTransferStatus
{
    public DevOwnerTransferDirection Direction { get; init; }
    public string PeerName { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public bool Complete { get; init; }
    public bool Failed { get; init; }
    public string Message { get; init; } = string.Empty;

    public float Progress => TotalBytes <= 0 ? 0f : Math.Clamp(BytesTransferred / (float)TotalBytes, 0f, 1f);
}

public static class DevOwnerBuildTransferService
{
    public const string ServeEnvironmentVariable = "LATTICEVEIL_DEV_OWNER_SERVE";
    public const string HostEnvironmentVariable = "LATTICEVEIL_DEV_OWNER_HOST";
    public const string PortEnvironmentVariable = "LATTICEVEIL_DEV_OWNER_PORT";
    private const int DefaultPort = 47741;
    private const int DiscoveryPort = 47742;
    private const string ProtocolMagic = "LV_DEV_BUILD_TRANSFER_2";
    private const string DiscoveryRequest = "LV_DEV_OWNER_DISCOVER_1";
    private const string DiscoveryResponse = "LV_DEV_OWNER_HERE_1";
    private const string ExecutableName = "LatticeVeilMonoGame.exe";
    private const string BuildKind = "build";
    private const string AssetsKind = "assets";
    private const string DefaultFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";

    private static CancellationTokenSource? _serverCts;
    private static Task? _serverTask;
    private static Task? _discoveryTask;

    public static event Action<DevOwnerTransferStatus>? TransferStatusChanged;

    private sealed class ProtectedVeilnetTokenEnvelope
    {
        public string PayloadBase64 { get; set; } = string.Empty;
    }

    private sealed class VeilnetTokenRecord
    {
        public string Username { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public static bool IsOwnerMachineCandidate() => Paths.IsDevBuild && LooksLikeOwnerMachine();

    public static bool HasVeilnetUsername(Logger log) => !string.IsNullOrWhiteSpace(ResolveVeilnetUsername(log));

    public static bool IsOwnerServerOnline => _serverTask != null && !_serverTask.IsCompleted;

    public static void TryStartOwnerServer(Logger log)
    {
        if (!Paths.IsDevBuild || (!IsEnabled(ServeEnvironmentVariable) && !LooksLikeOwnerMachine()))
            return;

        if (_serverTask != null)
            return;

        var ownerName = ResolveVeilnetUsername(log);
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            log.Warn("DEV OWNER build server disabled: Veilnet username cache is missing.");
            return;
        }

        var sourceExe = ResolveSourceExecutablePath();
        if (!File.Exists(sourceExe))
            log.Warn($"DEV OWNER build EXE missing at {sourceExe}; assets will still be served.");

        var port = ResolvePort();
        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunOwnerServerAsync(sourceExe, ownerName, port, log, _serverCts.Token));
        _discoveryTask = Task.Run(() => RunDiscoveryResponderAsync(ownerName, port, log, _serverCts.Token));
    }

    public static async Task<DevOwnerRemoteBuildInfo?> CheckRemoteAsync(string host, Logger log, CancellationToken ct)
    {
        return await RequestRemoteAsync(host, ResolvePort(), BuildKind, downloadPath: null, progress: null, log, ct).ConfigureAwait(false);
    }

    public static async Task<(DevOwnerRemoteBuildInfo Info, string DownloadedPath)> DownloadRemoteAsync(
        string host,
        string downloadPath,
        IProgress<float>? progress,
        Logger log,
        CancellationToken ct)
    {
        var info = await RequestRemoteAsync(host, ResolvePort(), BuildKind, downloadPath, progress, log, ct).ConfigureAwait(false);
        if (info == null)
            throw new IOException("DEV OWNER did not return build metadata.");

        return (info, downloadPath);
    }

    public static async Task<(DevOwnerRemoteBuildInfo Info, string DownloadedPath)> DownloadRemoteAssetsAsync(
        string host,
        string downloadPath,
        IProgress<float>? progress,
        Logger log,
        CancellationToken ct)
    {
        var info = await RequestRemoteAsync(host, ResolvePort(), AssetsKind, downloadPath, progress, log, ct).ConfigureAwait(false);
        if (info == null)
            throw new IOException("DEV OWNER did not return asset metadata.");

        return (info, downloadPath);
    }

    public static string ResolveConfiguredHost()
        => (Environment.GetEnvironmentVariable(HostEnvironmentVariable) ?? string.Empty).Trim();

    public static async Task<DevOwnerRemoteBuildInfo?> DiscoverOwnerAsync(Logger log, CancellationToken ct)
    {
        var username = ResolveVeilnetUsername(log);
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Sign into Veilnet before discovering DEV OWNER builds.");

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var payload = System.Text.Encoding.UTF8.GetBytes($"{DiscoveryRequest}|{username}");
        await udp.SendAsync(payload, payload.Length, "255.255.255.255", DiscoveryPort).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2.5));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var text = System.Text.Encoding.UTF8.GetString(result.Buffer);
                var parts = text.Split('|');
                if (parts.Length < 4 || !string.Equals(parts[0], DiscoveryResponse, StringComparison.Ordinal))
                    continue;

                if (!int.TryParse(parts[2], out var port) || port <= 0 || port > 65535)
                    continue;

                return new DevOwnerRemoteBuildInfo
                {
                    Host = result.RemoteEndPoint.Address.ToString(),
                    Port = port,
                    OwnerName = parts[1],
                    FileName = ExecutableName,
                    Size = 0,
                    Sha256 = parts[3]
                };
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static async Task RunOwnerServerAsync(string sourceExe, string ownerName, int port, Logger log, CancellationToken ct)
    {
        TcpListener? listener = null;
        string? upnpMappingId = null;
        try
        {
            // Try to use UPnP to automatically port forward
            upnpMappingId = await TryAddUpnpPortMappingAsync(port, log, ct).ConfigureAwait(false);
            if (upnpMappingId != null)
            {
                log.Info($"UPnP port mapping successful for port {port}");
            }
            else
            {
                log.Warn($"UPnP not available, manual port forwarding may be required for remote access");
            }

            listener = new TcpListener(IPAddress.Any, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            log.Info($"DEV OWNER build server listening on port {port}. source={sourceExe}");

            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, sourceExe, ownerName, log, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Warn($"DEV OWNER build server stopped: {ex.Message}");
        }
        finally
        {
            try { listener?.Stop(); } catch { }
            // Clean up UPnP mapping
            if (upnpMappingId != null)
            {
                _ = Task.Run(() => TryRemoveUpnpPortMappingAsync(upnpMappingId, port, log));
            }
        }
    }

    private static async Task RunDiscoveryResponderAsync(string ownerName, int port, Logger log, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(DiscoveryPort);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            log.Info($"DEV OWNER discovery responder listening on UDP {DiscoveryPort}.");
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = System.Text.Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(DiscoveryRequest, StringComparison.Ordinal))
                    continue;

                var reply = System.Text.Encoding.UTF8.GetBytes($"{DiscoveryResponse}|{ownerName}|{port}|{Environment.MachineName}");
                await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Warn($"DEV OWNER discovery responder stopped: {ex.Message}");
        }
    }

    private static async Task HandleClientAsync(TcpClient client, string sourceExe, string ownerName, Logger log, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 600000; // 10 minutes for large transfers
            client.SendTimeout = 600000;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            await using var stream = client.GetStream();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadString();
            var requesterName = reader.ReadString();
            var kind = reader.ReadString();
            var wantsBytes = reader.ReadBoolean();
            var requesterToken = reader.ReadString();
            if (!string.Equals(magic, ProtocolMagic, StringComparison.Ordinal))
            {
                writer.Write(false);
                writer.Write("Invalid DEV build request.");
                writer.Flush();
                return;
            }

            requesterName = string.IsNullOrWhiteSpace(requesterName) ? "Unknown Veilnet User" : requesterName.Trim();
            if (!await ValidateRequesterDevAccessAsync(requesterToken, requesterName, log, ct).ConfigureAwait(false))
            {
                writer.Write(false);
                writer.Write("This Veilnet account is not allowed to receive DEV files.");
                writer.Flush();
                return;
            }

            var transferPath = sourceExe;
            var transferName = ExecutableName;
            string? tempZip = null;
            if (string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase))
            {
                tempZip = await BuildAssetsZipAsync(log, ct).ConfigureAwait(false);
                transferPath = tempZip;
                transferName = "DevOwnerAssets.zip";
            }
            else if (!string.Equals(kind, BuildKind, StringComparison.OrdinalIgnoreCase))
            {
                writer.Write(false);
                writer.Write("Unknown DEV OWNER transfer request.");
                writer.Flush();
                return;
            }
            else if (!File.Exists(transferPath))
            {
                writer.Write(false);
                writer.Write($"DEV OWNER build EXE was not found: {transferPath}");
                writer.Flush();
                return;
            }

            var file = new FileInfo(transferPath);
            var hash = await ComputeSha256Async(transferPath, ct).ConfigureAwait(false);

            writer.Write(true);
            writer.Write(ownerName);
            writer.Write(transferName);
            writer.Write(file.Length);
            writer.Write(hash);
            writer.Flush();

            var noun = string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase) ? "DEV assets" : "DEV build";
            log.Info($"Sending {noun} to {requesterName}: {file.Length} bytes");
            if (!wantsBytes)
                return;

            RaiseStatus(new DevOwnerTransferStatus
            {
                Direction = DevOwnerTransferDirection.Sending,
                PeerName = requesterName,
                TotalBytes = file.Length,
                Message = $"Sending {noun} to {requesterName}"
            });

            var buffer = new byte[64 * 1024]; // Smaller buffer for better reliability
            long sent = 0;
            await using var fileStream = new FileStream(transferPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            while (true)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                // Write in smaller chunks with flushes
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                sent += read;

                // Send periodic keepalive
                if (sent % (1024 * 1024) == 0)
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }

                RaiseStatus(new DevOwnerTransferStatus
                {
                    Direction = DevOwnerTransferDirection.Sending,
                    PeerName = requesterName,
                    BytesTransferred = sent,
                    TotalBytes = file.Length,
                    Message = $"Sending {noun} to {requesterName}"
                });
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
            RaiseStatus(new DevOwnerTransferStatus
            {
                Direction = DevOwnerTransferDirection.Sending,
                PeerName = requesterName,
                BytesTransferred = file.Length,
                TotalBytes = file.Length,
                Complete = true,
                Message = $"Sent {noun} to {requesterName}"
            });
            log.Info($"Finished {noun} transfer to {requesterName}");
            if (!string.IsNullOrWhiteSpace(tempZip))
                TryDeleteFile(tempZip);
        }
        catch (IOException ex)
        {
            RaiseStatus(new DevOwnerTransferStatus
            {
                Direction = DevOwnerTransferDirection.Sending,
                Failed = true,
                Message = $"DEV build transfer failed (connection error): {ex.Message}"
            });
            log.Warn($"DEV build transfer failed (connection error): {ex.Message}");
        }
        catch (Exception ex)
        {
            RaiseStatus(new DevOwnerTransferStatus
            {
                Direction = DevOwnerTransferDirection.Sending,
                Failed = true,
                Message = $"DEV build transfer failed: {ex.Message}"
            });
            log.Warn($"DEV build transfer failed: {ex.Message}");
        }
    }

    private static async Task<DevOwnerRemoteBuildInfo?> RequestRemoteAsync(
        string host,
        int port,
        string kind,
        string? downloadPath,
        IProgress<float>? progress,
        Logger log,
        CancellationToken ct)
    {
        var username = ResolveVeilnetUsername(log);
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Sign into Veilnet before requesting DEV OWNER builds.");
        var token = ResolveVeilnetToken(log);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Sign into Veilnet before requesting DEV OWNER files.");

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(ProtocolMagic);
        writer.Write(username);
        writer.Write(kind);
        writer.Write(!string.IsNullOrWhiteSpace(downloadPath));
        writer.Write(token);
        writer.Flush();

        var ok = reader.ReadBoolean();
        if (!ok)
            throw new IOException(reader.ReadString());

        var info = new DevOwnerRemoteBuildInfo
        {
            Host = host,
            Port = port,
            OwnerName = reader.ReadString(),
            FileName = reader.ReadString(),
            Size = reader.ReadInt64(),
            Sha256 = reader.ReadString()
        };

        if (string.IsNullOrWhiteSpace(downloadPath))
            return info;

        var tempPath = downloadPath + ".part";
        TryDeleteFile(tempPath);
        TryDeleteFile(downloadPath);

        var buffer = new byte[1024 * 1024];
        long readTotal = 0;
        await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            while (readTotal < info.Size)
            {
                var wanted = (int)Math.Min(buffer.Length, info.Size - readTotal);
                var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read <= 0)
                    throw new EndOfStreamException(
                        string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase)
                            ? "DEV OWNER transfer ended before the asset download was complete."
                            : "DEV OWNER transfer ended before the EXE was complete.");

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;
                progress?.Report(Math.Clamp(readTotal / (float)Math.Max(1L, info.Size), 0f, 1f));
                RaiseStatus(new DevOwnerTransferStatus
                {
                    Direction = DevOwnerTransferDirection.Receiving,
                    PeerName = info.OwnerName,
                    BytesTransferred = readTotal,
                    TotalBytes = info.Size,
                    Message = string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase)
                        ? $"Downloading DEV assets from {info.OwnerName}"
                        : $"Downloading DEV build from {info.OwnerName}"
                });
            }
        }

        var actualHash = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
        if (!string.Equals(actualHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase)
                    ? "DEV OWNER asset hash did not match after download."
                    : "DEV OWNER build hash did not match after download.");

        File.Move(tempPath, downloadPath, true);
        progress?.Report(1f);
        RaiseStatus(new DevOwnerTransferStatus
        {
            Direction = DevOwnerTransferDirection.Receiving,
            PeerName = info.OwnerName,
            BytesTransferred = info.Size,
            TotalBytes = info.Size,
            Complete = true,
            Message = string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase)
                ? $"Downloaded DEV assets from {info.OwnerName}"
                : $"Downloaded DEV build from {info.OwnerName}"
        });
        log.Info(
            string.Equals(kind, AssetsKind, StringComparison.OrdinalIgnoreCase)
                ? $"Downloaded DEV assets from {info.OwnerName} at {host}:{port} to {downloadPath}"
                : $"Downloaded DEV build from {info.OwnerName} at {host}:{port} to {downloadPath}");
        return info;
    }

    private static string ResolveSourceExecutablePath()
    {
        var explicitExe = (Environment.GetEnvironmentVariable(DevOwnerBuildUpdater.SourceExeEnvironmentVariable) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return explicitExe;

        var sourceDir = (Environment.GetEnvironmentVariable(DevOwnerBuildUpdater.SourceDirEnvironmentVariable) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(sourceDir))
            return Path.Combine(sourceDir, ExecutableName);

        var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(processPath) ? processPath : Path.Combine(AppContext.BaseDirectory, ExecutableName);
    }

    private static async Task<string> BuildAssetsZipAsync(Logger log, CancellationToken ct)
    {
        var sourceDir = Paths.LocalAssetsDir;
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"DEV owner assets folder missing: {sourceDir}");

        Directory.CreateDirectory(GameReleaseUpdater.DownloadsDir);
        var zipPath = Path.Combine(GameReleaseUpdater.DownloadsDir, $"dev-owner-assets-{Guid.NewGuid():N}.zip");
        await Task.Run(() => ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false), ct)
            .ConfigureAwait(false);
        log.Info($"Prepared DEV owner assets zip: {zipPath}");
        return zipPath;
    }

    private static string ResolveVeilnetUsername(Logger log)
    {
        try
        {
            var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            var cache = VeilnetProfileCacheStore.Load(log);
            var fromCache = (cache?.Username ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromCache))
                return fromCache;

            var fromAuth = TryResolveUsernameFromLauncherAuth(log);
            if (!string.IsNullOrWhiteSpace(fromAuth))
                return fromAuth;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to resolve Veilnet username for DEV OWNER transfer: {ex.Message}");
        }

        return string.Empty;
    }

    private static string TryResolveUsernameFromLauncherAuth(Logger log)
    {
        try
        {
            if (!File.Exists(Paths.VeilnetLauncherAuthPath))
                return string.Empty;

            var envelopeData = LvcSerializer.Read(Paths.VeilnetLauncherAuthPath);
            var envelope = new ProtectedVeilnetTokenEnvelope();
            LvcSerializer.ApplyObject(envelope, envelopeData);
            if (string.IsNullOrWhiteSpace(envelope.PayloadBase64))
                return string.Empty;

            var protectedBytes = Convert.FromBase64String(envelope.PayloadBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var payload = Encoding.UTF8.GetString(bytes);
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, payload);
                var recordData = LvcSerializer.Read(tempPath);
                var record = new VeilnetTokenRecord();
                LvcSerializer.ApplyObject(record, recordData);
                return (record.Username ?? string.Empty).Trim();
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read saved Veilnet username for DEV OWNER transfer: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ResolveVeilnetToken(Logger log)
    {
        try
        {
            var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;

            var record = TryResolveLauncherAuthRecord(log);
            return (record?.Token ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to resolve Veilnet token for DEV OWNER transfer: {ex.Message}");
            return string.Empty;
        }
    }

    private static VeilnetTokenRecord? TryResolveLauncherAuthRecord(Logger log)
    {
        try
        {
            if (!File.Exists(Paths.VeilnetLauncherAuthPath))
                return null;

            var envelopeData = LvcSerializer.Read(Paths.VeilnetLauncherAuthPath);
            var envelope = new ProtectedVeilnetTokenEnvelope();
            LvcSerializer.ApplyObject(envelope, envelopeData);
            if (string.IsNullOrWhiteSpace(envelope.PayloadBase64))
                return null;

            var protectedBytes = Convert.FromBase64String(envelope.PayloadBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var payload = Encoding.UTF8.GetString(bytes);
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, payload);
                var recordData = LvcSerializer.Read(tempPath);
                var record = new VeilnetTokenRecord();
                LvcSerializer.ApplyObject(record, recordData);
                return record;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read saved Veilnet auth for DEV OWNER transfer: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> ValidateRequesterDevAccessAsync(string token, string requesterName, Logger log, CancellationToken ct)
    {
        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            log.Warn($"Denied DEV transfer to {requesterName}: missing Veilnet token.");
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ResolveFunctionsBaseUrl()}/dev-access")
            {
                Content = new StringContent("{\"action\":\"check\"}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"Denied DEV transfer to {requesterName}: DEV access check HTTP {(int)response.StatusCode}.");
                return false;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var allowed = root.TryGetProperty("allowed", out var allowedProp) && allowedProp.ValueKind == JsonValueKind.True;
            if (!allowed)
                log.Warn($"Denied DEV transfer to {requesterName}: account is not on the DEV access list.");
            return allowed;
        }
        catch (Exception ex)
        {
            log.Warn($"Denied DEV transfer to {requesterName}: DEV access check failed ({ex.Message}).");
            return false;
        }
    }

    private static string ResolveFunctionsBaseUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultFunctionsBaseUrl : fromEnv.TrimEnd('/');
    }

    private static int ResolvePort()
    {
        var raw = (Environment.GetEnvironmentVariable(PortEnvironmentVariable) ?? string.Empty).Trim();
        return int.TryParse(raw, out var port) && port > 0 && port <= 65535 ? port : DefaultPort;
    }

    private static bool IsEnabled(string name)
    {
        var raw = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeOwnerMachine()
    {
        try
        {
            return Directory.Exists(Paths.LocalAssetsDir)
                && Directory.Exists(Path.Combine(Paths.LocalAssetsDir, "textures", "blocks"))
                && Directory.Exists(Path.Combine(Paths.LocalAssetsDir, "textures", "menu"));
        }
        catch
        {
            return false;
        }
    }

    private static void RaiseStatus(DevOwnerTransferStatus status)
    {
        try { TransferStatusChanged?.Invoke(status); }
        catch { }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task<string?> TryAddUpnpPortMappingAsync(int port, Logger log, CancellationToken ct)
    {
        try
        {
            var localIp = await GetLocalIpAddressAsync().ConfigureAwait(false);
            if (localIp == null)
                return null;

            var serviceUrl = await DiscoverUpnpServiceUrlAsync(log, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serviceUrl))
                return null;

            var mappingId = Guid.NewGuid().ToString("N");
            var success = await SendUpnpAddPortMappingAsync(serviceUrl, localIp, port, mappingId, log, ct).ConfigureAwait(false);
            return success ? mappingId : null;
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP port mapping failed: {ex.Message}");
            return null;
        }
    }

    private static async Task TryRemoveUpnpPortMappingAsync(string mappingId, int port, Logger log)
    {
        try
        {
            var serviceUrl = await DiscoverUpnpServiceUrlAsync(log, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serviceUrl))
                return;

            await SendUpnpDeletePortMappingAsync(serviceUrl, port, log).ConfigureAwait(false);
            log.Info($"UPnP port mapping removed for port {port}");
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP port mapping removal failed: {ex.Message}");
        }
    }

    private static async Task<string?> DiscoverUpnpServiceUrlAsync(Logger log, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var searchMessage = "M-SEARCH * HTTP/1.1\r\n" +
                               "HOST: 239.255.255.250:1900\r\n" +
                               "MAN: \"ssdp:discover\"\r\n" +
                               "MX: 3\r\n" +
                               "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

            var bytes = System.Text.Encoding.UTF8.GetBytes(searchMessage);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            while (!timeoutCts.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync().ConfigureAwait(false);
                var response = System.Text.Encoding.UTF8.GetString(result.Buffer);

                if (response.Contains("urn:schemas-upnp-org:device:InternetGatewayDevice:1"))
                {
                    var locationMatch = System.Text.RegularExpressions.Regex.Match(response, "LOCATION: (.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (locationMatch.Success)
                    {
                        var location = locationMatch.Groups[1].Value.Trim();
                        var serviceUrl = await ExtractControlUrlAsync(location, log).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(serviceUrl))
                            return serviceUrl;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP discovery failed: {ex.Message}");
        }
        return null;
    }

    private static async Task<string?> ExtractControlUrlAsync(string location, Logger log)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync(location).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var serviceMatch = System.Text.RegularExpressions.Regex.Match(content, "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>.*?<controlURL>(.+?)</controlURL>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!serviceMatch.Success)
            {
                serviceMatch = System.Text.RegularExpressions.Regex.Match(content, "<serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>.*?<controlURL>(.+?)</controlURL>", System.Text.RegularExpressions.RegexOptions.Singleline);
            }

            if (serviceMatch.Success)
            {
                var controlUrl = serviceMatch.Groups[1].Value.Trim();
                var baseUrl = new Uri(location);
                var absoluteUrl = new Uri(baseUrl, controlUrl);
                return absoluteUrl.ToString();
            }
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP control URL extraction failed: {ex.Message}");
        }
        return null;
    }

    private static async Task<bool> SendUpnpAddPortMappingAsync(string serviceUrl, string localIp, int port, string mappingId, Logger log, CancellationToken ct)
    {
        try
        {
            var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:AddPortMapping xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{port}</NewExternalPort>
      <NewProtocol>TCP</NewProtocol>
      <NewInternalPort>{port}</NewInternalPort>
      <NewInternalClient>{localIp}</NewInternalClient>
      <NewEnabled>1</NewEnabled>
      <NewPortMappingDescription>LatticeVeil DEV Build Transfer</NewPortMappingDescription>
      <NewLeaseDuration>3600</NewLeaseDuration>
    </u:AddPortMapping>
  </s:Body>
</s:Envelope>";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = new StringContent(soapBody, System.Text.Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");

            var response = await httpClient.PostAsync(serviceUrl, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP AddPortMapping failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> SendUpnpDeletePortMappingAsync(string serviceUrl, int port, Logger log)
    {
        try
        {
            var soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:DeletePortMapping xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{port}</NewExternalPort>
      <NewProtocol>TCP</NewProtocol>
    </u:DeletePortMapping>
  </s:Body>
</s:Envelope>";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = new StringContent(soapBody, System.Text.Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:WANIPConnection:1#DeletePortMapping\"");

            var response = await httpClient.PostAsync(serviceUrl, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.Warn($"UPnP DeletePortMapping failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> GetLocalIpAddressAsync()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
            await socket.ConnectAsync("8.8.8.8", 80).ConfigureAwait(false);
            var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
            return endPoint?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }
}
