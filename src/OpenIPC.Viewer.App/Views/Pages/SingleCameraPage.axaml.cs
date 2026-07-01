using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class SingleCameraPage : UserControl
{
    // Swipe detection: short, mostly-horizontal pointer drag. 60px keeps
    // accidental thumb wiggles from triggering nav, 600ms upper bound stops
    // slow drags (which read more like pan than swipe) from counting.
    private const double SwipeThresholdPx = 60;
    private const double SwipeAxisRatio = 1.6;
    private static readonly TimeSpan SwipeMaxDuration = TimeSpan.FromMilliseconds(600);

    private Point? _pressOrigin;
    private DateTime _pressAt;

    // Below this viewport width (phone) the page scrolls with a fixed 16:9 video
    // preview; at/above it the video fills and the page doesn't scroll. Mirrors
    // the app-wide 700px breakpoint.
    private const double MobileBreakpoint = 700;

    public SingleCameraPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPageSizeChanged;

        // Holding defaults to touch-only; enabling mouse-hold lets us
        // validate the PTZ-toggle gesture on desktop too.
        VideoArea.SetValue(InputElement.IsHoldingEnabledProperty, true);
        VideoArea.SetValue(InputElement.IsHoldWithMouseEnabledProperty, true);

        VideoArea.Holding += OnVideoHolding;
        VideoArea.DoubleTapped += OnVideoDoubleTapped;
        VideoArea.Pinch += OnVideoPinch;
        VideoArea.AddHandler(InputElement.PointerPressedEvent, OnVideoPointerPressed, RoutingStrategies.Tunnel);
        VideoArea.AddHandler(InputElement.PointerReleasedEvent, OnVideoPointerReleased, RoutingStrategies.Tunnel);
        VideoArea.PointerWheelChanged += OnVideoPointerWheel;

        // Push-to-talk: hold the mic button to transmit (Phase 17.6). Pointer-up
        // and pointer-leave both end the transmission so a drag-off can't leave
        // the mic stuck open.
        TalkButton.AddHandler(InputElement.PointerPressedEvent, OnTalkPressed, RoutingStrategies.Tunnel);
        TalkButton.AddHandler(InputElement.PointerReleasedEvent, OnTalkReleased, RoutingStrategies.Tunnel);
        TalkButton.PointerExited += OnTalkReleased;
    }

    // Phone-width viewports can't fit the video plus the full Majestic config
    // panel, and a Grid alone would just run the panel off the bottom. Switch the
    // video row to a fixed 16:9 preview and let PageScroll scroll; on wider
    // viewports keep the fill layout (star row, scrolling disabled).
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width <= 0)
            return;

        var videoRow = RootGrid.RowDefinitions[1];
        if (width < MobileBreakpoint)
        {
            videoRow.Height = new GridLength(System.Math.Round(width * 9.0 / 16.0));
            PageScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        }
        else
        {
            videoRow.Height = new GridLength(1, GridUnitType.Star);
            PageScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        }
    }

    private async void OnTalkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) await vm.BeginTalkAsync();
    }

    private async void OnTalkReleased(object? sender, PointerEventArgs e)
    {
        if (Vm is { } vm) await vm.EndTalkAsync();
    }

    private SingleCameraPageViewModel? Vm => DataContext as SingleCameraPageViewModel;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ActivateAsync(CancellationToken.None);
    }

    private async void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.DisposeAsync();
    }

    private void OnVideoHolding(object? sender, HoldingRoutedEventArgs e)
    {
        // Fire on Started — Completed would mean the user has already lifted
        // and waiting for it makes the toggle feel laggy.
        if (e.HoldingState == HoldingState.Started)
            Vm?.TogglePtzOverlayCommand.Execute(null);
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
        => Vm?.ResetZoomCommand.Execute(null);

    private void OnVideoPinch(object? sender, PinchEventArgs e)
    {
        // PinchEventArgs.Scale is cumulative since the gesture began —
        // we use ApplyZoomDelta with the per-event scale ratio. For 9e we
        // settle for whole-scale application; pan + focal point come later.
        Vm?.ApplyZoomDelta(e.Scale);
    }

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(VideoArea).Properties;
        if (!props.IsLeftButtonPressed) return;
        _pressOrigin = e.GetPosition(VideoArea);
        _pressAt = DateTime.UtcNow;
    }

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressOrigin is not { } start || Vm is null) { _pressOrigin = null; return; }
        _pressOrigin = null;

        var elapsed = DateTime.UtcNow - _pressAt;
        if (elapsed > SwipeMaxDuration) return;

        var end = e.GetPosition(VideoArea);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        if (Math.Abs(dx) < SwipeThresholdPx) return;
        if (Math.Abs(dx) < SwipeAxisRatio * Math.Abs(dy)) return;

        // Right swipe = "drag forward" = previous camera (gallery convention).
        var direction = dx > 0 ? -1 : +1;
        _ = Vm.NavigateRelativeAsync(direction, CancellationToken.None);
    }

    private void OnVideoPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        // Desktop fallback for pinch — Ctrl + wheel adjusts digital zoom in
        // discrete steps. Without Ctrl, let the wheel fall through so a
        // future scroll-based control (timeline, etc.) can still react.
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        Vm?.StepZoom(e.Delta.Y > 0 ? +1 : -1);
        e.Handled = true;
    }
}
