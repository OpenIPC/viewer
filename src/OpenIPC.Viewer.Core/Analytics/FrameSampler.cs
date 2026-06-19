using System;

namespace OpenIPC.Viewer.Core.Analytics;

// Rate gate for the analytics tap (Phase 15.3). The decoder produces ~25-30
// FPS; feeding every frame to the detector is the dashboard's "8 GB in 3 hours"
// trap. ShouldSample admits at most targetFps frames per second by wall-clock
// spacing. Pure (time passed in) so it unit-tests deterministically.
public sealed class FrameSampler
{
    private readonly TimeSpan _minInterval;
    private DateTime _lastAdmitted = DateTime.MinValue;

    public FrameSampler(int targetFps)
    {
        if (targetFps < 1) targetFps = 1;
        if (targetFps > 30) targetFps = 30;
        TargetFps = targetFps;
        _minInterval = TimeSpan.FromSeconds(1.0 / targetFps);
    }

    public int TargetFps { get; }

    public bool ShouldSample(DateTime nowUtc)
    {
        if (_lastAdmitted == DateTime.MinValue || nowUtc - _lastAdmitted >= _minInterval)
        {
            _lastAdmitted = nowUtc;
            return true;
        }
        return false;
    }

    public void Reset() => _lastAdmitted = DateTime.MinValue;
}
