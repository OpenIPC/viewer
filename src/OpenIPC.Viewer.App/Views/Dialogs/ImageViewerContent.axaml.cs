using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ImageViewerContent : UserControl
{
    private bool _panning;
    private Point _lastPointer;
    private double _pinchStartZoom = 1.0;

    public ImageViewerContent()
    {
        InitializeComponent();

        // Pinch-zoom on touch (mobile). The recognizer raises Pinch with a
        // cumulative scale relative to gesture start.
        var viewport = this.FindControl<Grid>("Viewport");
        if (viewport is not null)
        {
            viewport.GestureRecognizers.Add(new PinchGestureRecognizer());
            viewport.Pinch += OnPinch;
            viewport.PinchEnded += OnPinchEnded;
        }

        Loaded += (_, _) => Focus();
    }

    private ImageViewerViewModel? Vm => DataContext as ImageViewerViewModel;

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is null) return;
        Vm.ApplyZoomFactor(e.Delta.Y > 0 ? 1.15 : 0.87);
        e.Handled = true;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v) return;
        _panning = true;
        _lastPointer = e.GetPosition(v);
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning || Vm is null || sender is not Visual v) return;
        var p = e.GetPosition(v);
        Vm.Pan(p.X - _lastPointer.X, p.Y - _lastPointer.Y);
        _lastPointer = p;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e) => _panning = false;

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (Vm is null) return;
        if (e.Scale <= 0) return;
        // First Pinch tick of a gesture: capture the baseline zoom.
        if (Math.Abs(e.Scale - 1.0) < 0.001 && !_panning)
            _pinchStartZoom = Vm.Zoom;
        Vm.SetZoom(_pinchStartZoom * e.Scale);
        e.Handled = true;
    }

    private void OnPinchEnded(object? sender, PinchEndedEventArgs e)
    {
        if (Vm is not null) _pinchStartZoom = Vm.Zoom;
    }

    private void OnActualSizeClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.CurrentImage is not { } bmp) return;
        var img = this.FindControl<Image>("ViewImage");
        if (img is null || img.Bounds.Width <= 0) return;
        // The image is laid out Uniform-fit at zoom 1; 1:1 means the fitted
        // width should equal the bitmap's pixel width.
        Vm.SetZoom(bmp.PixelSize.Width / img.Bounds.Width);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Left: Vm.PrevCommand.Execute(null); e.Handled = true; break;
            case Key.Right: Vm.NextCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: Vm.CloseCommand.Execute(null); e.Handled = true; break;
        }
    }
}
