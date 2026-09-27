using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.App.Messages;

public sealed record OpenCameraMessage(CameraId CameraId);

public sealed record GoBackToLibraryMessage;

// Phase 16: open the recordings player on a recorded segment, and return to
// the recordings list. CameraName travels with the message so the player can
// label itself without another directory lookup. StartAt (offset into the
// file) lets the Events page open a recording at the moment of an event;
// Rate carries the player's speed over to the previous/next recording.
public sealed record OpenRecordingMessage(Recording Recording, string CameraName, TimeSpan? StartAt = null, double Rate = 1.0);

// Reload: the player changed the archive (deleted its recording), so the list
// must be re-read instead of shown from cache.
public sealed record GoBackToRecordingsMessage(bool Reload = false);

// Recording player asks for (or leaves) chrome-free fullscreen; the main window
// owns the state so it survives previous/next swapping the player VM.
public sealed record SetPlayerFullscreenMessage(bool On);

// AI page → Events filtered to AI detections.
public sealed record ShowDetectionEventsMessage;

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
