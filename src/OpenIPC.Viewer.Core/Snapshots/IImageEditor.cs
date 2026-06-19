using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Renders a <see cref="SnapshotEdit"/> over a source image and writes a JPEG
/// copy. Lives behind an interface so the codec (SkiaSharp) stays out of Core;
/// the impl ships in the Video project alongside the thumbnail generator.
/// </summary>
public interface IImageEditor
{
    /// <summary>
    /// Reads <paramref name="srcPath"/>, draws the edit's annotations, applies
    /// its crop, and writes the result as JPEG to <paramref name="outPath"/>.
    /// Returns the output image's pixel size. The source is never modified.
    /// </summary>
    Task<ImageSize> RenderAsync(string srcPath, SnapshotEdit edit, string outPath, CancellationToken ct);
}
