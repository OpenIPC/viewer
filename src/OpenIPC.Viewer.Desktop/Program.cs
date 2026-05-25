using System;
using System.Threading;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Persistence;

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

        var logger = services.GetRequiredService<ILogger<App.App>>();
        logger.LogInformation(
            "Starting OpenIPC.Viewer {Version}",
            typeof(Program).Assembly.GetName().Version);

        try
        {
            services.GetRequiredService<IMigrationRunner>()
                .MigrateAsync(CancellationToken.None)
                .GetAwaiter().GetResult();

            return BuildAvaloniaApp(services)
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

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder
            .Configure(() => new App.App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}
