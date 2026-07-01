using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App.Services;
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

    // How long the mobile splash overlay stays before fading. The Android boot
    // is fast, so without a minimum beat the splash would just flicker.
    private static readonly TimeSpan MobileSplashMinDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan MobileSplashFadeDuration = TimeSpan.FromMilliseconds(400);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Services is not null)
        {
            // Settings → Appearance: the animated splash is our own feature (not
            // the OS launch screen), so the user can turn it off entirely.
            var showSplash = Services.GetService<UserSettingsService>()?.Current.ShowSplash ?? true;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                StartDesktop(desktop, showSplash);
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
                StartSingleView(singleView, showSplash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Desktop shows a splash that runs migrations + event ingestion with visible
    // progress, then swaps in the main window. With the splash disabled the same
    // init runs window-less and the shell opens straight away when it finishes —
    // but a failure still needs the retry/exit UI, so the StartupWindow is
    // created lazily in that case instead of being lost.
    private static void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop, bool showSplash)
    {
        var startup = Services!.GetRequiredService<StartupViewModel>();

        if (showSplash)
        {
            var splash = new StartupWindow { DataContext = startup };
            startup.Completed += () => ShowMainWindow(desktop, splash);
            desktop.MainWindow = splash;
            // Start init only once the splash is actually on screen.
            splash.Opened += (_, _) => _ = startup.RunAsync();
            return;
        }

        StartupWindow? errorWindow = null;
        startup.Completed += () => ShowMainWindow(desktop, errorWindow);
        startup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(StartupViewModel.HasError) ||
                !startup.HasError || errorWindow is not null)
                return;
            errorWindow = new StartupWindow { DataContext = startup };
            desktop.MainWindow = errorWindow;
            errorWindow.Show();
        };
        _ = startup.RunAsync();
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window? toClose)
    {
        var vm = Services!.GetRequiredService<MainWindowViewModel>();
        var main = new MainWindow { DataContext = vm };
        desktop.MainWindow = main;
        main.Show();
        toClose?.Close();
    }

    // Android/iOS: migrations already ran in the platform host, so the splash is
    // a purely visual overlay — MainView initializes underneath, and after a
    // minimum beat the overlay fades out and leaves the tree.
    private static void StartSingleView(ISingleViewApplicationLifetime singleView, bool showSplash)
    {
        var vm = Services!.GetRequiredService<MainWindowViewModel>();
        var main = new MainView { DataContext = vm };

        if (!showSplash)
        {
            singleView.MainView = main;
            return;
        }

        var splash = new MobileSplashView();
        var host = new Panel();
        host.Children.Add(main);
        host.Children.Add(splash);
        singleView.MainView = host;

        // Count the minimum beat from the splash's first layout pass, not from
        // here — Avalonia's first Android frame lands well after lifetime setup,
        // and a timer started now can remove the splash before it's ever drawn.
        splash.Loaded += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            splash.Opacity = 0; // MobileSplashView carries the Opacity transition
            DispatcherTimer.RunOnce(() => host.Children.Remove(splash), MobileSplashFadeDuration);
        }, MobileSplashMinDuration);
    }
}
