using System;

namespace OpenIPC.Viewer.Core.Video;

// Exponential reconnect backoff for AutoReconnectingVideoSession (Phase 12.3).
// attempt is 1-based; the delay doubles each attempt and caps at MaxSeconds:
// 1→1s, 2→2s, 3→4s, 4→8s, 5→16s, 6+→30s. A successful frame resets attempt.
public static class ReconnectBackoff
{
    public const double MaxSeconds = 30;

    public static TimeSpan Delay(int attempt)
    {
        if (attempt < 1) attempt = 1;
        var seconds = Math.Min(Math.Pow(2, attempt - 1), MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
