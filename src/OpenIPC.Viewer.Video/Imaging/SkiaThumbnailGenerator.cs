using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Snapshots;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Imaging;

/// <summary>
/// SkiaSharp-backed <see cref="IThumbnailGenerator"/>. Lives in the Video
/// project because that's where SkiaSharp is already referenced (frame → JPEG
/// encoding) and the pinned Skia version is managed. Cross-platform: Skia runs
/// on every Avalonia head (desktop + Android/iOS).
/// </summary>
public sealed class SkiaThumbnailGenerator : IThumbnailGenerator
{
    public Task<ImageSize> GenerateAsync(byte[] jpeg, string thumbPath, int maxDim, CancellationToken ct)
    {
        using var original = SKBitmap.Decode(jpeg)
            ?? throw new InvalidOperationException("Image bytes did not decode");

        var fullSize = new ImageSize(original.Width, original.Height);

        var (tw, th) = Scale(original.Width, original.Height, maxDim);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var thumb = original.Resize(new SKImageInfo(tw, th), sampling)
            ?? throw new InvalidOperationException("Thumbnail resize failed");
        using var image = SKImage.FromBitmap(thumb);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality: 80);

        using (var fs = new FileStream(thumbPath, FileMode.Create, FileAccess.Write, FileShare.None))
            data.SaveTo(fs);

        return Task.FromResult(fullSize);
    }

    private static (int W, int H) Scale(int w, int h, int maxDim)
    {
        if (w <= maxDim && h <= maxDim) return (w, h);
        var ratio = (double)maxDim / Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * ratio)), Math.Max(1, (int)Math.Round(h * ratio)));
    }
}
