using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Devices.Majestic;
using OpenIPC.Viewer.Devices.Onvif;
using OpenIPC.Viewer.Devices.Onvif.Discovery;
using OpenIPC.Viewer.Video.Recording;
using OpenIPC.Viewer.Infrastructure.Persistence;
using OpenIPC.Viewer.Infrastructure.Secrets;
using OpenIPC.Viewer.Video;
using Serilog;

namespace OpenIPC.Viewer.Desktop;

internal static class Composition
{
    public static ServiceProvider Build()
    {
        var configuration = BuildConfiguration();
        var serilog = BuildSerilog(configuration);

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(serilog, dispose: true);
        });

        // Platform
        services.AddSingleton<IFileSystem, DesktopFileSystem>();
        services.AddSingleton<ISecretsStore>(sp =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "DPAPI secret store is Windows-only; Linux/macOS stores land in Phase 8.");
            return new DpapiSecretsStore(sp.GetRequiredService<IFileSystem>().AppDataDir);
        });

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

        // Domain services
        services.AddSingleton<CameraDirectoryService>();

        // Video
        services.AddSingleton<IVideoEngine, FfmpegVideoEngine>();
        services.AddSingleton<LiveStreamCoordinator>();

        // ONVIF (Phase 4a). Client is stateless — singleton is fine.
        services.AddSingleton<IOnvifClient, OnvifCoreClient>();
        services.AddSingleton<OnvifProbeService>();

        // ONVIF discovery (Phase 4b). WS-Discovery only; mDNS is deferred.
        services.AddSingleton<IDiscoveryService, WsDiscoveryService>();

        // Majestic HTTP API (Phase 5). Single shared HttpClient inside —
        // safe because creds are attached per-request.
        services.AddSingleton<IMajesticClient, MajesticHttpClient>();

        // Recording (Phase 6). ffmpeg subprocess; one IRecordingSession per camera.
        // Path resolves bundled → "ffmpeg" on PATH; user can pin via appsettings.
        services.AddSingleton<IRecorder>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new FfmpegSubprocessRecorder(sp.GetRequiredService<ILoggerFactory>(), cfg["Recording:FfmpegPath"]);
        });
        services.AddSingleton<RecordingService>();

        // UI services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SingleCameraPageFactory>();
        services.AddSingleton<CameraEditorFactory>();
        services.AddSingleton<DiscoveryDialogFactory>();

        // ViewModels — singletons so navigation preserves their state across
        // sidebar switches and so messenger registrations stay alive.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<GridPageViewModel>();
        services.AddSingleton<CameraLibraryPageViewModel>();
        services.AddSingleton<RecordingsPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static IConfiguration BuildConfiguration()
    {
        var baseDir = AppContext.BaseDirectory;
        var userOverride = Path.Combine(AppPaths.AppDataDir.FullName, "appsettings.json");

        return new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile(userOverride, optional: true, reloadOnChange: true)
            .Build();
    }

    private static Serilog.ILogger BuildSerilog(IConfiguration configuration)
    {
        var logFile = Path.Combine(AppPaths.LogsDir.FullName, "openipc-viewer-.log");

        var cfg = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");

#if DEBUG
        cfg = cfg.WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
#endif

        return cfg.CreateLogger();
    }
}
