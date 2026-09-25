using System;

namespace OpenIPC.Viewer.Core.Video;

// Digital zoom + pan over a live video surface. Pure math so it's unit-testable
// without a control; the view layer turns it into a render transform.
//
// Two coordinate spaces, both in the host control's units (DIPs):
//   - view space: the unzoomed control, 0..ViewWidth × 0..ViewHeight. The video
//     sits in it as a Stretch="Uniform" rect (ContentRect).
//   - screen space: what the user sees after the transform,
//     screen = (view - Offset) * Zoom.
// Offset is the view-space top-left of the visible window. Clamping keeps that
// window on the picture: once the zoomed picture is larger than the control on
// an axis, the letterbox bars can't be dragged into view; while it's smaller it
// stays centered.
public sealed class DigitalZoomViewport
{
    public const double MinZoom = 1.0;

    // 8× leaves room to look past 1:1 on a 4K/8K stream in a normal window
    // (8K at 1080p width is ~1:1 at 4×) without zooming into mush.
    public const double DefaultMaxZoom = 8.0;

    private double _maxZoom = DefaultMaxZoom;

    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    // Source frame width/height; <= 0 until known → the picture fills the view.
    public double ContentAspect { get; private set; }

    public double Zoom { get; private set; } = MinZoom;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public bool IsZoomed => Zoom > MinZoom + 1e-6;

    public double MaxZoom
    {
        get => _maxZoom;
        set
        {
            _maxZoom = Math.Max(MinZoom, value);
            if (Zoom > _maxZoom) Zoom = _maxZoom;
            Clamp();
        }
    }

    // Resizing keeps the same part of the picture on screen: the offset scales
    // with the view, then gets re-clamped against the new content rect.
    public void SetViewSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        if (ViewWidth > 0 && ViewHeight > 0)
        {
            OffsetX *= width / ViewWidth;
            OffsetY *= height / ViewHeight;
        }
        ViewWidth = width;
        ViewHeight = height;
        Clamp();
    }

    public void SetContentAspect(double aspect)
    {
        ContentAspect = aspect > 0 && !double.IsInfinity(aspect) ? aspect : 0;
        Clamp();
    }

    // The Stretch="Uniform" rect the picture occupies in view space.
    public (double X, double Y, double Width, double Height) ContentRect
    {
        get
        {
            var w = ViewWidth;
            var h = ViewHeight;
            if (ContentAspect <= 0 || w <= 0 || h <= 0) return (0, 0, w, h);
            if (w / h > ContentAspect)
            {
                var cw = h * ContentAspect;
                return ((w - cw) / 2, 0, cw, h);
            }
            var ch = w / ContentAspect;
            return (0, (h - ch) / 2, w, ch);
        }
    }

    // The part of the source frame on screen, normalized 0..1 — what a minimap
    // outlines.
    public (double X, double Y, double Width, double Height) VisibleContent
    {
        get
        {
            var (cx, cy, cw, ch) = ContentRect;
            if (cw <= 0 || ch <= 0) return (0, 0, 1, 1);
            var x0 = Math.Clamp((OffsetX - cx) / cw, 0, 1);
            var y0 = Math.Clamp((OffsetY - cy) / ch, 0, 1);
            var x1 = Math.Clamp((OffsetX + ViewWidth / Zoom - cx) / cw, 0, 1);
            var y1 = Math.Clamp((OffsetY + ViewHeight / Zoom - cy) / ch, 0, 1);
            return (x0, y0, x1 - x0, y1 - y0);
        }
    }

    public (double X, double Y) ScreenToView(double x, double y) =>
        (OffsetX + x / Zoom, OffsetY + y / Zoom);

    public (double X, double Y) ViewToScreen(double x, double y) =>
        ((x - OffsetX) * Zoom, (y - OffsetY) * Zoom);

    // factor > 1 zooms in. The picture point under the anchor stays under it
    // (unless clamping has to pull the window back onto the picture).
    public void ZoomAt(double anchorX, double anchorY, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) return;
        SetZoomAt(anchorX, anchorY, Zoom * factor);
    }

    public void SetZoomAt(double anchorX, double anchorY, double zoom)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0) return;
        var (vx, vy) = ScreenToView(anchorX, anchorY);
        Zoom = Math.Clamp(zoom, MinZoom, _maxZoom);
        OffsetX = vx - anchorX / Zoom;
        OffsetY = vy - anchorY / Zoom;
        Clamp();
    }

    // Screen-pixel drag: dragging right moves the picture right, i.e. the
    // window slides left over it.
    public void Pan(double deltaX, double deltaY)
    {
        OffsetX -= deltaX / Zoom;
        OffsetY -= deltaY / Zoom;
        Clamp();
    }

    // Zooms so a screen-space selection fills the view (the larger of its two
    // ratios wins, so all of it stays visible) and centers on it.
    public void ZoomToRect(double x, double y, double width, double height)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0 || width <= 0 || height <= 0) return;
        var (vx, vy) = ScreenToView(x, y);
        var vw = width / Zoom;
        var vh = height / Zoom;
        Zoom = Math.Clamp(Math.Min(ViewWidth / vw, ViewHeight / vh), MinZoom, _maxZoom);
        OffsetX = vx + vw / 2 - ViewWidth / Zoom / 2;
        OffsetY = vy + vh / 2 - ViewHeight / Zoom / 2;
        Clamp();
    }

    // Centers the visible window on a normalized picture point (minimap click).
    public void CenterOn(double normalizedX, double normalizedY)
    {
        var (cx, cy, cw, ch) = ContentRect;
        OffsetX = cx + Math.Clamp(normalizedX, 0, 1) * cw - ViewWidth / Zoom / 2;
        OffsetY = cy + Math.Clamp(normalizedY, 0, 1) * ch - ViewHeight / Zoom / 2;
        Clamp();
    }

    public void Reset()
    {
        Zoom = MinZoom;
        OffsetX = 0;
        OffsetY = 0;
        Clamp();
    }

    private void Clamp()
    {
        var (cx, cy, cw, ch) = ContentRect;
        OffsetX = ClampAxis(OffsetX, ViewWidth / Zoom, cx, cw);
        OffsetY = ClampAxis(OffsetY, ViewHeight / Zoom, cy, ch);
    }

    private static double ClampAxis(double offset, double visible, double contentStart, double contentLength)
    {
        // Picture wider than the window: keep the window on it. Narrower: center
        // it, so the bars split evenly instead of piling up on one side.
        if (contentLength >= visible)
            return Math.Clamp(offset, contentStart, contentStart + contentLength - visible);
        return contentStart + (contentLength - visible) / 2;
    }
}
