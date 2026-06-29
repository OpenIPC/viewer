using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Firmware;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

public sealed class FirmwareDialogFactory
{
    private readonly CameraDirectoryService _directory;
    private readonly IFirmwareMaintenanceService _firmware;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;

    public FirmwareDialogFactory(
        CameraDirectoryService directory,
        IFirmwareMaintenanceService firmware,
        IDialogService dialogs,
        ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _firmware = firmware;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
    }

    public FirmwareDialogViewModel Create(Camera camera) =>
        new(camera, _directory, _firmware, _dialogs, _loggerFactory.CreateLogger<FirmwareDialogViewModel>());
}
