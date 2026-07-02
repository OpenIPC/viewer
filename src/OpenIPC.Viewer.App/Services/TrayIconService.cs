using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        menu.Add(Item("Tray.Open", "IconVideo", RestoreMainWindow));
        menu.Add(Item("Tray.AddCamera", "IconCamera", AddCamera));
        menu.Add(new NativeMenuItemSeparator());
        // About lives on the settings page, so both items land there — About
        // just deep-links to its section.
        menu.Add(Item("Tray.Settings", "IconCog", () => OpenSettings(about: false)));
        menu.Add(Item("Tray.About", "IconAbout", () => OpenSettings(about: true)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Tray.Exit", "IconPower", ExitApplication));
        return menu;
    }

    private NativeMenuItem Item(string key, string iconKey, Action action)
    {
        var item = new NativeMenuItem(Localizer.Instance[key])
        {
            Icon = GetMenuIcon(iconKey),
        };
        item.Click += (_, _) => action();
        return item;
    }

    // Rendered lucide bitmaps, one per geometry key. Cached because the menu is
    // rebuilt on every language switch and the icons never change.
    private readonly Dictionary<string, Bitmap?> _menuIcons = new();

    // Renders one of the Theme.axaml lucide stroke geometries into a small
    // bitmap for NativeMenuItem.Icon (there is no vector slot on native menu
    // items). Mirrors the Path.lucide style. The bitmap MUST be 16×16 at 96
    // DPI: the menu presents it at pixel size, so a hi-DPI (2x) bitmap comes
    // out double-sized and gets cropped to its top-left corner in the ~16px
    // icon slot. Best-effort: a missing resource or render failure just
    // leaves the item without an icon.
    private Bitmap? GetMenuIcon(string resourceKey)
    {
        if (_menuIcons.TryGetValue(resourceKey, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            if (Application.Current?.TryGetResource(resourceKey, null, out var res) == true &&
                res is Geometry geometry)
            {
                var accent = Application.Current.TryGetResource("AccentBrush", null, out var b) && b is IBrush brush
                    ? brush
                    : Brushes.Gray;

                var path = new Avalonia.Controls.Shapes.Path
                {
                    Data = geometry,
                    Stroke = accent,
                    StrokeThickness = 1.5,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Fill = Brushes.Transparent,
                    Stretch = Stretch.Uniform,
                    Width = 14,
                    Height = 14,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                var host = new Border { Width = 16, Height = 16, Child = path };
                host.Measure(new Size(16, 16));
                host.Arrange(new Rect(0, 0, 16, 16));

                var rtb = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96));
                rtb.Render(host);
                bitmap = rtb;
            }
        }
        catch
        {
            bitmap = null;
        }

        _menuIcons[resourceKey] = bitmap;
        return bitmap;
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
        foreach (var icon in _menuIcons.Values)
            icon?.Dispose();
        _menuIcons.Clear();
        if (_tray is null)
            return;
        // Explicit disposal removes the icon immediately — relying on process
        // death leaves a ghost icon in the Windows tray until hovered.
        _tray.Clicked -= OnTrayClicked;
        _tray.Dispose();
        _tray = null;
    }
}
