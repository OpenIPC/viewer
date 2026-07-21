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
using OpenIPC.Viewer.Infrastructure.Secrets;
using OpenIPC.Viewer.Infrastructure.Video.Decoders;
using OpenIPC.Viewer.Video.Recording;
using Serilog;

namespace OpenIPC.Viewer.Desktop;

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

        AddPlatformServices(services);

        // Share (Phase 14.6): no native sheet on desktop — reveal in file manager.
        services.AddSingleton<IShareService, DesktopShareService>();

        // Recording backend — ffmpeg subprocess works on all three desktop OSes
        // (resolves bundled binary or system PATH per FfmpegSubprocessRecorder).
        services.AddSingleton<IRecorder>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new FfmpegSubprocessRecorder(
                sp.GetRequiredService<ILoggerFactory>(),
                cfg["Recording:FfmpegPath"]);
        });

        // Clip export (Phase 16.5) — ffmpeg subprocess on desktop.
        services.AddSingleton<OpenIPC.Viewer.Core.Archive.IClipExporter>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new FfmpegSubprocessClipExporter(
                sp.GetRequiredService<ILoggerFactory>(),
                cfg["Recording:FfmpegPath"]);
        });

        // Notifications (Phase 19.3) — Telegram-style corner toasts. Registered
        // before AddSharedServices so it wins over the TryAdd Null fallback.
        services.AddSingleton<OpenIPC.Viewer.Core.Notifications.INotificationService, DesktopNotificationService>();

        services.AddSharedServices();

        // Splash/startup VM — desktop-only; runs migrations + ingestion with a
        // progress UI (Android does that bootstrap in MainApplication instead).
        services.AddSingleton<OpenIPC.Viewer.App.ViewModels.StartupViewModel>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        HookUserSettingsToLogLevel(provider, levelSwitch);
        // Eager resolve: the bridge subscribes in its ctor, so without this it
        // would never wire up (singletons are lazy).
        _ = provider.GetRequiredService<LiveStreamSettingsBridge>();
        provider.GetRequiredService<OpenIPC.Viewer.Core.Notifications.NotificationCoordinator>().Start();
        return provider;
    }

    // Bridges UserSettingsService → LoggingLevelSwitch + Localizer without
    // dragging Serilog into the App project. Applies on startup, re-applies
    // on every Changed event so the Settings page toggles take effect live.
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

    // Internal so the headless server-only path (ServerOnly) can reuse the exact
    // same platform trio (IFileSystem / ISecretsStore / …) without duplicating
    // the per-OS guards.
    internal static void AddPlatformServices(IServiceCollection services)
    {
        // Platform — one trio of IFileSystem / ISecretsStore / IHwDecoderFactory
        // per OS. Selection happens once at startup; downstream services see a
        // single registration each. The IsX checks inside each factory lambda
        // are what teaches the platform-compatibility analyzer that the call
        // site is guarded (CA1416).
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IFileSystem, WindowsFileSystem>();
            services.AddSingleton<ISecretsStore>(sp =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                return new DpapiSecretsStore(sp.GetRequiredService<IFileSystem>().AppDataDir);
            });
            services.AddSingleton<IHwDecoderFactory, D3d11VaDecoderFactory>();
            // Audio listen (Phase 17.2) — native WASAPI renderer on Windows.
            services.AddSingleton<IAudioOutput, Audio.WasapiAudioOutput>();
            // Talk/backchannel (Phase 17.6) — native WASAPI mic capture.
            services.AddSingleton<IAudioInput, Audio.WasapiAudioInput>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IFileSystem, LinuxFileSystem>();
            services.AddSingleton<ISecretsStore>(sp =>
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                return new LibSecretSecretsStore(
                    sp.GetRequiredService<IFileSystem>().AppDataDir,
                    sp.GetRequiredService<ILoggerFactory>());
            });
            services.AddSingleton<IHwDecoderFactory, VaapiDecoderFactory>();
            // Audio listen + talk (Phase 17) — ALSA playback + mic capture.
            services.AddSingleton<IAudioOutput, Audio.AlsaAudioOutput>();
            services.AddSingleton<IAudioInput, Audio.AlsaAudioInput>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IFileSystem, MacOsFileSystem>();
            services.AddSingleton<ISecretsStore>(sp =>
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                return new KeychainSecretsStore(sp.GetRequiredService<ILoggerFactory>());
            });
            services.AddSingleton<IHwDecoderFactory, VideoToolboxDecoderFactory>();
            // Audio listen + talk (Phase 17) — CoreAudio playback + mic capture.
            services.AddSingleton<IAudioOutput, Audio.CoreAudioOutput>();
            services.AddSingleton<IAudioInput, Audio.CoreAudioInput>();
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
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

    private static Serilog.ILogger BuildSerilog(IConfiguration configuration, Serilog.Core.LoggingLevelSwitch levelSwitch)
    {
        var logFile = Path.Combine(AppPaths.LogsDir.FullName, "openipc-viewer-.log");

        var cfg = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.ControlledBy(levelSwitch)
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
