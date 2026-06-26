using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using OpenIPC.Viewer.App.Messages;

namespace OpenIPC.Viewer.App.Services;

// Foreground idle watchdog (opt-in). When the user leaves the app open but
// stops interacting for UserSettings.IdleStreamTimeoutMinutes, release the live
// streams to spare the camera network — the "left it open and walked away" case
// that minimize-detection never sees. It reuses the very same Window
// minimize/restore messages the grid already acts on (Smart Pause → release →
// rebuild), so no new teardown path is needed.
//
// Window minimize is owned by MainWindow; this parks itself while the window is
// actually minimized so the two don't double up on the same release.
public sealed class IdleStreamMonitor
{
    private readonly UserSettingsService _settings;
    private readonly DispatcherTimer _timer;
    private bool _suspended;
    private bool _minimized;

    public IdleStreamMonitor(UserSettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => OnTick();
        _settings.Changed += (_, _) => Reset();
    }

    // Hook window-scoped input on the tunnel so a click that's handled by a
    // child control still counts as activity. Window-scoped (not a global OS
    // hook) is exactly right: when our window isn't focused it gets no input, so
    // "another app in front" also counts as idle.
    public void Attach(Window window)
    {
        window.AddHandler(InputElement.PointerMovedEvent, OnInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, OnInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnInput, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, OnInput, RoutingStrategies.Tunnel);
        Reset();
    }

    // MainWindow tells us when the OS minimize state flips so we step aside for
    // the real minimize→release path and don't fire a duplicate.
    public void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        if (minimized)
        {
            _timer.Stop();
            _suspended = false;
        }
        else
        {
            Reset();
        }
    }

    private void OnInput(object? sender, RoutedEventArgs e)
    {
        if (_suspended)
        {
            _suspended = false;
            WeakReferenceMessenger.Default.Send(new WindowRestoredMessage());
        }
        Reset();
    }

    private void Reset()
    {
        _timer.Stop();
        if (_minimized) return;
        var minutes = _settings.Current.IdleStreamTimeoutMinutes;
        if (minutes <= 0) return;
        _timer.Interval = TimeSpan.FromMinutes(minutes);
        _timer.Start();
    }

    private void OnTick()
    {
        _timer.Stop();
        if (_suspended || _minimized) return;
        _suspended = true;
        WeakReferenceMessenger.Default.Send(new WindowMinimizedMessage());
    }
}
