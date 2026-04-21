using System;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace OBSToABB;

public class EncounterManager : IDisposable
{
    /// <summary>Delay before saving the replay buffer after recording starts.</summary>
    private static readonly TimeSpan ReplayBufferSaveDelay = TimeSpan.FromSeconds(5);

    /// <summary>Grace period after combat ends before stopping the recording.</summary>
    private static readonly TimeSpan CombatEndGracePeriod = TimeSpan.FromSeconds(5);

    private readonly IOBSController _obs;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    private bool _isInCombat;
    private readonly object _lock = new();
    private bool _isDisposed;

    /// <summary>
    /// When true, combat-triggered recording is suppressed.
    /// Runtime-only (not persisted) - resets to false on plugin startup.
    /// Toggled via the /pto rec command.
    /// </summary>
    public bool IsStandby { get; set; }

    /// <summary>
    /// Tracks whether we initiated recording. Protected by <see cref="_lock"/>.
    /// Set true when we call StartRecording, false when we call StopRecording
    /// or determine we should not be recording.
    /// </summary>
    private bool _weStartedRecording;

    /// <summary>
    /// Kernel-backed timer for the grace period before stopping recording.
    /// Fires reliably regardless of thread pool pressure.
    /// </summary>
    private System.Timers.Timer? _gracePeriodTimer;

    private System.Timers.Timer? _replayBufferTimer;
    private long _combatStartTimeMs;
    public bool IsInCombat => _isInCombat;

    public event Action? EncounterStarted;
    public event Action? EncounterEnded;
    public event Action<string>? ErrorOccurred;
    public event Action? StateChanged;
    public event Action<long, long, string>? SyncDataBroadcast;

    public EncounterManager(IOBSController obs, ICondition condition, IPluginLog log)
    {
        _obs = obs;
        _condition = condition;
        _log = log;
        _obs.RecordingCompleted += OnRecordingCompleted;
    }

    private void OnRecordingCompleted(long recStartTimeMs, string filePath)
    {
        SyncDataBroadcast?.Invoke(_combatStartTimeMs, recStartTimeMs, filePath);
    }

    /// <summary>
    /// Call this every frame from the game thread (Framework.Update).
    /// Polls the Dalamud condition flag for combat state changes.
    /// </summary>
    public void Update()
    {
        var inCombat = _condition[ConditionFlag.InCombat];

        bool shouldStart = false;
        bool shouldEnd = false;
        bool fireStateChanged = false;

        lock (_lock)
        {
            if (_isDisposed) return;

            if (inCombat == _isInCombat) return;

            _log.Debug($"[Encounter] Combat state changed: inCombat={inCombat}, wasInCombat={_isInCombat}");

            _isInCombat = inCombat;
            fireStateChanged = true;

            // In standby mode, track combat state but skip recording actions.
            if (IsStandby)
            {
                _log.Debug("[Encounter] Standby mode active - skipping recording actions");
            }
            else if (inCombat)
            {
                // If we just loaded mid-combat, ensure we claim the session
                if (!_isInCombat && _obs.IsRecording)
                    _weStartedRecording = true;
                // Entering combat - cancel any pending stop
                CancelGracePeriodTimer();
                shouldStart = true;
                _combatStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _log.Debug("[Encounter] Entering combat, will start encounter");
            }
            else
            {
                shouldEnd = true;
                _log.Debug("[Encounter] Leaving combat, will end encounter");
            }
        }

        // Fire events outside the lock to avoid potential deadlocks
        if (fireStateChanged)
            StateChanged?.Invoke();

        if (shouldStart)
            HandleEncounterStart();
        else if (shouldEnd)
            HandleEncounterEnd();
    }

    /// <summary>
    /// Starts recording immediately (off the game thread via ThreadPool.QueueUserWorkItem)
    /// and schedules a replay buffer save after the configured delay.
    /// </summary>
    private void HandleEncounterStart()
    {
        lock (_lock)
        {
            if (_weStartedRecording)
            {
                _log.Debug("[Encounter] HandleEncounterStart: already in a recording session we started, skipping");
                return;
            }
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                // Re-check under lock - the grace period callback may have run between
                // our initial check and this thread pool work item executing.
                if (_weStartedRecording)
                {
                    _log.Debug("[Encounter] HandleEncounterStart: already in a recording session (re-check), skipping");
                    return;
                }
            }

            try
            {
                _log.Debug($"[Encounter] HandleEncounterStart: obs.IsConnected={_obs.IsConnected}");

                if (!_obs.IsConnected)
                {
                    _log.Warning("[Encounter] HandleEncounterStart: OBS not connected, aborting");
                    return;
                }

                _log.Debug("[Encounter] HandleEncounterStart: calling StartRecording");
                _obs.StartRecording();
                _log.Debug("[Encounter] HandleEncounterStart: StartRecording called successfully");

                lock (_lock)
                {
                    _weStartedRecording = true;
                }

                ScheduleReplayBufferSave();
                EncounterStarted?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error($"[Encounter] HandleEncounterStart exception: {ex}");
                ErrorOccurred?.Invoke($"Error starting encounter: {ex.Message}");
            }
        });
    }


    private void ScheduleReplayBufferSave()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            CancelReplayBufferTimer();
            _replayBufferTimer = new System.Timers.Timer(ReplayBufferSaveDelay.TotalMilliseconds) { AutoReset = false };
            _replayBufferTimer.Elapsed += (s, e) => {
                if (_obs.IsConnected && _obs.IsRecording && _obs.IsReplayBufferConfigured) _obs.SaveReplayBuffer();
            };
            _replayBufferTimer.Start();
        }
    }

    private void CancelReplayBufferTimer()
    {
        if (_replayBufferTimer != null)
        {
            _replayBufferTimer.Stop();
            _replayBufferTimer.Dispose();
            _replayBufferTimer = null;
        }
    }

    /// <summary>
    /// Starts the grace period timer. When it fires, the recording will be stopped
    /// (unless combat resumes first, which cancels the timer).
    /// </summary>
    private void HandleEncounterEnd()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            _log.Debug($"[Encounter] HandleEncounterEnd: obs.IsConnected={_obs.IsConnected}, obs.IsRecording={_obs.IsRecording}");

            if (!_obs.IsConnected || !_obs.IsRecording)
            {
                _log.Debug("[Encounter] HandleEncounterEnd: OBS not connected or not recording, firing EncounterEnded without stopping");
                _weStartedRecording = false;
                // Fire outside the lock below
            }
            else
            {
                _log.Debug("[Encounter] HandleEncounterEnd: starting grace period timer");

                CancelGracePeriodTimer();

                var timer = new System.Timers.Timer(CombatEndGracePeriod.TotalMilliseconds);
                timer.AutoReset = false;
                timer.Elapsed += OnGracePeriodElapsed;
                _gracePeriodTimer = timer;
                timer.Start();

                return;
            }
        }

        EncounterEnded?.Invoke();
    }

    /// <summary>
    /// Fires when the grace period has elapsed without combat resuming.
    /// Runs on a thread pool thread (fired by the kernel timer), then stops the recording.
    /// </summary>
    private void OnGracePeriodElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            // If we didn't start this recording, or combat resumed and cancelled us, bail out.
            if (!_weStartedRecording)
            {
                _log.Debug("[Encounter] HandleEncounterEnd: _weStartedRecording is false, skipping stop");
                return;
            }

            _weStartedRecording = false;
        }

        try
        {
            // Re-check after grace period - recording may have stopped externally
            if (!_obs.IsConnected || !_obs.IsRecording)
            {
                _log.Debug("[Encounter] HandleEncounterEnd: recording already stopped during grace period, firing EncounterEnded without stopping");
                EncounterEnded?.Invoke();
                return;
            }

            _log.Debug("[Encounter] HandleEncounterEnd: calling StopRecording");
            _obs.StopRecording();
            _log.Debug("[Encounter] HandleEncounterEnd: StopRecording called successfully");
            EncounterEnded?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"[Encounter] HandleEncounterEnd exception: {ex}");
            ErrorOccurred?.Invoke($"Error ending encounter: {ex.Message}");
        }
    }

    /// <summary>Cancels and disposes the grace period timer if active. Must be called under lock.</summary>
    private void CancelGracePeriodTimer()
    {
        if (_gracePeriodTimer != null)
        {
            _log.Debug("[Encounter] Grace period timer cancelled");
            _gracePeriodTimer.Stop();
            _gracePeriodTimer.Elapsed -= OnGracePeriodElapsed;
            _gracePeriodTimer.Dispose();
            _gracePeriodTimer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _obs.RecordingCompleted -= OnRecordingCompleted;
            CancelGracePeriodTimer();
        }
    }
}
