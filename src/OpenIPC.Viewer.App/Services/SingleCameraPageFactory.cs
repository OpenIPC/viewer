using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class SingleCameraPageFactory
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IOnvifClient _onvif;
    private readonly IFileSystem _fs;
    private readonly ILoggerFactory _loggerFactory;

    public SingleCameraPageFactory(
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        IOnvifClient onvif,
        IFileSystem fs,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _directory = directory;
        _onvif = onvif;
        _fs = fs;
        _loggerFactory = loggerFactory;
    }

    public SingleCameraPageViewModel Create(Camera camera) =>
        new(camera, _coordinator, _directory, _onvif, _fs, _loggerFactory.CreateLogger<SingleCameraPageViewModel>());
}
