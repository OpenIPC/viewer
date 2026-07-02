using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Services;

// Desktop tray icon. Created by App once the main window is up and lives until
// process exit. Owns the close-to-tray flow: with the setting on, MainWindow
// cancels its close and hides itself, and this menu is the way back (or out).
public sealed class TrayIconService : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IServiceProvider _services;
    private TrayIcon? _tray;

    // Set by the tray "Exit" item so MainWindow.OnClosing lets the close
    // proceed even when close-to-tray is enabled.
    public bool ExitRequested { get; private set; }

    public TrayIconService(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services)
    {
        _desktop = desktop;
        _services = services;
    }

    public void Show()
    {
        if (_tray is not null)
            return;

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(
                new Uri("avares://OpenIPC.Viewer.App/Assets/openipc-logo.png"))),
            ToolTipText = "OpenIPC Viewer",
            Menu = BuildMenu(),
        };
        _tray.Clicked += OnTrayClicked;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });

        // Menu item text follows the in-app language switch.
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnTrayClicked(object? sender, EventArgs e) => RestoreMainWindow();

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_tray is not null)
            _tray.Menu = BuildMenu();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Add(Item("Tray.Open", RestoreMainWindow));
        menu.Add(Item("Tray.AddCamera", AddCamera));
        menu.Add(new NativeMenuItemSeparator());
        // About lives on the settings page, so both items land there — About
        // just deep-links to its section.
        menu.Add(Item("Tray.Settings", () => OpenSettings(about: false)));
        menu.Add(Item("Tray.About", () => OpenSettings(about: true)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Tray.Exit", ExitApplication));
        return menu;
    }

    private static NativeMenuItem Item(string key, Action action)
    {
        var item = new NativeMenuItem(Localizer.Instance[key]);
        item.Click += (_, _) => action();
        return item;
    }

    private void RestoreMainWindow()
    {
        if (_desktop.MainWindow is Views.MainWindow window)
            window.RestoreFromTray();
    }

    private void AddCamera()
    {
        RestoreMainWindow();
        var vm = _services.GetService<MainWindowViewModel>();
        if (vm is null) return;
        vm.NavigateCommand.Execute("library");
        vm.Library.AddCameraCommand.Execute(null);
    }

    private void OpenSettings(bool about)
    {
        RestoreMainWindow();
        var vm = _services.GetService<MainWindowViewModel>();
        if (vm is null) return;
        vm.NavigateCommand.Execute("settings");
        if (about)
            vm.Settings.SelectAboutSection();
    }

    private void ExitApplication()
    {
        ExitRequested = true;
        _desktop.Shutdown();
    }

    public void Dispose()
    {
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;
        if (_tray is null)
            return;
        // Explicit disposal removes the icon immediately — relying on process
        // death leaves a ghost icon in the Windows tray until hovered.
        _tray.Clicked -= OnTrayClicked;
        _tray.Dispose();
        _tray = null;
    }
}
