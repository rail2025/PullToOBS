using System;
using System.Threading.Tasks;

namespace OBSToABB;

public interface IOBSController : IDisposable
{
    bool IsConnected { get; }
    bool IsRecording { get; }
    bool IsReplayBufferActive { get; }
    bool IsReplayBufferConfigured { get; }

    event Action? ConnectionStateChanged;
    event Action? RecordingStateChanged;
    event Action? ReplayBufferStateChanged;
    event Action<string>? ErrorOccurred;

    event Action<long, string>? RecordingCompleted;

    Task ConnectAsync(string url, string password);
    void Disconnect();
    void StartReplayBuffer();
    void StopReplayBuffer();
    void SaveReplayBuffer();
    void StartRecording();
    void StopRecording();
}
