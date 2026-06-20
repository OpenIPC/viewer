using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.App.Services;
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
        var levelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);
        var serilog = BuildSerilog(configuration, levelSwitch);

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(levelSwitch);

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
        services.AddSingleton<IShareService, IosShareService>();
        // Talk/backchannel (Phase 17.6) — AVAudioEngine mic capture. Needs
        // NSMicrophoneUsageDescription in Info.plist + a granted record permission.
        services.AddSingleton<IAudioInput, Platform.IosAudioInput>();

        // Recording — in-process libavformat. iOS won't let surveillance apps
        // run 24/7 in background, so recording is foreground-only (Phase 10
        // §10.6). Idle-timer-disabled lifecycle hook is a follow-up.
        services.AddSingleton<IRecorder, LibavformatRecorder>();

        // Clip export (Phase 16.5) — in-process libavformat (no subprocess on iOS).
        services.AddSingleton<OpenIPC.Viewer.Core.Archive.IClipExporter,
            OpenIPC.Viewer.Video.Recording.LibavformatClipExporter>();

        services.AddSharedServices();

        var provider = services.BuildServiceProvider(validateScopes: true);
        HookUserSettingsToLogLevel(provider, levelSwitch);
        _ = provider.GetRequiredService<LiveStreamSettingsBridge>();
        return provider;
    }

    private static void HookUserSettingsToLogLevel(IServiceProvider sp, Serilog.Core.LoggingLevelSwitch levelSwitch)
    {
        var settings = sp.GetRequiredService<UserSettingsService>();
        void Apply()
        {
            levelSwitch.MinimumLevel = settings.Current.VerboseLogging
                ? Serilog.Events.LogEventLevel.Debug
                : Serilog.Events.LogEventLevel.Information;
            Localizer.Instance.SetLanguage(ParseLang(settings.Current.Language));
        }
        Apply();
        settings.Changed += (_, _) => Apply();
    }

    private static LangCode ParseLang(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => LangCode.English,
        "ru" => LangCode.Russian,
        _ => LangCode.System,
    };

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

    private static Serilog.ILogger BuildSerilog(IConfiguration configuration, Serilog.Core.LoggingLevelSwitch levelSwitch)
    {
        var logFile = Path.Combine(AppPaths.LogsDir.FullName, "openipc-viewer-.log");

        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();
    }
}
