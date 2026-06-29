using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Status;

// Pure helpers behind the Health Center: how to order cameras (worst first) and
// how to roll a set of statuses up into the online/attention/offline summary.
// Kept out of the view model so it unit-tests without a directory or a probe.
public static class HealthRollup
{
    // Sort key — lower sorts first, so anything needing a look sits at the top.
    public static int SortRank(CameraStatus status) => status switch
    {
        CameraStatus.Offline => 0,
        CameraStatus.Attention => 1,
        CameraStatus.Connecting => 2,
        CameraStatus.Unknown => 3,
        _ => 4, // Online
    };

    // Summary counts. Unknown folds into Offline — for an at-a-glance overview an
    // un-probed camera is "not known good", which reads closer to offline.
    public static (int Online, int Attention, int Offline) Counts(IEnumerable<CameraStatus> statuses)
    {
        int online = 0, attention = 0, offline = 0;
        foreach (var s in statuses)
        {
            switch (s)
            {
                case CameraStatus.Online: online++; break;
                case CameraStatus.Attention: attention++; break;
                case CameraStatus.Offline:
                case CameraStatus.Unknown: offline++; break;
            }
        }
        return (online, attention, offline);
    }
}
