using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;

namespace OpenIPC.Viewer.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly UserSettingsService? _settings;
    private WindowState _previousState = WindowState.Normal;

    // Last known normal-state geometry, tracked live so a window that closes
    // while maximized still persists sensible un-maximize bounds.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Services?.GetService<UserSettingsService>();
        RestoreGeometry();

        PropertyChanged += OnWindowPropertyChanged;
        PositionChanged += (_, _) => CaptureNormalBounds();
        SizeChanged += (_, _) => CaptureNormalBounds();
        Closing += OnClosing;
    }

    // Apply the saved size/position before the window is shown so it opens in
    // place — no visible jump. Falls back to centered on first run (no saved X).
    private void RestoreGeometry()
    {
        var s = _settings?.Current;
        if (s is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        if (s.WindowWidth > 0) Width = s.WindowWidth;
        if (s.WindowHeight > 0) Height = s.WindowHeight;

        if (s.WindowX is { } x && s.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _normalPosition = Position;
        _normalSize = new Size(Width, Height);

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        EnsureOnScreen();
    }

    // Guard against a saved position that lands on a now-disconnected monitor:
    // if the window rect intersects no screen, recenter on the primary one.
    private void EnsureOnScreen()
    {
        if (WindowState == WindowState.Maximized) return;
        var screens = Screens;
        if (screens is null || screens.All.Count == 0) return;

        var size = new PixelSize(
            Math.Max(1, (int)(ClientSize.Width * RenderScaling)),
            Math.Max(1, (int)(ClientSize.Height * RenderScaling)));
        var rect = new PixelRect(Position, size);

        if (screens.All.Any(scr => scr.Bounds.Intersects(rect)))
            return;

        var target = screens.Primary ?? screens.All[0];
        var wa = target.WorkingArea;
        Position = new PixelPoint(
            wa.X + Math.Max(0, (wa.Width - size.Width) / 2),
            wa.Y + Math.Max(0, (wa.Height - size.Height) / 2));
        _normalPosition = Position;
    }

    private void CaptureNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings is null) return;

        // Maximized close → keep the tracked normal bounds; otherwise snapshot
        // the live geometry. Minimized is treated as normal (we never persist a
        // minimized window).
        var maximized = WindowState == WindowState.Maximized;
        if (!maximized) CaptureNormalBounds();

        var next = _settings.Current with
        {
            WindowX = _normalPosition.X,
            WindowY = _normalPosition.Y,
            WindowWidth = _normalSize.Width > 0 ? _normalSize.Width : _settings.Current.WindowWidth,
            WindowHeight = _normalSize.Height > 0 ? _normalSize.Height : _settings.Current.WindowHeight,
            WindowMaximized = maximized,
        };

        // Synchronous so the write completes before the process exits; the save
        // is off-UI file IO (ConfigureAwait(false)) so this won't deadlock.
        _settings.UpdateAsync(next).GetAwaiter().GetResult();
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        var current = (WindowState)e.NewValue!;
        if (current == WindowState.Minimized && _previousState != WindowState.Minimized)
            WeakReferenceMessenger.Default.Send(new WindowMinimizedMessage());
        else if (_previousState == WindowState.Minimized && current != WindowState.Minimized)
            WeakReferenceMessenger.Default.Send(new WindowRestoredMessage());

        _previousState = current;
    }
}
