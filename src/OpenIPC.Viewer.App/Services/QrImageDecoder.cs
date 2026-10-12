using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;

namespace OpenIPC.Viewer.App.Services;

// Decodes a QR / barcode from an image file. The image is loaded with
// Avalonia's own (Skia) decoder and handed to ZXing core as raw pixels, so we
// carry no extra imaging library. Returns the decoded text or null when no
// code is found in the image (ZXing's Decode returns null on miss).
public static class QrImageDecoder
{
    public static Task<string?> DecodeAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run(() => Decode(imagePath, ct), ct);

    private static string? Decode(string imagePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(imagePath);
        using var bitmap = WriteableBitmap.Decode(stream);
        ct.ThrowIfCancellationRequested();

        byte[] pixels;
        int width, height;
        RGBLuminanceSource.BitmapFormat format;
        using (var fb = bitmap.Lock())
        {
            format = fb.Format == PixelFormat.Bgra8888 ? RGBLuminanceSource.BitmapFormat.BGRA32
                : fb.Format == PixelFormat.Rgba8888 ? RGBLuminanceSource.BitmapFormat.RGBA32
                : throw new NotSupportedException($"Unsupported pixel format {fb.Format}");

            width = fb.Size.Width;
            height = fb.Size.Height;
            var rowBytes = width * 4;
            pixels = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
                Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * rowBytes, rowBytes);
        }

        ct.ThrowIfCancellationRequested();
        var source = new RGBLuminanceSource(pixels, width, height, format);
        return new BarcodeReaderGeneric().Decode(source)?.Text;
    }
}
