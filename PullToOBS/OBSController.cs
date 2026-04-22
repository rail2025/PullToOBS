using Dalamud.Plugin.Services;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using OBSWebsocketDotNet.Types;
using OBSWebsocketDotNet.Types.Events;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace OBSToABB;

public class OBSController : IOBSController
{
    private const int StatePollingIntervalMs = 500;
    private const int PollFailureEscalationThreshold = 10;

    private readonly OBSWebsocket _obs;
    private readonly IPluginLog _log;
    private bool _isDisposed;
    private System.Timers.Timer? _statePollingTimer;

    // Tracks consecutive polling failures for escalation.
    private int _consecutivePollFailures;

    // Volatile ensures cross-thread visibility (timer thread writes, UI thread reads).
    private volatile bool _isRecording;
    private volatile bool _isReplayBufferActive;
    private volatile bool _isReplayBufferConfigured;
    private long _recordingStartTimeMs;

    public bool IsConnected => _obs.IsConnected;
    public bool IsRecording => _isRecording;
    public bool IsReplayBufferActive => _isReplayBufferActive;
    public bool IsReplayBufferConfigured => _isReplayBufferConfigured;

    public event Action? ConnectionStateChanged;
    public event Action? RecordingStateChanged;
    public event Action? ReplayBufferStateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<long, string>? RecordingCompleted;

    public OBSController(IPluginLog log)
    {
        _log = log;
        _obs = new OBSWebsocket();
        _obs.Connected += OnConnected;
        _obs.Disconnected += OnDisconnected;
        _obs.RecordStateChanged += OnRecordStateChanged;
    }

    private void OnRecordStateChanged(object? sender, RecordStateChangedEventArgs e)
    {
        string stateStr = "";
        try
        {
            // Dynamically extract all properties and their values into a single text string
            var props = e.OutputState.GetType().GetProperties();
            var propStrings = System.Linq.Enumerable.Select(props, p => $"{p.Name}={p.GetValue(e.OutputState)}");
            stateStr = string.Join(", ", propStrings);
        }
        catch { }

        _log.Debug($"[OBS] RecordStateChanged evaluated properties: '{stateStr}'");

        if (stateStr.Contains("Stopped", StringComparison.OrdinalIgnoreCase) ||
            stateStr.Contains("OBS_WEBSOCKET_OUTPUT_STOPPED", StringComparison.OrdinalIgnoreCase) ||
            stateStr.Contains("Idle", StringComparison.OrdinalIgnoreCase) ||
            stateStr.Contains("False", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string recordDir = _obs.GetRecordDirectory();
                if (System.IO.Directory.Exists(recordDir))
                {
                    var dirInfo = new System.IO.DirectoryInfo(recordDir);
                    var files = dirInfo.GetFiles("*.*", System.IO.SearchOption.TopDirectoryOnly);
                    System.IO.FileInfo? latestFile = null;

                    foreach (var f in files)
                    {
                        if (f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            if (latestFile == null || f.LastWriteTime > latestFile.LastWriteTime)
                            {
                                latestFile = f;
                            }
                        }
                    }

                    if (latestFile != null)
                    {
                        var recentFiles = files.OrderByDescending(f => f.LastWriteTime).Take(2).ToList();
                        if (recentFiles.Count == 2 && recentFiles[1].Name.Contains("Replay", StringComparison.OrdinalIgnoreCase))
                        {
                            var recordFile = recentFiles[0].FullName;
                            var bufferFile = recentFiles[1].FullName;
                            var outputFile = System.IO.Path.Combine(recordDir, $"Stitched_{DateTime.Now:yyyyMMdd_HHmmss}{recentFiles[0].Extension}");

                            string listFile = System.IO.Path.Combine(recordDir, "concat.txt");
                            System.IO.File.WriteAllText(listFile, $"file '{bufferFile.Replace("\\", "/")}'\nfile '{recordFile.Replace("\\", "/")}'");

                            long currentRecStartMs = _recordingStartTimeMs;

                            Task.Run(async () =>
                            {
                                // Race Condition Mitigation: Wait for OBS to release file locks
                                await Task.Delay(1000);

                                try
                                {
                                    _log.Debug($"[OBS] Starting FFmpeg stitch: {outputFile}");

                                    var psi = new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "ffmpeg",
                                        Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputFile}\"",
                                        CreateNoWindow = true,
                                        UseShellExecute = false,
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = true
                                    };

                                    using var process = System.Diagnostics.Process.Start(psi);
                                    if (process != null)
                                    {
                                        // Capture logs from the FFmpeg process (FFmpeg logs to Error stream by default)
                                        string stdout = await process.StandardOutput.ReadToEndAsync();
                                        string stderr = await process.StandardError.ReadToEndAsync();

                                        await process.WaitForExitAsync();

                                        if (process.ExitCode == 0)
                                        {
                                            _log.Information("[OBS] FFmpeg stitch successful.");
                                            System.IO.File.Delete(listFile);
                                            System.IO.File.Delete(recordFile);
                                            System.IO.File.Delete(bufferFile);
                                            RecordingCompleted?.Invoke(currentRecStartMs, outputFile);
                                        }
                                        else
                                        {
                                            _log.Error($"[OBS] FFmpeg failed with exit code {process.ExitCode}");
                                            _log.Error($"[OBS] FFmpeg Output: {stdout}");
                                            _log.Error($"[OBS] FFmpeg Error: {stderr}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _log.Error($"[OBS] Stitching process exception: {ex.Message}");
                                }
                            });
                        }
                        else
                        {
                            RecordingCompleted?.Invoke(_recordingStartTimeMs, latestFile.FullName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[OBS] Failed to get recording path: {ex.Message}");
            }
        }
    }

    public async Task ConnectAsync(string url, string password)
    {
        if (_obs.IsConnected)
            Disconnect();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConnected(object? s, EventArgs e) => tcs.TrySetResult(true);
        void OnDisconnected(object? s, ObsDisconnectionInfo e) =>
            tcs.TrySetException(new Exception(e.DisconnectReason ?? "Connection failed"));

        _obs.Connected += OnConnected;
        _obs.Disconnected += OnDisconnected;

        try
        {
            _log.Information($"[OBS] Connecting to {url} (password {(string.IsNullOrEmpty(password) ? "not set" : "set")})...");
            _obs.ConnectAsync(url, password);
            await tcs.Task;

            CheckReplayBufferConfiguration();
            if (_isReplayBufferConfigured)
                TryStartReplayBuffer();
            StartStatePolling();
            _log.Information("[OBS] Connected to OBS successfully");
        }
        catch (Exception ex)
        {
            _log.Error($"[OBS] Failed to connect: {ex}");
            ErrorOccurred?.Invoke($"Failed to connect to OBS: {ex.Message}");
            throw;
        }
        finally
        {
            _obs.Connected -= OnConnected;
            _obs.Disconnected -= OnDisconnected;
        }
    }

    private void StartStatePolling()
    {
        _statePollingTimer = new System.Timers.Timer(StatePollingIntervalMs);
        _statePollingTimer.Elapsed += (_, _) => PollState();
        _statePollingTimer.Start();
    }

    private void PollState()
    {
        if (!_obs.IsConnected) return;

        bool anyFailure = false;

        try
        {
            var recordStatus = _obs.GetRecordStatus();
            var wasRecording = _isRecording;
            _isRecording = recordStatus.IsRecording;
            if (wasRecording != _isRecording)
                RecordingStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Debug($"[OBS] PollState recording error: {ex.GetType().Name}: {ex.Message}");
            anyFailure = true;
        }

        try
        {
            var wasActive = _isReplayBufferActive;
            _isReplayBufferActive = _obs.GetReplayBufferStatus();
            if (wasActive != _isReplayBufferActive)
                ReplayBufferStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Debug($"[OBS] PollState replay buffer error: {ex.GetType().Name}: {ex.Message}");
            anyFailure = true;
        }

        if (anyFailure)
        {
            _consecutivePollFailures++;
            if (_consecutivePollFailures >= PollFailureEscalationThreshold)
            {
                _log.Warning($"[OBS] State polling has failed {_consecutivePollFailures} consecutive times, OBS may be unreachable");
                ErrorOccurred?.Invoke("OBS state polling is failing repeatedly — OBS may be unreachable");
                _consecutivePollFailures = 0;
            }
        }
        else
        {
            _consecutivePollFailures = 0;
        }
    }

    public void Disconnect()
    {
        _log.Information("[OBS] Disconnecting from OBS (user-initiated)");
        StopStatePolling();

        if (_obs.IsConnected)
            _obs.Disconnect();
    }

    private void StopStatePolling()
    {
        _statePollingTimer?.Stop();
        _statePollingTimer?.Dispose();
        _statePollingTimer = null;
    }

    private void CheckReplayBufferConfiguration()
    {
        try
        {
            var isActive = _obs.GetReplayBufferStatus();
            _isReplayBufferConfigured = true;
            _isReplayBufferActive = isActive;
        }
        catch (Exception ex)
        {
            _log.Debug($"[OBS] CheckReplayBufferConfiguration: not configured ({ex.GetType().Name}: {ex.Message})");
            _isReplayBufferConfigured = false;
            _isReplayBufferActive = false;
        }
    }

    private void TryStartReplayBuffer()
    {
        if (_isReplayBufferActive) return;

        try
        {
            _obs.StartReplayBuffer();
            _isReplayBufferActive = true;
            ReplayBufferStateChanged?.Invoke();
            _log.Debug("[OBS] TryStartReplayBuffer: started successfully");
        }
        catch (Exception ex) when (IsAlreadyRunningError(ex))
        {
            _log.Debug("[OBS] TryStartReplayBuffer: replay buffer was already running");
            _isReplayBufferActive = true;
            ReplayBufferStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning($"[OBS] TryStartReplayBuffer: could not auto-start: {ex.Message}");
            ErrorOccurred?.Invoke($"Could not auto-start replay buffer: {ex.Message}");
        }
    }

    public void StartReplayBuffer()
    {
        ExecuteObsAction(
            "StartReplayBuffer",
            () =>
            {
                _obs.StartReplayBuffer();
                _isReplayBufferActive = true;
                ReplayBufferStateChanged?.Invoke();
            });
    }

    public void StopReplayBuffer()
    {
        ExecuteObsAction(
            "StopReplayBuffer",
            () =>
            {
                _obs.StopReplayBuffer();
                _isReplayBufferActive = false;
                ReplayBufferStateChanged?.Invoke();
            });
    }

    public void SaveReplayBuffer()
    {
        _log.Debug($"[OBS] SaveReplayBuffer called: IsConnected={_obs.IsConnected}, IsReplayBufferActive={_isReplayBufferActive}");
        ExecuteObsAction(
            "SaveReplayBuffer",
            () => _obs.SaveReplayBuffer());
    }

    public void StartRecording()
    {
        _log.Debug($"[OBS] StartRecording called: IsConnected={_obs.IsConnected}, IsRecording={_isRecording}");
        ExecuteObsAction(
            "StartRecording",
            () =>
            {
                try
                {
                    _obs.StartRecord();
                }
                catch (Exception ex) when (IsAlreadyRunningError(ex))
                {
                    _log.Debug("[OBS] StartRecording: recording was already running (500), treating as success");
                }
                _isRecording = true;
                _recordingStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RecordingStateChanged?.Invoke();
            });
    }

    public void StopRecording()
    {
        _log.Debug($"[OBS] StopRecording called: IsConnected={_obs.IsConnected}, IsRecording={_isRecording}");

        if (!_obs.IsConnected)
        {
            _log.Debug("[OBS] StopRecording: not connected, skipping");
            return;
        }

        try
        {
            _obs.StopRecord();
            _isRecording = false;
            RecordingStateChanged?.Invoke();
            _log.Debug("[OBS] StopRecording: succeeded");
        }
        catch (Exception ex) when (IsNotRecordingError(ex))
        {
            _log.Debug("[OBS] StopRecording: recording was already stopped (501), treating as success");
            _isRecording = false;
            RecordingStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"[OBS] StopRecording failed: {ex}");
            ErrorOccurred?.Invoke($"Failed to StopRecording: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Executes an OBS action with standard connection check, logging, and error handling.
    /// </summary>
    private void ExecuteObsAction(string operationName, Action action)
    {
        if (!_obs.IsConnected)
        {
            _log.Debug($"[OBS] {operationName}: not connected, skipping");
            return;
        }

        try
        {
            action();
            _log.Debug($"[OBS] {operationName}: succeeded");
        }
        catch (Exception ex)
        {
            _log.Error($"[OBS] {operationName} failed: {ex}");
            ErrorOccurred?.Invoke($"Failed to {operationName}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Checks if the exception indicates the resource is already in the requested state.
    /// OBS WebSocket uses error code 500 for "already running" conditions.
    /// </summary>
    private static bool IsAlreadyRunningError(Exception ex)
    {
        // obs-websocket-dotnet wraps the error code in the message.
        // Check both the message and inner exception for robustness.
        return ex.Message.Contains("500") ||
               (ex.InnerException?.Message.Contains("500") ?? false);
    }

    /// <summary>
    /// Checks if the exception indicates the output is not active.
    /// OBS WebSocket uses error code 501 for "not recording/streaming" conditions.
    /// </summary>
    private static bool IsNotRecordingError(Exception ex)
    {
        return ex.Message.Contains("501") ||
               (ex.InnerException?.Message.Contains("501") ?? false);
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        _log.Debug("[OBS] WebSocket connected event received");
        ConnectionStateChanged?.Invoke();
    }

    private void OnDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        _log.Warning($"[OBS] Disconnected from OBS. Reason: {e.DisconnectReason ?? "unknown"}");
        _isRecording = false;
        _isReplayBufferActive = false;
        _consecutivePollFailures = 0;
        StopStatePolling();
        ConnectionStateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        StopStatePolling();

        _obs.Connected -= OnConnected;
        _obs.Disconnected -= OnDisconnected;
        _obs.RecordStateChanged -= OnRecordStateChanged;

        if (_obs.IsConnected)
            _obs.Disconnect();

        _isDisposed = true;
    }
}
