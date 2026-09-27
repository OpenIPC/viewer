using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

public static class CameraEditorResultExtensions
{
    // Stores what the editor's Connect learned (PTZ/ONVIF profile, OpenIPC
    // web API) so the camera page knows about it without probing again.
    public static async Task SaveDetectedAsync(this CameraDirectoryService directory, CameraId id, CameraEditorResult result, CancellationToken ct)
    {
        if (result.Onvif is { } onvif)
            await directory.SaveOnvifMetadataAsync(id, onvif, ct).ConfigureAwait(false);
        if (result.IsMajestic is { } majestic)
            await directory.SetIsMajesticAsync(id, majestic, ct).ConfigureAwait(false);
    }
}
