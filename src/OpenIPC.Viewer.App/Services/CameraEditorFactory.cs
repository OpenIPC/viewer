using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public sealed class CameraEditorFactory
{
    private readonly IVideoEngine _engine;
    private readonly CameraDirectoryService _directory;
    private readonly UserSettingsService _userSettings;
    private readonly ILoggerFactory _loggerFactory;

    public CameraEditorFactory(IVideoEngine engine, CameraDirectoryService directory, UserSettingsService userSettings, ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _directory = directory;
        _userSettings = userSettings;
        _loggerFactory = loggerFactory;
    }

    public CameraEditorViewModel CreateForNew() =>
        new(_engine, _directory, _userSettings, _loggerFactory.CreateLogger<CameraEditorViewModel>());

    public CameraEditorViewModel CreateForEdit(Camera existing, CameraCredentials? credentials) =>
        new(existing, credentials, _engine, _directory, _userSettings, _loggerFactory.CreateLogger<CameraEditorViewModel>());
}
