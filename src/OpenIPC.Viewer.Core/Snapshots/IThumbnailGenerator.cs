using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>Full-image pixel dimensions.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Decodes a captured JPEG to produce a cached gallery thumbnail. Lives behind
/// an interface so the codec (SkiaSharp) stays out of Core — the impl ships in
/// the Video project where Skia already lives.
/// </summary>
public interface IThumbnailGenerator
{
    /// <summary>
    /// Decodes <paramref name="jpeg"/>, writes a downscaled JPEG thumbnail
    /// (longest side ≤ <paramref name="maxDim"/>) to <paramref name="thumbPath"/>,
    /// and returns the full image's pixel size. Throws if the bytes don't decode.
    /// </summary>
    Task<ImageSize> GenerateAsync(byte[] jpeg, string thumbPath, int maxDim, CancellationToken ct);
}
