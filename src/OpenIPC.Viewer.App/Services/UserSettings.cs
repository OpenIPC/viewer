namespace OpenIPC.Viewer.App.Services;

// User-tweakable preferences persisted to AppDataDir/usersettings.json.
// Distinct from appsettings.json — appsettings is shipped defaults +
// optional override, this is "what the user toggled in the Settings page".
// Default values match the current hard-coded behavior so an upgrade with
// a missing settings file behaves identically.
public sealed record UserSettings(
    bool ShowTelemetryOverlay = true,
    bool VerboseLogging = false,
    bool AutoScanLanOnStartup = false,
    int MaxConcurrentGridSessions = 9,
    string RtspTransport = "tcp",
    // Auto SD/HD (Phase 12.2): substream in the multi-camera grid, mainstream
    // when a single tile fills the view (1×1 layout / single-camera page).
    // Off → always substream in the grid.
    bool AutoSdHd = true,
    // Local IPv4 to bind WS-Discovery to (Phase 12.6). "" = auto-pick the best
    // LAN interface (ignore VPN/virtual adapters).
    string PreferredNetworkInterface = "",
    string RecordingsDirOverride = "",
    // "system" follows CurrentUICulture; "en"/"ru" force a specific locale.
    string Language = "system",
    bool WelcomeShown = false,
    // Unlocks the "Edit raw" button in the Phase 5 Majestic panel. Off by
    // default — a typo here can leave the camera in a non-bootable state.
    bool RawConfigEditorEnabled = false,
    // SSH device suite (Phase 13). StrictHostKey on → a changed host key is
    // refused (TOFU); off → the new key is accepted and re-pinned (e.g. after
    // a camera reflash). DefaultPort is used when a camera has no per-camera
    // SSH port. TerminalFontSize is the monospace size in the SSH terminal.
    bool SshStrictHostKey = true,
    int SshDefaultPort = 22,
    int SshTerminalFontSize = 14,
    string MajesticConfigPath = "/etc/majestic.yaml",
    // Local AI analytics (Phase 15.2). "auto" lets the detector pick the
    // platform execution provider with a CPU fallback; "force-cpu" pins CPU.
    string AiAcceleration = "auto",
    // Audio listen (Phase 17.3). Muted by default — a freshly opened camera
    // never plays sound until the user taps the speaker. Volume is a 0..1 gain.
    bool AudioMuted = true,
    double AudioVolume = 1.0,
    // Tabbed layouts (Phase 19.1). Id of the layout shown in the Live grid; 0 =
    // not yet chosen → fall back to the first layout.
    int ActiveLayoutId = 0)
{
    public static UserSettings Default => new();
}
