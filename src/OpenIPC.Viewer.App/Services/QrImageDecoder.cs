using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing.ImageSharp;

namespace OpenIPC.Viewer.App.Services;

// Thin wrapper around ZXing.ImageSharp so callers don't bind to the binding's
// type names. Returns the decoded text or null when no QR / barcode is found
// in the image (no exception path — ZXing's Decode returns null on miss).
public static class QrImageDecoder
{
    public static async Task<string?> DecodeAsync(string imagePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(imagePath);
        using var image = await Image.LoadAsync<Rgba32>(stream, ct).ConfigureAwait(false);
        var reader = new BarcodeReader<Rgba32>();
        var result = reader.Decode(image);
        return result?.Text;
    }
}
