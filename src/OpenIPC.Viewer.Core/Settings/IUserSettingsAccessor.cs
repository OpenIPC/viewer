using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Core.Settings;

// Thin read-only view onto the UI's UserSettings that Core services can
// consume without taking a dep on the App project (which would invert the
// architecture). App.Services.UserSettingsService implements this; SharedComposition
// registers the same instance under both types.
public interface IUserSettingsAccessor
{
    // Empty / null means "use IFileSystem.RecordingsDir as-is". Non-empty
    // string is an absolute path the user picked in Settings.
    string? RecordingsDirectoryOverride { get; }

    int MaxConcurrentGridSessions { get; }

    // Local IPv4 to bind discovery/listeners to (Phase 12.6). Empty / null =
    // auto-pick the best LAN interface (ignore VPN/virtual adapters).
    string? PreferredNetworkInterface { get; }

    // SSH device suite (Phase 13). Strict host-key checking (TOFU reject on
    // change) when true; default SSH port for cameras without an explicit one;
    // remote path of the Majestic config for the SSH transport.
    bool SshStrictHostKey { get; }
    int SshDefaultPort { get; }
    string MajesticConfigPath { get; }

    // Local AI analytics acceleration preference (Phase 15.2).
    AiAcceleration AiAcceleration { get; }

    // Tabbed layouts (Phase 19.1). Id of the active Live-grid layout; 0 = unset,
    // callers fall back to the first layout.
    int ActiveLayoutId { get; }
}
