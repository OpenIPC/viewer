using System;
using System.IO;
using Android.Content;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Android.Platform;
using OpenIPC.Viewer.Android.Recording;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Composition;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Video;
using Serilog;

namespace OpenIPC.Viewer.Android;

internal static class Composition
{
    public static ServiceProvider Build(Context context)
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

        // Platform trio — Android only branch. Context-dependent constructors
        // capture the supplied ApplicationContext (long-lived per process).
        services.AddSingleton<IFileSystem>(_ => new AndroidFileSystem(context));
        services.AddSingleton<ISecretsStore>(sp =>
            new AndroidSecretsStore(context, sp.GetRequiredService<IFileSystem>().AppDataDir));
        services.AddSingleton<IHwDecoderFactory, MediaCodecDecoderFactory>();
        services.AddSingleton<IShareService>(_ => new AndroidShareService(context));

        // Recording — in-process libavformat (no subprocess on Android) +
        // foreground service for OS keep-alive. Phase 9c.
        services.AddSingleton<IRecorder>(sp =>
            new AndroidRecorder(context, sp.GetRequiredService<ILoggerFactory>()));

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
