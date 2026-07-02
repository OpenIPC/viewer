using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class SingleCameraPageFactory
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IOnvifClient _onvif;
    private readonly IMajesticClient _majestic;
    private readonly IMajesticConfigSchema _schema;
    private readonly IMajesticSshConfigClient _majesticSsh;
    private readonly RecordingService _recordings;
    private readonly UserSettingsService _userSettings;
    private readonly IDialogService _dialogs;
    private readonly ISnapshotService _snapshots;
    private readonly AudioMonitor _audio;
    private readonly OpenIPC.Viewer.Core.Platform.IAudioInput _audioInput;
    private readonly IAudioBackchannelClient _backchannel;
    private readonly IReachabilityProbe _reachability;
    private readonly OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine _analytics;
    private readonly AnalyticsBootstrap _analyticsBootstrap;
    private readonly ILoggerFactory _loggerFactory;

    public SingleCameraPageFactory(
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        IOnvifClient onvif,
        IMajesticClient majestic,
        IMajesticConfigSchema schema,
        IMajesticSshConfigClient majesticSsh,
        RecordingService recordings,
        UserSettingsService userSettings,
        IDialogService dialogs,
        ISnapshotService snapshots,
        AudioMonitor audio,
        OpenIPC.Viewer.Core.Platform.IAudioInput audioInput,
        IAudioBackchannelClient backchannel,
        IReachabilityProbe reachability,
        OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine analytics,
        AnalyticsBootstrap analyticsBootstrap,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _directory = directory;
        _onvif = onvif;
        _majestic = majestic;
        _schema = schema;
        _majesticSsh = majesticSsh;
        _recordings = recordings;
        _userSettings = userSettings;
        _dialogs = dialogs;
        _snapshots = snapshots;
        _audio = audio;
        _audioInput = audioInput;
        _backchannel = backchannel;
        _reachability = reachability;
        _analytics = analytics;
        _analyticsBootstrap = analyticsBootstrap;
        _loggerFactory = loggerFactory;
    }

    public SingleCameraPageViewModel Create(Camera camera) =>
        new(camera, _coordinator, _directory, _onvif, _majestic, _schema, _majesticSsh, _recordings, _userSettings, _dialogs, _snapshots, _audio,
            new PushToTalkController(_audioInput, _backchannel),
            _reachability,
            _analytics,
            _analyticsBootstrap,
            _loggerFactory.CreateLogger<SingleCameraPageViewModel>());
}
