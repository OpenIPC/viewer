using System.IO;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Ssh;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Devices.Majestic;
using OpenIPC.Viewer.Devices.Onvif;
using OpenIPC.Viewer.Devices.Onvif.Discovery;
using OpenIPC.Viewer.Infrastructure.Net;
using OpenIPC.Viewer.Infrastructure.Persistence;

namespace OpenIPC.Viewer.Composition;

// Cross-platform DI registrations shared between Desktop (Win/Lin/Mac) and
// mobile heads (Android/iOS). The platform host registers the platform trio
// (IFileSystem / ISecretsStore / IHwDecoderFactory) and IRecorder before
// calling AddSharedServices — everything downstream resolves from there.
public static class SharedComposition
{
    public static IServiceCollection AddSharedServices(this IServiceCollection services)
    {
        // Persistence
        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var fs = sp.GetRequiredService<IFileSystem>();
            return new SqliteConnectionFactory(Path.Combine(fs.AppDataDir.FullName, "openipc-viewer.db"));
        });
        services.AddSingleton<IMigrationRunner, MigrationRunner>();
        services.AddSingleton<ICameraRepository, SqliteCameraRepository>();
        services.AddSingleton<IGroupRepository, SqliteGroupRepository>();
        services.AddSingleton<IRecordingRepository, SqliteRecordingRepository>();
        services.AddSingleton<IEventRepository, SqliteEventRepository>();
        services.AddSingleton<ISnapshotRepository, SqliteSnapshotRepository>();

        // Domain services
        services.AddSingleton<CameraDirectoryService>();
        services.AddSingleton<ICameraCredentialsProvider>(sp => sp.GetRequiredService<CameraDirectoryService>());
        services.AddSingleton<IReachabilityProbe, TcpReachabilityProbe>();

        // Snapshots (Phase 14): HD-always capture + thumbnail generation.
        services.AddSingleton<IThumbnailGenerator, OpenIPC.Viewer.Video.Imaging.SkiaThumbnailGenerator>();
        services.AddSingleton<IImageEditor, OpenIPC.Viewer.Video.Imaging.SkiaImageEditor>();
        services.AddSingleton<ISnapshotService, SnapshotService>();

        // Video
        services.AddSingleton<IVideoEngine, OpenIPC.Viewer.Video.FfmpegVideoEngine>();
        services.AddSingleton<LiveStreamCoordinator>();

        // ONVIF
        services.AddSingleton<IOnvifClient, OnvifCoreClient>();
        services.AddSingleton<OnvifProbeService>();
        services.AddSingleton<IDiscoveryService, WsDiscoveryService>();
        services.AddSingleton<OpenIPC.Viewer.Core.Onvif.Discovery.INetworkInterfaceProvider,
            OpenIPC.Viewer.Devices.Onvif.Discovery.SystemNetworkInterfaceProvider>();

        // Majestic HTTP
        services.AddSingleton<IMajesticClient, MajesticHttpClient>();

        // SSH device suite (Phase 13): factory creates per-use sessions; the
        // SSH transport for majestic.yaml is the fallback when HTTP is off.
        services.AddSingleton<OpenIPC.Viewer.Core.Ssh.ISshHostKeyStore,
            OpenIPC.Viewer.Infrastructure.Ssh.JsonFileHostKeyStore>();
        services.AddSingleton<ISshSessionFactory, OpenIPC.Viewer.Infrastructure.Ssh.SshNetSessionFactory>();
        services.AddSingleton<IMajesticSshConfigClient, MajesticSshConfigClient>();

        // Local AI analytics (Phase 15). One shared detector + engine; the model
        // is fetched on first enable and inference falls back to CPU when a GPU
        // EP is unavailable, so registering this on every head is boot-safe
        // (analytics is opt-in per camera and only initializes when enabled).
        services.AddSingleton<OpenIPC.Viewer.Core.Analytics.IModelProvider,
            OpenIPC.Viewer.Analytics.ModelProvider>();
        services.AddSingleton<OpenIPC.Viewer.Core.Analytics.IObjectDetector,
            OpenIPC.Viewer.Analytics.OnnxObjectDetector>();
        services.AddSingleton<OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine,
            OpenIPC.Viewer.Analytics.ObjectDetectionEngine>();

        // Recording lifecycle (IRecorder itself is registered by the platform
        // host — FFmpeg subprocess on desktop, FFmpegKit on Android, etc).
        services.AddSingleton<RecordingService>();

        // Events
        services.AddSingleton<ManualMotionEventSource>();
        services.AddSingleton<IMotionEventSource>(sp => sp.GetRequiredService<ManualMotionEventSource>());
        services.AddSingleton<EventIngestionService>();

        // UI services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SingleCameraPageFactory>();
        services.AddSingleton<CameraEditorFactory>();
        services.AddSingleton<DiscoveryDialogFactory>();
        services.AddSingleton<ManageGroupsDialogFactory>();
        services.AddSingleton<SshTerminalFactory>();
        services.AddSingleton<FileManagerFactory>();
        services.AddSingleton<ImageViewerFactory>();

        // User-tweakable settings (Phase 11). Side-effects (e.g. live Serilog
        // level switching) are wired by each platform composition after the
        // provider is built — keeps the App project free of Serilog refs.
        // Re-exposed under IUserSettingsAccessor so Core services (e.g.
        // RecordingService) can read user prefs without taking a dep on App.
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<OpenIPC.Viewer.Core.Settings.IUserSettingsAccessor>(
            sp => sp.GetRequiredService<UserSettingsService>());

        // Cross-cuts: subscribes UserSettings → LiveStreamCoordinator. Platform
        // hosts must eagerly resolve it once so its ctor runs and the event
        // subscription is wired up (singletons are lazy by default).
        services.AddSingleton<LiveStreamSettingsBridge>();

        // ViewModels — singletons so navigation preserves state across
        // sidebar/tab switches and messenger registrations stay alive.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<GridPageViewModel>();
        services.AddSingleton<CameraLibraryPageViewModel>();
        services.AddSingleton<RecordingsPageViewModel>();
        services.AddSingleton<SnapshotBrowserPageViewModel>();
        services.AddSingleton<EventsPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();

        return services;
    }
}
