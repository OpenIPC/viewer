using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.App.Services;

public sealed class SshTerminalFactory
{
    private readonly CameraDirectoryService _directory;
    private readonly ISshSessionFactory _sessions;
    private readonly UserSettingsService _settings;
    private readonly ILoggerFactory _loggerFactory;

    public SshTerminalFactory(CameraDirectoryService directory, ISshSessionFactory sessions, UserSettingsService settings, ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _sessions = sessions;
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    public SshTerminalViewModel Create(Camera camera) =>
        new(camera, _directory, _sessions, _settings.Current.SshTerminalFontSize, _loggerFactory.CreateLogger<SshTerminalViewModel>());
}
