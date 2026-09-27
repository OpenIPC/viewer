using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

public sealed class CameraEditorFactory
{
    private readonly CameraConnectService _connect;
    private readonly CameraDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;

    public CameraEditorFactory(CameraConnectService connect, CameraDirectoryService directory, IDialogService dialogs, ILoggerFactory loggerFactory)
    {
        _connect = connect;
        _directory = directory;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
    }

    public CameraEditorViewModel CreateForNew() =>
        new(_connect, _directory, _dialogs, _loggerFactory.CreateLogger<CameraEditorViewModel>());

    public CameraEditorViewModel CreateForEdit(Camera existing, CameraCredentials? credentials, CameraCredentials? sshCredentials) =>
        new(existing, credentials, sshCredentials, _connect, _directory, _dialogs, _loggerFactory.CreateLogger<CameraEditorViewModel>());
}
