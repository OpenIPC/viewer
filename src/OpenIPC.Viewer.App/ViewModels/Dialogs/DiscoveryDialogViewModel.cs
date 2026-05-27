using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Onvif.Discovery;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

// Two-step: (1) Scan multicast WS-Discovery -> list of cameras; (2) user
// picks one, types credentials, we probe ONVIF (capabilities + profiles +
// stream URI) to produce the full result the Library page hands to the
// CameraEditor. Probe is here so failed creds fail fast inside the dialog
// instead of pre-filling the editor with bad data.
public sealed partial class DiscoveryDialogViewModel : ViewModelBase
{
    private readonly IDiscoveryService _discovery;
    private readonly OnvifProbeService _probe;
    private readonly ILogger<DiscoveryDialogViewModel> _logger;

    private CancellationTokenSource? _scanCts;

    public ObservableCollection<DiscoveredCameraRowVm> Cameras { get; } = new();

    [ObservableProperty] private string _statusText = Localizer.Instance["Discovery.Status.Initial"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowSelected))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private DiscoveredCameraRowVm? _selected;

    public bool IsRowSelected => Selected is not null;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _scanInProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _addInProgress;

    public bool CanAdd => Selected is not null && !ScanInProgress && !AddInProgress;

    public DiscoveryDialogViewModel(
        IDiscoveryService discovery,
        OnvifProbeService probe,
        ILogger<DiscoveryDialogViewModel> logger)
    {
        _discovery = discovery;
        _probe = probe;
        _logger = logger;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        Cameras.Clear();
        Selected = null;
        StatusText = Localizer.Instance["Discovery.Status.Scanning"];
        ScanInProgress = true;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            var timeout = TimeSpan.FromSeconds(6);
            await foreach (var cam in _discovery.ScanAsync(timeout, ct).ConfigureAwait(true))
            {
                Cameras.Add(new DiscoveredCameraRowVm(cam));
            }
            StatusText = Cameras.Count == 0
                ? Localizer.Instance["Discovery.Status.NoResponse"]
                : string.Format(Localizer.Instance["Discovery.Status.FoundFormat"], Cameras.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = Localizer.Instance["Discovery.Status.Cancelled"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery scan failed");
            StatusText = string.Format(Localizer.Instance["Discovery.Status.ScanFailedFormat"], ex.Message);
        }
        finally
        {
            ScanInProgress = false;
        }
    }

    private bool CanScan() => !ScanInProgress && !AddInProgress;

    public async Task<DiscoveryDialogResult?> AddSelectedAsync()
    {
        var row = Selected;
        if (row is null) return null;

        AddInProgress = true;
        StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbingFormat"], row.HostPort);

        try
        {
            var creds = string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)
                ? null
                : new CameraCredentials(Username, Password);
            var endpoint = new OnvifEndpoint(row.Camera.DeviceServiceUri, creds);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var probeResult = await _probe.ProbeAsync(endpoint, cts.Token).ConfigureAwait(true);

            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbeOkFormat"], probeResult.Manufacturer ?? "?", probeResult.Model ?? "").TrimEnd();
            return new DiscoveryDialogResult(row.Camera, probeResult, creds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ONVIF probe failed for {Host}", row.HostPort);
            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbeFailedFormat"], ex.Message);
            return null;
        }
        finally
        {
            AddInProgress = false;
        }
    }

    public void Cancel()
    {
        _scanCts?.Cancel();
    }
}

public sealed class DiscoveredCameraRowVm
{
    public DiscoveredCamera Camera { get; }
    public string HostPort => Camera.OnvifPort == 80 ? Camera.Host : $"{Camera.Host}:{Camera.OnvifPort}";
    public string DisplayName => Camera.Name ?? Camera.Model ?? Camera.Host;
    public string Subtitle => Camera.Model ?? Localizer.Instance["Discovery.UnknownModel"];

    public DiscoveredCameraRowVm(DiscoveredCamera camera)
    {
        Camera = camera;
    }
}

public sealed record DiscoveryDialogResult(
    DiscoveredCamera Discovered,
    OnvifProbeResult Probe,
    CameraCredentials? Credentials);
