using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class RecordingPlayerPageFactory
{
    private readonly IPlaybackEngine _engine;
    private readonly IMediaProbe _probe;
    private readonly ILoggerFactory _loggerFactory;

    public RecordingPlayerPageFactory(IPlaybackEngine engine, IMediaProbe probe, ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _probe = probe;
        _loggerFactory = loggerFactory;
    }

    public RecordingPlayerPageViewModel Create(Recording recording, string cameraName) =>
        new(recording, cameraName, _engine, _probe, _loggerFactory.CreateLogger<RecordingPlayerPageViewModel>());
}
