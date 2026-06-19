using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ImageViewerContent : UserControl
{
    private bool _panning;
    private Point _lastPointer;
    private double _pinchStartZoom = 1.0;

    // Editor state (14.5). Annotations are recorded in normalized image coords;
    // the on-canvas shapes are live preview only — the saved JPEG is composited
    // by the Skia editor from these annotations.
    private sealed record EditAction(Control Shape, bool IsCrop);
    private readonly List<SnapshotAnnotation> _edits = new();
    private readonly List<EditAction> _actions = new();
    private double? _cropX, _cropY, _cropW, _cropH;
    private bool _drawing;
    private Point _drawStart;
    private Control? _preview;

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

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Focus();
    }

    private ImageViewerViewModel? Vm => DataContext as ImageViewerViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is { } vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Editing ended (save / cancel / navigation): wipe the preview overlay.
        if (e.PropertyName == nameof(ImageViewerViewModel.IsEditing) && Vm is { IsEditing: false })
            ClearEdits();
    }

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
            case Key.Left when !Vm.IsEditing: Vm.PrevCommand.Execute(null); e.Handled = true; break;
            case Key.Right when !Vm.IsEditing: Vm.NextCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: Vm.CloseCommand.Execute(null); e.Handled = true; break;
        }
    }

    // --- editor drawing (14.5) ---

    // The image is shown Uniform-fit at zoom 1 while editing; this is its
    // letterboxed rectangle within the viewport, in EditCanvas coordinates.
    private Rect? GetImageRect()
    {
        if (Vm?.CurrentImage is not { } bmp) return null;
        var img = this.FindControl<Image>("ViewImage");
        if (img is null) return null;
        var b = img.Bounds;
        double pw = bmp.PixelSize.Width, ph = bmp.PixelSize.Height;
        if (b.Width <= 0 || b.Height <= 0 || pw <= 0 || ph <= 0) return null;
        var scale = Math.Min(b.Width / pw, b.Height / ph);
        double dw = pw * scale, dh = ph * scale;
        return new Rect(b.X + (b.Width - dw) / 2, b.Y + (b.Height - dh) / 2, dw, dh);
    }

    private static Point Clamp(Point p, Rect r) => new(
        Math.Clamp(p.X, r.X, r.Right),
        Math.Clamp(p.Y, r.Y, r.Bottom));

    private static (double X, double Y) Normalize(Point p, Rect r) => (
        Math.Clamp((p.X - r.X) / r.Width, 0, 1),
        Math.Clamp((p.Y - r.Y) / r.Height, 0, 1));

    private IBrush CurrentBrush() => new SolidColorBrush(Color.FromUInt32(Vm?.EditColor ?? 0xFFFF3B30));

    private double OverlayStroke(Rect r) => Math.Max(1.5, (Vm?.EditThickness ?? 0.006) * Math.Max(r.Width, r.Height));

    private async void OnEditPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { IsEditing: true } vm || GetImageRect() is not { } rect) return;
        var p = Clamp(e.GetPosition(EditCanvas), rect);
        e.Handled = true;

        if (vm.CurrentTool == EditTool.Text)
        {
            var text = await vm.PromptAnnotationTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            var (nx, ny) = Normalize(p, rect);
            _edits.Add(new SnapshotAnnotation(AnnotationKind.Text, nx, ny, nx, ny, vm.EditColor, vm.EditThickness, text));
            var fontSize = Math.Max(12, vm.EditThickness * Math.Max(rect.Width, rect.Height) * 6);
            var tb = new TextBlock { Text = text, Foreground = CurrentBrush(), FontSize = fontSize };
            Canvas.SetLeft(tb, p.X);
            Canvas.SetTop(tb, p.Y - fontSize);
            EditCanvas.Children.Add(tb);
            _actions.Add(new EditAction(tb, IsCrop: false));
            return;
        }

        _drawing = true;
        _drawStart = p;
        _preview = vm.CurrentTool switch
        {
            EditTool.Rectangle => new Rectangle { Stroke = CurrentBrush(), StrokeThickness = OverlayStroke(rect) },
            EditTool.Crop => new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double>(4, 3) },
            _ => new Polyline { Stroke = CurrentBrush(), StrokeThickness = OverlayStroke(rect), StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round },
        };
        EditCanvas.Children.Add(_preview);
        UpdatePreview(_drawStart, _drawStart);
    }

    private void OnEditPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_drawing || Vm is null || GetImageRect() is not { } rect) return;
        UpdatePreview(_drawStart, Clamp(e.GetPosition(EditCanvas), rect));
    }

    private void OnEditPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing || Vm is not { } vm || GetImageRect() is not { } rect) { _drawing = false; return; }
        _drawing = false;
        var end = Clamp(e.GetPosition(EditCanvas), rect);
        UpdatePreview(_drawStart, end);

        var (x1, y1) = Normalize(_drawStart, rect);
        var (x2, y2) = Normalize(end, rect);

        if (vm.CurrentTool == EditTool.Crop)
        {
            // Only one crop at a time — drop a previous crop action.
            var prev = _actions.FindLast(a => a.IsCrop);
            if (prev is not null) { EditCanvas.Children.Remove(prev.Shape); _actions.Remove(prev); }
            _cropX = Math.Min(x1, x2);
            _cropY = Math.Min(y1, y2);
            _cropW = Math.Abs(x2 - x1);
            _cropH = Math.Abs(y2 - y1);
            if (_preview is not null) _actions.Add(new EditAction(_preview, IsCrop: true));
        }
        else
        {
            var kind = vm.CurrentTool == EditTool.Rectangle ? AnnotationKind.Rectangle : AnnotationKind.Arrow;
            _edits.Add(new SnapshotAnnotation(kind, x1, y1, x2, y2, vm.EditColor, vm.EditThickness, null));
            if (_preview is not null) _actions.Add(new EditAction(_preview, IsCrop: false));
        }
        _preview = null;
    }

    private void UpdatePreview(Point a, Point b)
    {
        switch (_preview)
        {
            case Rectangle rectShape:
                Canvas.SetLeft(rectShape, Math.Min(a.X, b.X));
                Canvas.SetTop(rectShape, Math.Min(a.Y, b.Y));
                rectShape.Width = Math.Abs(b.X - a.X);
                rectShape.Height = Math.Abs(b.Y - a.Y);
                break;
            case Polyline arrow:
                arrow.Points = ArrowPoints(a, b);
                break;
        }
    }

    private static Points ArrowPoints(Point a, Point b)
    {
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        var dist = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var len = Math.Max(8, dist * 0.18);
        var a1 = angle + Math.PI - Math.PI / 7;
        var a2 = angle + Math.PI + Math.PI / 7;
        var h1 = new Point(b.X + len * Math.Cos(a1), b.Y + len * Math.Sin(a1));
        var h2 = new Point(b.X + len * Math.Cos(a2), b.Y + len * Math.Sin(a2));
        return new Points { a, b, h1, b, h2 };
    }

    private void OnEditUndoClick(object? sender, RoutedEventArgs e)
    {
        if (_actions.Count == 0) return;
        var last = _actions[^1];
        _actions.RemoveAt(_actions.Count - 1);
        EditCanvas.Children.Remove(last.Shape);
        if (last.IsCrop)
        {
            _cropX = _cropY = _cropW = _cropH = null;
        }
        else if (_edits.Count > 0)
        {
            _edits.RemoveAt(_edits.Count - 1);
        }
    }

    private async void OnEditSaveClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var edit = new SnapshotEdit(_cropX, _cropY, _cropW, _cropH, new List<SnapshotAnnotation>(_edits));
        await vm.ApplyEditAsync(edit);
    }

    private void ClearEdits()
    {
        _edits.Clear();
        _actions.Clear();
        _cropX = _cropY = _cropW = _cropH = null;
        _preview = null;
        _drawing = false;
        EditCanvas?.Children.Clear();
    }
}
