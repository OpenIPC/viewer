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

        // Headless web-server mode (Phase 20): run Kestrel without Avalonia and
        // return, before the single-instance guard or any GUI service spins up.
        if (ServerOnly.IsRequested(args))
            return ServerOnly.Run(args);

        // Opt-in single-instance mode: when another copy already runs and the
        // user enabled the guard, hand focus to it and bail out before any
        // services (logs, db) spin up.
        if (!SingleInstanceGuard.TryBecomePrimary() && SingleInstanceGuard.IsEnabledInSettings())
        {
            SingleInstanceGuard.ActivatePrimary();
            return 0;
        }

        var services = Composition.Build();
        App.App.Services = services;

        var logger = services.GetRequiredService<ILogger<App.App>>();
        logger.LogInformation(
            "Starting OpenIPC.Viewer {Version}",
            typeof(Program).Assembly.GetName().Version);

        int exitCode;
        try
        {
            // Migrations + event ingestion no longer block here (they used to run
            // synchronously before Avalonia started, so a slow first-run migration
            // showed a frozen, window-less process). The startup splash window now
            // runs them off the UI thread with visible progress — see
            // App.OnFrameworkInitializationCompleted / StartupViewModel.
            exitCode = BuildAvaloniaApp()
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
            //
            // We're back on the UI thread here, but the dispatcher loop has already
            // exited. Disposing inline would deadlock: service DisposeAsync paths
            // use ConfigureAwait(true) (e.g. GridPageViewModel releasing live grid
            // tiles), and their continuations would post to the now-dead dispatcher
            // and never run — leaving the process hung in the background. Dispose on
            // a thread-pool thread instead (no SynchronizationContext to capture, so
            // those continuations resume freely), and bound the wait so a wedged
            // native join can't keep us alive either — background threads die with
            // the process regardless.
            if (!System.Threading.Tasks.Task.Run(() => services.DisposeAsync().AsTask())
                    .Wait(TimeSpan.FromSeconds(10)))
            {
                logger.LogWarning("Service disposal timed out during shutdown; forcing exit");
            }
        }

        // Returning from Main is not enough: any foreground thread left behind by
        // a native interop layer keeps the closed app alive in Task Manager
        // (observed on Windows 10). Everything is flushed and disposed by now, so
        // terminate unconditionally.
        Environment.Exit(exitCode);
        return exitCode; // unreachable — Environment.Exit does not return
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}
