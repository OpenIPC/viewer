using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.App.Messages;

public sealed record OpenCameraMessage(CameraId CameraId);

public sealed record GoBackToLibraryMessage;

public sealed record WindowMinimizedMessage;

public sealed record WindowRestoredMessage;

// Raised by a grid tile's Close button (error cell). The grid drops the tile
// for this session; it comes back on the next Live-tab refresh since the
// camera's IncludedInGrid flag is untouched.
public sealed record CloseTileMessage(CameraId CameraId);
