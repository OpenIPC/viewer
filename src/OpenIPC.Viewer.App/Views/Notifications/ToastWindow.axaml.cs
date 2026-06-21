using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.App.Views.Notifications;

// Telegram-style corner toast (Phase 19.3 desktop sink). A borderless, non-
// activating topmost window the DesktopNotificationService positions and stacks.
// Auto-dismisses after a few seconds; a click closes it early. Click and the
// auto-close both just Close() — the service reflows the stack on Closed.
public sealed partial class ToastWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _timer;

    public ToastWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Close();

        Card.PointerPressed += OnCardPressed;

        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
        };

        Opened += OnOpened;
        Closed += (_, _) => _timer.Stop();
    }

    public void SetContent(NotificationRequest request)
    {
        TitleText.Text = request.Title;
        BodyText.Text = request.Body;

        var iconKey = request.Kind == EventKind.Detection ? "IconRadar" : "IconEvents";
        if (Application.Current?.TryFindResource(iconKey, out var res) == true && res is Geometry geo)
            EventIcon.Data = geo;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _timer.Start();

        // Subtle fade-in; positioning is owned by the service, so we only animate
        // opacity (sliding the window would fight the reflow).
        Dispatcher.UIThread.Post(() => Opacity = 1, DispatcherPriority.Background);
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => Close();
}
