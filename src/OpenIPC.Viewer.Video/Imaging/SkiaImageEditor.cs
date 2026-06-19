using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Snapshots;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Imaging;

/// <summary>
/// SkiaSharp-backed <see cref="IImageEditor"/>. Draws annotations onto the full
/// image, then crops, then encodes JPEG. Coordinates are normalized (0..1) so
/// the same edit renders identically regardless of source resolution.
/// </summary>
public sealed class SkiaImageEditor : IImageEditor
{
    public Task<ImageSize> RenderAsync(string srcPath, SnapshotEdit edit, string outPath, CancellationToken ct)
    {
        using var src = SKBitmap.Decode(srcPath)
            ?? throw new InvalidOperationException($"Could not decode {srcPath}");

        int w = src.Width, h = src.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.DrawBitmap(src, 0, 0);

        var longest = Math.Max(w, h);
        foreach (var a in edit.Annotations)
        {
            var color = ToColor(a.ColorArgb);
            var strokePx = (float)Math.Max(1.0, a.Thickness * longest);
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokePx,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };

            float x1 = (float)(a.X1 * w), y1 = (float)(a.Y1 * h);
            float x2 = (float)(a.X2 * w), y2 = (float)(a.Y2 * h);

            switch (a.Kind)
            {
                case AnnotationKind.Rectangle:
                    canvas.DrawRect(
                        SKRect.Create(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1)),
                        paint);
                    break;
                case AnnotationKind.Arrow:
                    DrawArrow(canvas, x1, y1, x2, y2, paint, strokePx);
                    break;
                case AnnotationKind.Text:
                    using (var fill = new SKPaint { Color = color, IsAntialias = true })
                    using (var font = new SKFont(SKTypeface.Default, Math.Max(12f, strokePx * 6f)))
                        canvas.DrawText(a.Text ?? string.Empty, x1, y1, SKTextAlign.Left, font, fill);
                    break;
            }
        }

        using var image = surface.Snapshot();

        SKImage final = image;
        SKImage? cropped = null;
        if (edit.CropX is { } cx && edit.CropY is { } cy && edit.CropW is { } cw && edit.CropH is { } chh
            && cw > 0 && chh > 0)
        {
            var rx = Math.Clamp((int)(cx * w), 0, w - 1);
            var ry = Math.Clamp((int)(cy * h), 0, h - 1);
            var rw = Math.Clamp((int)(cw * w), 1, w - rx);
            var rh = Math.Clamp((int)(chh * h), 1, h - ry);
            cropped = image.Subset(SKRectI.Create(rx, ry, rw, rh));
            if (cropped is not null) final = cropped;
        }

        using (var data = final.Encode(SKEncodedImageFormat.Jpeg, 92))
        using (var fs = File.Create(outPath))
            data.SaveTo(fs);

        var size = new ImageSize(final.Width, final.Height);
        cropped?.Dispose();
        return Task.FromResult(size);
    }

    private static void DrawArrow(SKCanvas c, float x1, float y1, float x2, float y2, SKPaint paint, float stroke)
    {
        c.DrawLine(x1, y1, x2, y2, paint);
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        var head = Math.Max(stroke * 4f, 12f);
        var a1 = angle + Math.PI - Math.PI / 7;
        var a2 = angle + Math.PI + Math.PI / 7;
        c.DrawLine(x2, y2, (float)(x2 + head * Math.Cos(a1)), (float)(y2 + head * Math.Sin(a1)), paint);
        c.DrawLine(x2, y2, (float)(x2 + head * Math.Cos(a2)), (float)(y2 + head * Math.Sin(a2)), paint);
    }

    private static SKColor ToColor(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));
}
