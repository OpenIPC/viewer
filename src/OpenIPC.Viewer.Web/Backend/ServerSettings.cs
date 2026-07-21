using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Web.Backend;

// Defaults for the Core services the headless server composes.
//
// IUserSettingsAccessor is a read-only view onto the desktop's Settings page,
// and the only implementation lives in the Avalonia App layer — which the web
// backend must not reference (architecture §2.2). The server has no settings UI
// of its own yet, so it supplies the same defaults the desktop ships with. The
// values that matter here are the discovery ones: PreferredNetworkInterface
// null means "auto-pick the best LAN adapter", which is what a server wants.
internal sealed class ServerSettings : IUserSettingsAccessor
{
    public string? RecordingsDirectoryOverride => null;
    public int MaxConcurrentGridSessions => 25;
    public string? PreferredNetworkInterface => null;

    public bool SshStrictHostKey => true;
    public int SshDefaultPort => 22;
    public string MajesticConfigPath => "/etc/majestic.yaml";

    public AiAcceleration AiAcceleration => AiAcceleration.Auto;
    public int ActiveLayoutId => 0;

    // Notifications are a client-side concern; nothing in the server raises them.
    public bool NotificationsEnabled => false;
    public bool NotifyOnMotion => false;
    public bool NotifyOnDetection => false;
    public int NotificationCooldownSeconds => 60;
    public bool QuietHoursEnabled => false;
    public int QuietHoursStartHour => 0;
    public int QuietHoursEndHour => 0;
}
