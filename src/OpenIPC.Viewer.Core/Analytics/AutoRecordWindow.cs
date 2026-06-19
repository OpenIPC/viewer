using System;

namespace OpenIPC.Viewer.Core.Analytics;

public enum AutoRecordAction
{
    None = 0,
    Start,
    Stop,
}

// Smart auto-stop state machine (Phase 15.6). Recording starts on the first
// detection and keeps running until postEventSeconds elapse with no new
// detection; each detection extends the window. A cooldown after a stop avoids
// start/stop flapping on a lone stray detection. Pure (clock passed in) so the
// extend/stop/cooldown behaviour unit-tests deterministically.
public sealed class AutoRecordWindow
{
    private readonly TimeSpan _postEvent;
    private readonly TimeSpan _cooldown;
    private DateTime _lastDetection = DateTime.MinValue;
    private DateTime _stoppedAt = DateTime.MinValue;
    private bool _active;

    public AutoRecordWindow(int postEventSeconds, int cooldownSeconds = 0)
    {
        if (postEventSeconds < 1) postEventSeconds = 1;
        _postEvent = TimeSpan.FromSeconds(postEventSeconds);
        _cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
    }

    public bool IsActive => _active;

    public AutoRecordAction OnDetection(DateTime now)
    {
        // During the post-stop cooldown, ignore detections so a single stray
        // hit can't immediately restart recording.
        if (!_active && _stoppedAt != DateTime.MinValue && now - _stoppedAt < _cooldown)
            return AutoRecordAction.None;

        _lastDetection = now;
        if (_active) return AutoRecordAction.None;
        _active = true;
        return AutoRecordAction.Start;
    }

    public AutoRecordAction OnTick(DateTime now)
    {
        if (_active && now - _lastDetection >= _postEvent)
        {
            _active = false;
            _stoppedAt = now;
            return AutoRecordAction.Stop;
        }
        return AutoRecordAction.None;
    }
}
