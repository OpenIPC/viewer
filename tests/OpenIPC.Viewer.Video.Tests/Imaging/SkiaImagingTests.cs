using System.IO;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Video.Imaging;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Tests.Imaging;

public sealed class SkiaImagingTests
{
    [Fact]
    public async Task Thumbnail_DownscalesAndReportsFullSize()
    {
        var src = MakeJpeg(width: 200, height: 100, SKColors.Red);
        var thumbPath = TempPath();

        var size = await new SkiaThumbnailGenerator().GenerateAsync(src, thumbPath, maxDim: 64, CancellationToken.None);

        Assert.Equal(200, size.Width);
        Assert.Equal(100, size.Height);
        Assert.True(File.Exists(thumbPath));
        using var thumb = SKBitmap.Decode(thumbPath);
        Assert.True(Math.Max(thumb.Width, thumb.Height) <= 64);
    }

    [Fact]
    public async Task Editor_AppliesCrop()
    {
        var src = MakeJpeg(200, 100, SKColors.Red);
        var srcPath = TempPath();
        File.WriteAllBytes(srcPath, src);
        var outPath = TempPath();

        // Crop to the left half.
        var edit = new SnapshotEdit(0.0, 0.0, 0.5, 1.0, Array.Empty<SnapshotAnnotation>());
        var size = await new SkiaImageEditor().RenderAsync(srcPath, edit, outPath, CancellationToken.None);

        Assert.InRange(size.Width, 95, 105);
        Assert.Equal(100, size.Height);
    }

    [Fact]
    public async Task Editor_DrawsAnnotationPixels()
    {
        var src = MakeJpeg(200, 200, SKColors.Red);
        var srcPath = TempPath();
        File.WriteAllBytes(srcPath, src);
        var outPath = TempPath();

        // A thick green box over a red image: the output must contain green pixels.
        var box = new SnapshotAnnotation(AnnotationKind.Rectangle, 0.2, 0.2, 0.8, 0.8, 0xFF00FF00, 0.05, null);
        await new SkiaImageEditor().RenderAsync(
            srcPath, new SnapshotEdit(null, null, null, null, new[] { box }), outPath, CancellationToken.None);

        using var result = SKBitmap.Decode(outPath);
        var foundGreen = false;
        for (var y = 0; y < result.Height && !foundGreen; y++)
            for (var x = 0; x < result.Width; x++)
            {
                var p = result.GetPixel(x, y);
                if (p.Green > 120 && p.Red < 120) { foundGreen = true; break; }
            }
        Assert.True(foundGreen, "Expected the green annotation to be composited onto the image");
    }

    private static byte[] MakeJpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"oipc-img-{Guid.NewGuid():N}.jpg");
}
