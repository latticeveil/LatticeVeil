using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LatticeVeilMonoGame.Core;

public sealed class MicTester : IDisposable
{
    private readonly Logger _log;
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private WasapiOut? _playback;
    private WaveFormat? _format;
    private float _level;
    private float _peak;
    private bool _monitor;
    private string? _inputDeviceId;
    private string? _outputDeviceId;

    public MicTester(Logger log)
    {
        _log = log;
    }

    public bool IsRunning => _capture != null;

    public float Level => _level;

    public float Peak => _peak;

    public void Start(string? inputDeviceId, string? outputDeviceId, bool monitor)
    {
        lock (_lock)
        {
            StopInternal();
            _inputDeviceId = string.IsNullOrWhiteSpace(inputDeviceId) ? null : inputDeviceId;
            _outputDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;
            _monitor = monitor;

            var captureDevice = GetDevice(DataFlow.Capture, _inputDeviceId);
            if (captureDevice == null)
            {
                _log.Warn("Mic test: no capture device available.");
                return;
            }

            try
            {
                _capture = new WasapiCapture(captureDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _format = _capture.WaveFormat;

                if (_monitor)
                    StartPlayback(_outputDeviceId, _format);

                _capture.StartRecording();
                _log.Info("Mic test started.");
            }
            catch (Exception ex)
            {
                _log.Warn($"Mic test start failed: {ex.Message}");
                StopInternal();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    public void SetMonitor(bool enabled, string? outputDeviceId)
    {
        lock (_lock)
        {
            _monitor = enabled;
            _outputDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;

            if (_capture == null)
                return;

            if (enabled)
                StartPlayback(_outputDeviceId, _format ?? _capture.WaveFormat);
            else
                StopPlayback();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        var peak = ComputePeak(e.Buffer, e.BytesRecorded, _format ?? _capture?.WaveFormat);
        var previous = Volatile.Read(ref _peak);
        var decayed = previous * 0.92f;
        var newPeak = peak > decayed ? peak : decayed;

        Volatile.Write(ref _level, peak);
        Volatile.Write(ref _peak, newPeak);

        if (_monitor && _buffer != null)
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            _log.Warn($"Mic test stopped: {e.Exception.Message}");
    }

    private void StartPlayback(string? outputDeviceId, WaveFormat format)
    {
        StopPlayback();

        var outputDevice = GetDevice(DataFlow.Render, outputDeviceId);
        if (outputDevice == null)
        {
            _log.Warn("Mic test: no output device available for monitoring.");
            return;
        }

        try
        {
            _buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true
            };
            _playback = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 50);
            _playback.Init(_buffer);
            _playback.Play();
        }
        catch (Exception ex)
        {
            _log.Warn($"Mic monitor start failed: {ex.Message}");
            StopPlayback();
        }
    }

    private void StopPlayback()
    {
        if (_playback != null)
        {
            try { _playback.Stop(); } catch { }
            _playback.Dispose();
            _playback = null;
        }

        _buffer = null;
    }

    private void StopInternal()
    {
        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        StopPlayback();
        _format = null;
        Volatile.Write(ref _level, 0f);
        Volatile.Write(ref _peak, 0f);
    }

    private static MMDevice? GetDevice(DataFlow flow, string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    return enumerator.GetDevice(deviceId);
                }
                catch
                {
                    // Fall through to default.
                }
            }

            return enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
        }
        catch
        {
            return null;
        }
    }

    private static float ComputePeak(byte[] buffer, int bytes, WaveFormat? format)
    {
        if (format == null || bytes <= 0)
            return 0f;

        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.IeeeFloat)
            return ComputePeakFloat(buffer, bytes);

        if (format.BitsPerSample == 16)
            return ComputePeakInt16(buffer, bytes);

        if (format.BitsPerSample == 32)
            return ComputePeakInt32(buffer, bytes);

        return 0f;
    }

    private static float ComputePeakFloat(byte[] buffer, int bytes)
    {
        var peak = 0f;
        var samples = bytes / 4;
        for (int i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * 4);
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    private static float ComputePeakInt16(byte[] buffer, int bytes)
    {
        var peak = 0f;
        var samples = bytes / 2;
        for (int i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(buffer, i * 2);
            var abs = Math.Abs(sample / 32768f);
            if (abs > peak) peak = abs;
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    private static float ComputePeakInt32(byte[] buffer, int bytes)
    {
        var peak = 0f;
        var samples = bytes / 4;
        for (int i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt32(buffer, i * 4);
            var abs = Math.Abs(sample / (float)int.MaxValue);
            if (abs > peak) peak = abs;
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    public void Dispose()
    {
        Stop();
    }
}
