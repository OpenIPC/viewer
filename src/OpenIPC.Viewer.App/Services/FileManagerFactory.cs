using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.App.Services;

public sealed class FileManagerFactory
{
    private readonly CameraDirectoryService _directory;
    private readonly ISshSessionFactory _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;

    public FileManagerFactory(CameraDirectoryService directory, ISshSessionFactory sessions, IDialogService dialogs, ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _sessions = sessions;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
    }

    public FileManagerViewModel Create(Camera camera) =>
        new(camera, _directory, _sessions, _dialogs, _loggerFactory.CreateLogger<FileManagerViewModel>());
}
