using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.App.Views;

namespace OpenIPC.Viewer.App;

public sealed class App : Application
{
    // Set by the platform host (Desktop's Program.Main or Android's MainActivity)
    // before Avalonia hits OnFrameworkInitializationCompleted. A static slot is
    // the only way to thread IoC across the parameterless ctor that
    // AvaloniaMainActivity<App> requires on Android.
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Services is not null)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Desktop shows a splash that runs migrations + event ingestion
                // with visible progress, then swaps in the main window. (Android
                // runs that bootstrap in MainApplication and uses the single-view
                // branch below.)
                var startup = Services.GetRequiredService<StartupViewModel>();
                var splash = new StartupWindow { DataContext = startup };

                startup.Completed += () =>
                {
                    var vm = Services.GetRequiredService<MainWindowViewModel>();
                    var main = new MainWindow { DataContext = vm };
                    desktop.MainWindow = main;
                    main.Show();
                    splash.Close();
                };

                desktop.MainWindow = splash;
                // Start init only once the splash is actually on screen.
                splash.Opened += (_, _) => _ = startup.RunAsync();
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                var vm = Services.GetRequiredService<MainWindowViewModel>();
                singleView.MainView = new MainView { DataContext = vm };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
