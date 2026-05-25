using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.App.Controls;

// Virtual joystick. PointerPressed captures, PointerMoved updates the velocity
// fed into PtzController, PointerReleased recenters and stops. Y is inverted so
// dragging up sends positive tilt (camera looks up). All visuals are drawn in
// Render — no child controls — to keep hit-testing trivial.
public sealed class PtzJoystick : Control
{
    public static readonly StyledProperty<PtzController?> ControllerProperty =
        AvaloniaProperty.Register<PtzJoystick, PtzController?>(nameof(Controller));

    public PtzController? Controller
    {
        get => GetValue(ControllerProperty);
        set => SetValue(ControllerProperty, value);
    }

    private const double RingRadius = 48;
    private const double KnobRadius = 18;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromArgb(0xb0, 0x17, 0x1c, 0x24));
    private static readonly IBrush KnobBrush = new SolidColorBrush(Color.Parse("#2196F3"));
    private static readonly IPen RingPen = new Pen(new SolidColorBrush(Color.FromArgb(0xff, 0x2a, 0x32, 0x3d)), 1);

    private bool _dragging;
    private Point _knob;

    public PtzJoystick()
    {
        Width = 110;
        Height = 110;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(BgBrush, RingPen, center, RingRadius, RingRadius);
        context.DrawEllipse(KnobBrush, null, center + _knob, KnobRadius, KnobRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Controller is null) return;
        e.Pointer.Capture(this);
        _dragging = true;
        UpdateFromPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Controller is null) return;
        UpdateFromPointer(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        _knob = default;
        InvalidateVisual();
        _ = Controller?.StopAsync(CancellationToken.None);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        _knob = default;
        InvalidateVisual();
        _ = Controller?.StopAsync(CancellationToken.None);
    }

    private void UpdateFromPointer(Point p)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        var maxR = RingRadius - KnobRadius;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist > maxR && dist > 0)
        {
            dx = dx / dist * maxR;
            dy = dy / dist * maxR;
        }
        _knob = new Point(dx, dy);
        InvalidateVisual();

        var nx = (float)(dx / maxR);
        var ny = (float)(-dy / maxR);
        Controller?.SetVelocity(new PtzVelocity(nx, ny, 0));
    }
}
