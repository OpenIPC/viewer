using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Firmware;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

// Firmware-lite dialog: clock (read / set-from-PC / NTP sync), log snapshots, and
// a guarded reboot — all over the camera's SSH session. One short SSH session per
// op (the service handles that); this VM just sequences them and surfaces status.
public sealed partial class FirmwareDialogViewModel : ViewModelBase
{
    private readonly Camera _camera;
    private readonly CameraDirectoryService _directory;
    private readonly IFirmwareMaintenanceService _firmware;
    private readonly IDialogService _dialogs;
    private readonly ILogger<FirmwareDialogViewModel> _logger;

    private SshEndpoint? _endpoint;

    public string Title => _camera.Name;

    // No SSH credentials on the camera → the whole dialog is inert; show a hint.
    [ObservableProperty] private bool _hasCredentials = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string? _deviceTime;
    [ObservableProperty] private string? _timezone;
    [ObservableProperty] private string? _uptime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNtpCommand))]
    private string _ntpServer = "pool.ntp.org";

    public ObservableCollection<DeviceLogKind> LogKinds { get; } = new()
    {
        DeviceLogKind.Syslog, DeviceLogKind.Kernel, DeviceLogKind.Majestic,
    };

    [ObservableProperty] private DeviceLogKind _selectedLogKind = DeviceLogKind.Syslog;
    [ObservableProperty] private string? _logText;

    public FirmwareDialogViewModel(
        Camera camera,
        CameraDirectoryService directory,
        IFirmwareMaintenanceService firmware,
        IDialogService dialogs,
        ILogger<FirmwareDialogViewModel> logger)
    {
        _camera = camera;
        _directory = directory;
        _firmware = firmware;
        _dialogs = dialogs;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        _endpoint = await _directory.GetSshEndpointAsync(_camera, ct).ConfigureAwait(true);
        if (_endpoint is null)
        {
            HasCredentials = false;
            StatusText = Localizer.Instance["Firmware.NoCredentials"];
            return;
        }
        await RefreshTimeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshTimeAsync() => RunAsync(LoadTimeAsync, "Firmware.Status.TimeLoaded");

    [RelayCommand]
    private Task SetTimeFromHostAsync() => RunAsync(async ep =>
    {
        await _firmware.SetTimeFromHostAsync(ep, DateTimeOffset.Now, CancellationToken.None).ConfigureAwait(true);
        await LoadTimeAsync(ep).ConfigureAwait(true);
    }, "Firmware.Status.TimeSet");

    [RelayCommand(CanExecute = nameof(CanSyncNtp))]
    private Task SyncNtpAsync() => RunAsync(async ep =>
    {
        await _firmware.SyncNtpAsync(ep, NtpServer.Trim(), CancellationToken.None).ConfigureAwait(true);
        await LoadTimeAsync(ep).ConfigureAwait(true);
    }, "Firmware.Status.NtpSynced");

    private bool CanSyncNtp() => FirmwareCommands.IsValidNtpServer(NtpServer?.Trim() ?? "");

    [RelayCommand]
    private Task RefreshLogAsync() => RunAsync(async ep =>
        LogText = await _firmware.ReadLogAsync(ep, SelectedLogKind, CancellationToken.None).ConfigureAwait(true),
        "Firmware.Status.LogLoaded");

    partial void OnSelectedLogKindChanged(DeviceLogKind value)
    {
        if (_endpoint is not null && !IsBusy)
            RefreshLogCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (_endpoint is null) return;

        var ok = await _dialogs.ConfirmAsync(
            Localizer.Instance["Firmware.Reboot.Title"],
            string.Format(Localizer.Instance["Firmware.Reboot.Message"], _camera.Name),
            Localizer.Instance["Firmware.Reboot.Confirm"],
            Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!ok) return;

        await RunAsync(ep => _firmware.RebootAsync(ep, CancellationToken.None), "Firmware.Status.Rebooting")
            .ConfigureAwait(true);
    }

    private async Task LoadTimeAsync(SshEndpoint endpoint)
    {
        var info = await _firmware.GetTimeAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
        DeviceTime = info.DeviceTime;
        Timezone = info.Timezone;
        Uptime = info.Uptime;
    }

    // Single-flight guard + status/error plumbing shared by every op.
    private async Task RunAsync(Func<SshEndpoint, Task> op, string okKey)
    {
        if (_endpoint is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await op(_endpoint).ConfigureAwait(true);
            StatusText = Localizer.Instance[okKey];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firmware op failed for {Camera}", _camera.Name);
            StatusText = string.Format(Localizer.Instance["Firmware.Status.Failed"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
