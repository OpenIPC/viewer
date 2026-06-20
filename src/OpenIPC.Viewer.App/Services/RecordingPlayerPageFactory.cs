using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class RecordingPlayerPageFactory
{
    private readonly IPlaybackEngine _engine;
    private readonly IMediaProbe _probe;
    private readonly IEventRepository _events;
    private readonly ILoggerFactory _loggerFactory;

    public RecordingPlayerPageFactory(IPlaybackEngine engine, IMediaProbe probe, IEventRepository events, ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _probe = probe;
        _events = events;
        _loggerFactory = loggerFactory;
    }

    public RecordingPlayerPageViewModel Create(Recording recording, string cameraName) =>
        new(recording, cameraName, _engine, _probe, _events, _loggerFactory.CreateLogger<RecordingPlayerPageViewModel>());
}
