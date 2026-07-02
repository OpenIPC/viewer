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
    // Idle stream auto-pause (opt-in). Minutes of no user input while the window
    // is open before all live sessions are released to spare the camera network;
    // 0 = off. Window minimize already pauses+releases regardless of this (see
    // GridPageViewModel.Receive(WindowMinimizedMessage)) — this covers the
    // "left open in the foreground and walked away" case.
    int IdleStreamTimeoutMinutes = 0,
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
    // The animated startup splash (our in-app one, not the OS launch screen).
    // Applies to both the desktop StartupWindow and the mobile overlay.
    bool ShowSplash = true,
    // Shared "risky device tools" gate: unlocks the raw Majestic config editors
    // (HTTP + SSH) on the camera page AND the SSH file manager in the library.
    // Off by default — a typo in the config or a deleted system file can leave
    // the camera in a non-bootable state. The toggle itself is the consent, so
    // the gated tools open without an extra per-use warning.
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
    int ActiveLayoutId = 0,
    // Network config auto-sync (Phase 20). When enabled, on startup the app
    // MIRRORS its cameras + layouts from the JSON at ConfigSyncPath (a local or
    // UNC path, e.g. a shared folder) — the file is the source of truth, so
    // locally-added cameras/layouts are removed. Off (default) → no sync, local
    // editing as usual. If the path is unreachable the last-applied local config
    // keeps working. ConfigSyncSignature is the SHA-256 of the last-applied file
    // content, so an unchanged file isn't re-imported on every launch.
    bool ConfigSyncEnabled = false,
    string ConfigSyncPath = "",
    string ConfigSyncSignature = "",
    // Opt-in (Phase 20 Slice B): also sync camera passwords, carried ENCRYPTED in
    // the file with a fleet passphrase (the passphrase itself lives in the OS
    // secrets store, never in this JSON). Off → topology only, passwords stay
    // local. The passphrase is read from ISecretsStore under ConfigSyncSecretKey.
    bool ConfigSyncIncludeCredentials = false,
    // Notifications (Phase 19.3).
    bool NotificationsEnabled = true,
    bool NotifyOnMotion = true,
    bool NotifyOnDetection = true,
    int NotificationCooldownSeconds = 30,
    bool QuietHoursEnabled = false,
    int QuietHoursStartHour = 22,
    int QuietHoursEndHour = 7,
    // Main window geometry (desktop only). Null position = never saved yet →
    // center on first run. Size is the client area in DIPs; defaults match the
    // MainWindow.axaml fallback. WindowMaximized restores a maximized window
    // while keeping the normal-state bounds above for un-maximize.
    int? WindowX = null,
    int? WindowY = null,
    double WindowWidth = 1200,
    double WindowHeight = 780,
    bool WindowMaximized = false,
    // Grid "stills" mode: show periodic HTTP snapshots instead of a live RTSP
    // session in every grid tile — far cheaper on CPU/bandwidth for big walls.
    // Interval is seconds between grabs. A per-camera override lands later.
    bool GridStillsMode = false,
    int GridStillsIntervalSeconds = 10,
    // Desktop only: closing the main window hides it to the tray icon instead
    // of quitting (live streams are released while hidden). Off = close quits.
    bool CloseToTray = false)
{
    public static UserSettings Default => new();
}
