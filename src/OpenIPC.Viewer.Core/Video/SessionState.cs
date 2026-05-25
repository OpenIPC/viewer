namespace OpenIPC.Viewer.Core.Video;

public enum SessionState
{
    Idle = 0,
    Connecting,
    Playing,
    Paused,
    Failed,
    Reconnecting,
}
