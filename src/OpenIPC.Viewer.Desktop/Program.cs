using System;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        // Route System.Diagnostics.Trace (and Avalonia's LogToTrace) to the console
        // so binding warnings and view-layer Trace.WriteLine show up next to Serilog.
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
#endif

        var services = Composition.Build();
        App.App.Services = services;

        var logger = services.GetRequiredService<ILogger<App.App>>();
        logger.LogInformation(
            "Starting OpenIPC.Viewer {Version}",
            typeof(Program).Assembly.GetName().Version);

        try
        {
            // Migrations + event ingestion no longer block here (they used to run
            // synchronously before Avalonia started, so a slow first-run migration
            // showed a frozen, window-less process). The startup splash window now
            // runs them off the UI thread with visible progress — see
            // App.OnFrameworkInitializationCompleted / StartupViewModel.
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error during app run");
            throw;
        }
        finally
        {
            logger.LogInformation("OpenIPC.Viewer shutting down");
            Serilog.Log.CloseAndFlush();
            // ServiceProvider.Dispose throws on services that only implement
            // IAsyncDisposable (LiveStreamCoordinator, GridPageViewModel, etc.).
            // DisposeAsync handles both shapes.
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}
