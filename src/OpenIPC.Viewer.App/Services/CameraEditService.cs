using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

// Opens the camera editor for an existing camera and saves the result. Shared by
// the Cameras page and the AI page ("Configure" on a detection camera).
public sealed class CameraEditService
{
    private readonly CameraEditorFactory _editorFactory;
    private readonly IDialogService _dialogs;
    private readonly CameraDirectoryService _directory;
    private readonly ILogger<CameraEditService> _logger;

    public CameraEditService(
        CameraEditorFactory editorFactory,
        IDialogService dialogs,
        CameraDirectoryService directory,
        ILogger<CameraEditService> logger)
    {
        _editorFactory = editorFactory;
        _dialogs = dialogs;
        _directory = directory;
        _logger = logger;
    }

    // True when the camera was changed and saved.
    public async Task<bool> EditAsync(Camera camera)
    {
        var creds = await _directory.GetCredentialsAsync(camera.Id, CancellationToken.None).ConfigureAwait(true);
        var sshCreds = await _directory.GetSshCredentialsAsync(camera.Id, CancellationToken.None).ConfigureAwait(true);
        var editor = _editorFactory.CreateForEdit(camera, creds, sshCreds);
        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.UpdateRequest is not { } req)
            return false;

        try
        {
            await _directory.UpdateAsync(camera.Id, req, CancellationToken.None).ConfigureAwait(true);
            await _directory.SaveDetectedAsync(camera.Id, result, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update camera {Id}", camera.Id);
            return false;
        }
    }
}
