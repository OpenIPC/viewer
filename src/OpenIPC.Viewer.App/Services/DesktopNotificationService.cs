using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenIPC.Viewer.App.Views.Notifications;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.App.Services;

// Desktop notification sink (Phase 19.3): stacks Telegram-style toast windows in
// the bottom-right of the primary screen. The coordinator already vetted the
// notification (cooldown / quiet hours / type) — we just present it. Marshals to
// the UI thread since events arrive off the decode/event pipeline.
public sealed class DesktopNotificationService : INotificationService
{
    // Newest at the bottom; older ones float up. Cap so a burst doesn't paper
    // over the whole screen — the oldest is dropped when the cap is hit.
    private const int MaxVisible = 4;
    private const double MarginDip = 16;
    private const double GapDip = 10;

    private readonly List<ToastWindow> _active = new();

    // Always available on desktop — this sink is only registered in the desktop
    // head. (We can't probe Application.Current here: the coordinator is Start()ed
    // during composition, before the Avalonia lifetime exists.)
    public bool IsAvailable => true;

    public void Show(NotificationRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess()) ShowCore(request);
        else Dispatcher.UIThread.Post(() => ShowCore(request));
    }

    private void ShowCore(NotificationRequest request)
    {
        // No desktop lifetime yet (or shutting down) — nowhere to show a window.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
            return;

        if (_active.Count >= MaxVisible)
            _active[0].Close(); // Closed handler removes it and reflows.

        var toast = new ToastWindow();
        toast.SetContent(request);
        toast.Closed += (_, _) =>
        {
            _active.Remove(toast);
            Reflow();
        };

        _active.Add(toast);
        toast.Show();
        Reflow();
    }

    private void Reflow()
    {
        if (_active.Count == 0) return;

        var probe = _active[^1];
        var screen = probe.Screens.Primary ?? (probe.Screens.All.Count > 0 ? probe.Screens.All[0] : null);
        if (screen is null) return;

        var area = screen.WorkingArea;       // device pixels
        var scaling = probe.DesktopScaling;  // dip → px

        var marginPx = (int)(MarginDip * scaling);
        var gapPx = (int)(GapDip * scaling);

        // Stack upward from the bottom-right corner; newest (last) sits lowest.
        var cursorY = area.Y + area.Height - marginPx;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var w = _active[i];
            var wPx = (int)(w.Width * scaling);
            var hPx = (int)(w.Height * scaling);
            var x = area.X + area.Width - marginPx - wPx;
            cursorY -= hPx;
            w.Position = new PixelPoint(x, cursorY);
            cursorY -= gapPx;
        }
    }
}
