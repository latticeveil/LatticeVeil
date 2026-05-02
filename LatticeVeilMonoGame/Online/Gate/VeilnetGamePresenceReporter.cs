using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Online.Gate;

internal sealed class VeilnetGamePresenceReporter
{
    private readonly Logger _log;
    private readonly OnlineGateClient _gate;
    private readonly string _source;
    private readonly string _sessionId;
    private DateTime _nextHeartbeatUtc = DateTime.MinValue;
    private bool _heartbeatInFlight;
    private bool _clearRequested;
    private PresenceState _desiredState;
    private PresenceState _lastSentState;

    private static readonly TimeSpan SuccessInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    public VeilnetGamePresenceReporter(Logger log, string source)
    {
        _log = log;
        _gate = OnlineGateClient.GetOrCreate();
        _source = string.Equals(source, "game", StringComparison.OrdinalIgnoreCase) ? "game" : "launcher";
        _sessionId = _source;
        _desiredState = PresenceState.None;
        _lastSentState = PresenceState.None;
    }

    public void ReportLauncher()
    {
        Report(new PresenceState("LAUNCHER", false, false, string.Empty, string.Empty));
    }

    public void ReportOnline()
    {
        Report(new PresenceState("MENU", false, false, string.Empty, string.Empty));
    }

    public void ReportInGame(string? worldName, string? gameMode, bool isMultiplayer)
    {
        Report(new PresenceState("IN_WORLD", true, isMultiplayer, worldName, gameMode));
    }

    public void Clear()
    {
        _clearRequested = true;
        _desiredState = PresenceState.None;
        if (_heartbeatInFlight)
            return;

        _heartbeatInFlight = true;
        _clearRequested = false;
        _ = ClearAsync();
    }

    public void ClearBeforeExit(int timeoutMs = 1500)
    {
        try
        {
            _gate.ClearGamePresenceAsync(_source, _sessionId).Wait(timeoutMs);
        }
        catch (Exception ex)
        {
            _log.Warn($"Presence clear-before-exit failed ({_source}): {ex.Message}");
        }
        finally
        {
            _heartbeatInFlight = false;
            _clearRequested = false;
            _desiredState = PresenceState.None;
            _lastSentState = PresenceState.None;
            _nextHeartbeatUtc = DateTime.MinValue;
        }
    }

    private void Report(PresenceState state)
    {
        var accessToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        _clearRequested = false;
        _desiredState = state;
        var nowUtc = DateTime.UtcNow;
        if (_heartbeatInFlight)
            return;

        var changed = !_lastSentState.Equals(state);
        if (!changed && nowUtc < _nextHeartbeatUtc)
            return;

        _heartbeatInFlight = true;
        _ = SendAsync(state);
    }

    private async System.Threading.Tasks.Task SendAsync(PresenceState state)
    {
        try
        {
            var result = await _gate.UpsertGamePresenceAsync(
                _source,
                _sessionId,
                state.Status,
                state.IsInGame,
                state.IsMultiplayer,
                state.WorldName,
                state.GameMode).ConfigureAwait(false);
            if (result.Ok)
                _lastSentState = state;
            _nextHeartbeatUtc = DateTime.UtcNow.Add(result.Ok ? SuccessInterval : RetryInterval);
        }
        catch (Exception ex)
        {
            _log.Warn($"Presence heartbeat failed ({_source}): {ex.Message}");
            _nextHeartbeatUtc = DateTime.UtcNow.Add(RetryInterval);
        }
        finally
        {
            _heartbeatInFlight = false;
            ContinuePendingWork(state);
        }
    }

    private async System.Threading.Tasks.Task ClearAsync()
    {
        try
        {
            await _gate.ClearGamePresenceAsync(_source, _sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Presence clear failed ({_source}): {ex.Message}");
        }
        finally
        {
            _heartbeatInFlight = false;
            _lastSentState = PresenceState.None;
            _nextHeartbeatUtc = DateTime.MinValue;
            ContinuePendingWork(PresenceState.None);
        }
    }

    private void ContinuePendingWork(PresenceState completedState)
    {
        if (_heartbeatInFlight)
            return;
        if (_clearRequested)
        {
            _heartbeatInFlight = true;
            _clearRequested = false;
            _ = ClearAsync();
            return;
        }

        if (_desiredState.Equals(PresenceState.None) || _desiredState.Equals(completedState))
            return;

        _heartbeatInFlight = true;
        _ = SendAsync(_desiredState);
    }

    private readonly record struct PresenceState(
        string Status,
        bool IsInGame,
        bool IsMultiplayer,
        string? WorldName,
        string? GameMode)
    {
        public static PresenceState None => new(string.Empty, false, false, string.Empty, string.Empty);
    }
}
