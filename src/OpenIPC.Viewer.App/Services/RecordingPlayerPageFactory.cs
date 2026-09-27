using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class RecordingPlayerPageFactory
{
    private readonly IPlaybackEngine _engine;
    private readonly IMediaProbe _probe;
    private readonly IEventRepository _events;
    private readonly IRecordingRepository _recordings;
    private readonly ISnapshotService _snapshots;
    private readonly IClipExporter _exporter;
    private readonly IDialogService _dialogs;
    private readonly OpenIPC.Viewer.Core.Platform.IShareService _share;
    private readonly ILoggerFactory _loggerFactory;

    public RecordingPlayerPageFactory(
        IPlaybackEngine engine,
        IMediaProbe probe,
        IEventRepository events,
        IRecordingRepository recordings,
        ISnapshotService snapshots,
        IClipExporter exporter,
        IDialogService dialogs,
        OpenIPC.Viewer.Core.Platform.IShareService share,
        ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _probe = probe;
        _events = events;
        _recordings = recordings;
        _snapshots = snapshots;
        _exporter = exporter;
        _dialogs = dialogs;
        _share = share;
        _loggerFactory = loggerFactory;
    }

    public RecordingPlayerPageViewModel Create(Recording recording, string cameraName) =>
        new(recording, cameraName, _engine, _probe, _events, _recordings, _snapshots, _exporter, _dialogs, _share,
            _loggerFactory.CreateLogger<RecordingPlayerPageViewModel>());
}
