using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class SingleCameraPageFactory
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IOnvifClient _onvif;
    private readonly IMajesticClient _majestic;
    private readonly RecordingService _recordings;
    private readonly IFileSystem _fs;
    private readonly ILoggerFactory _loggerFactory;

    public SingleCameraPageFactory(
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        IOnvifClient onvif,
        IMajesticClient majestic,
        RecordingService recordings,
        IFileSystem fs,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _directory = directory;
        _onvif = onvif;
        _majestic = majestic;
        _recordings = recordings;
        _fs = fs;
        _loggerFactory = loggerFactory;
    }

    public SingleCameraPageViewModel Create(Camera camera) =>
        new(camera, _coordinator, _directory, _onvif, _majestic, _recordings, _fs, _loggerFactory.CreateLogger<SingleCameraPageViewModel>());
}
