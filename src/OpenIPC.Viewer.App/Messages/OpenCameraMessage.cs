using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.App.Messages;

public sealed record OpenCameraMessage(CameraId CameraId);

public sealed record GoBackToLibraryMessage;

// Phase 16: open the recordings player on a recorded segment, and return to
// the recordings list. CameraName travels with the message so the player can
// label itself without another directory lookup.
public sealed record OpenRecordingMessage(Recording Recording, string CameraName);

public sealed record GoBackToRecordingsMessage;

public sealed record WindowMinimizedMessage;

public sealed record WindowRestoredMessage;

// Desktop kiosk fullscreen (Phase 20): toggles a chrome-free fullscreen grid
// for an unattended guard station. Raised by the grid's fullscreen button and
// the F11 key; MainWindowViewModel owns the state.
public sealed record ToggleKioskMessage;

// Raised by a grid tile's Close button (error cell). The grid drops the tile
// for this session; it comes back on the next Live-tab refresh since the
// camera's IncludedInGrid flag is untouched.
public sealed record CloseTileMessage(CameraId CameraId);
