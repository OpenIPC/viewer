using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Composition;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Infrastructure.Video.Decoders;
using OpenIPC.Viewer.iOS.Platform;
using OpenIPC.Viewer.Video.Recording;
using Serilog;

namespace OpenIPC.Viewer.iOS;

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

        // Platform trio — iOS branch. VideoToolbox is shared with macOS;
        // the factory class carries [SupportedOSPlatform("ios")] alongside.
        services.AddSingleton<IFileSystem, IosFileSystem>();
        services.AddSingleton<ISecretsStore>(sp =>
        {
            if (!OperatingSystem.IsIOS()) throw new PlatformNotSupportedException();
            return new IosSecretsStore(sp.GetRequiredService<IFileSystem>().AppDataDir);
        });
        services.AddSingleton<IHwDecoderFactory, VideoToolboxDecoderFactory>();

        // Recording — in-process libavformat. iOS won't let surveillance apps
        // run 24/7 in background, so recording is foreground-only (Phase 10
        // §10.6). Idle-timer-disabled lifecycle hook is a follow-up.
        services.AddSingleton<IRecorder, LibavformatRecorder>();

        services.AddSharedServices();

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

        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();
    }
}
