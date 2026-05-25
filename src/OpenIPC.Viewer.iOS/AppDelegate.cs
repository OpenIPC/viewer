using System.Threading;
using Avalonia;
using Avalonia.iOS;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Persistence;
using UIKit;

namespace OpenIPC.Viewer.iOS;

[Register("AppDelegate")]
public sealed partial class AppDelegate : AvaloniaAppDelegate<App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Same composition order as Desktop/Program.Main and Android's
        // MainActivity.OnCreate: build services, migrate DB, start hot
        // ingestion before Avalonia constructs the first view.
        var services = Composition.Build();
        App.App.Services = services;

        services.GetRequiredService<IMigrationRunner>()
            .MigrateAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        services.GetRequiredService<EventIngestionService>()
            .StartAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
