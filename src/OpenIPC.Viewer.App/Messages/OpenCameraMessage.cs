using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.App.Messages;

public sealed record OpenCameraMessage(CameraId CameraId);

public sealed record GoBackToLibraryMessage;

public sealed record WindowMinimizedMessage;

public sealed record WindowRestoredMessage;
