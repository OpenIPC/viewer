using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;
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

        // Domain services
        services.AddSingleton<CameraDirectoryService>();

        // Video
        services.AddSingleton<IVideoEngine, FfmpegVideoEngine>();
        services.AddSingleton<LiveStreamCoordinator>();

        // UI services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SingleCameraPageFactory>();
        services.AddSingleton<CameraEditorFactory>();

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
