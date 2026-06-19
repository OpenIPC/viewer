using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Platform;

/// <summary>
/// Platform sharing for a saved file. On mobile this is the native share sheet;
/// on desktop there's no system share sheet, so the implementation reveals the
/// file in the OS file manager. <see cref="SupportsSystemShare"/> lets the UI
/// label the action appropriately ("Share" vs "Reveal").
/// </summary>
public interface IShareService
{
    /// <summary>True when <see cref="ShareFileAsync"/> opens a native share sheet.</summary>
    bool SupportsSystemShare { get; }

    /// <summary>
    /// Shares the file via the platform share sheet (mobile) or reveals it in the
    /// file manager (desktop). <paramref name="mimeType"/> defaults to image/jpeg.
    /// </summary>
    Task ShareFileAsync(string path, string? mimeType, CancellationToken ct);

    /// <summary>Reveals/selects the file in the OS file manager (desktop). No-op where unsupported.</summary>
    Task RevealInFolderAsync(string path);
}
