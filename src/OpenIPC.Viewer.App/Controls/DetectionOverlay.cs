using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.App.Controls;

// Draws detection boxes + labels over the video tile (Phase 15.5). Boxes are
// normalized 0..1 in the source frame, so we just scale them to Bounds — no
// dependency on the model input size. Rendered directly (a Canvas of one box
// per visual would churn the tree); we repaint on each new detection result.
public sealed class DetectionOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<Detection>?> DetectionsProperty =
        AvaloniaProperty.Register<DetectionOverlay, IReadOnlyList<Detection>?>(nameof(Detections));

    public static readonly StyledProperty<bool> ShowBoxesProperty =
        AvaloniaProperty.Register<DetectionOverlay, bool>(nameof(ShowBoxes), defaultValue: true);

    // Distinct hues per class id so different objects read apart at a glance.
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4C, 0xAF, 0x50), Color.FromRgb(0x21, 0x96, 0xF3),
        Color.FromRgb(0xFF, 0x98, 0x00), Color.FromRgb(0xE0, 0x40, 0x40),
        Color.FromRgb(0x9C, 0x27, 0xB0), Color.FromRgb(0x00, 0xBC, 0xD4),
        Color.FromRgb(0xFF, 0xEB, 0x3B), Color.FromRgb(0xFF, 0x40, 0x81),
    };

    static DetectionOverlay()
    {
        AffectsRender<DetectionOverlay>(DetectionsProperty, ShowBoxesProperty);
    }

    public IReadOnlyList<Detection>? Detections
    {
        get => GetValue(DetectionsProperty);
        set => SetValue(DetectionsProperty, value);
    }

    public bool ShowBoxes
    {
        get => GetValue(ShowBoxesProperty);
        set => SetValue(ShowBoxesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var detections = Detections;
        if (!ShowBoxes || detections is null || detections.Count == 0) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        foreach (var d in detections)
        {
            var color = Palette[((d.ClassId % Palette.Length) + Palette.Length) % Palette.Length];
            var pen = new Pen(new SolidColorBrush(color), 2);

            var x = d.X * w;
            var y = d.Y * h;
            var bw = d.Width * w;
            var bh = d.Height * h;
            var box = new Rect(x, y, bw, bh);
            context.DrawRectangle(null, pen, box);

            var label = $"{d.ClassName} {d.Confidence:0.00}";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Brushes.White);

            var labelW = text.Width + 8;
            var labelH = text.Height + 2;
            var labelY = y - labelH >= 0 ? y - labelH : y; // flip below the top edge if clipped
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, labelY, labelW, labelH));
            context.DrawText(text, new Point(x + 4, labelY + 1));
        }
    }
}
