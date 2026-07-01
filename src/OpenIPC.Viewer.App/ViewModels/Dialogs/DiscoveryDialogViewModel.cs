using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

// Two-step: (1) scan via the aggregator (ONVIF + later sweep/mDNS) -> merged
// devices, upserted by host as signals arrive; (2) user picks one, types creds,
// we ONVIF-probe (capabilities + profiles + stream URI) to produce the result
// the Library hands to the CameraEditor. Probe fails fast inside the dialog
// instead of pre-filling the editor with bad data.
public sealed partial class DiscoveryDialogViewModel : ViewModelBase
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly OnvifProbeService _probe;
    private readonly ILogger<DiscoveryDialogViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    private readonly Dictionary<string, DiscoveredDeviceRowVm> _rowsByHost =
        new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DiscoveredDeviceRowVm> Cameras { get; } = new();

    [ObservableProperty] private string _statusText = Localizer.Instance["Discovery.Status.Initial"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowSelected))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private DiscoveredDeviceRowVm? _selected;

    public bool IsRowSelected => Selected is not null;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    // 0..1 scan progress (mean across sources). Drives the progress bar; hidden
    // when not scanning.
    [ObservableProperty] private double _scanProgress;

    // Opt-in active /24 sweep — finds OpenIPC cameras that answer neither ONVIF
    // nor mDNS, at the cost of knocking on every host. Off by default.
    [ObservableProperty] private bool _deepScan;

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
        IDiscoveryAggregator aggregator,
        OnvifProbeService probe,
        ILogger<DiscoveryDialogViewModel> logger)
    {
        _aggregator = aggregator;
        _probe = probe;
        _logger = logger;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        Cameras.Clear();
        _rowsByHost.Clear();
        Selected = null;
        ScanProgress = 0;
        StatusText = Localizer.Instance["Discovery.Status.Scanning"];
        ScanInProgress = true;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            var options = new DiscoveryOptions(TimeSpan.FromSeconds(6), DeepScan);
            var progress = new Progress<double>(p => ScanProgress = p);

            await foreach (var device in _aggregator.ScanAsync(options, progress, ct).ConfigureAwait(true))
                Upsert(device);

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
            ScanProgress = 0;
        }
    }

    // Merge-by-host upsert: a device can be yielded repeatedly as more sources
    // confirm it, so update the existing row in place instead of duplicating.
    private void Upsert(DiscoveredDevice device)
    {
        if (_rowsByHost.TryGetValue(device.Host, out var row))
        {
            row.Device = device;
        }
        else
        {
            row = new DiscoveredDeviceRowVm(device);
            _rowsByHost[device.Host] = row;
            Cameras.Add(row);
        }
    }

    private bool CanScan() => !ScanInProgress && !AddInProgress;

    public async Task<DiscoveryDialogResult?> AddSelectedAsync()
    {
        var row = Selected;
        if (row is null) return null;

        var creds = string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)
            ? null
            : new CameraCredentials(Username, Password);

        AddInProgress = true;
        try
        {
            // ONVIF device → probe for the real stream URI. Non-ONVIF (sweep/mDNS)
            // → skip the probe and pre-fill a guessed RTSP URL from the open ports;
            // the user reviews / tests it in the editor before saving.
            var onvifUri = row.Device.OnvifServiceUri;
            if (onvifUri is null)
            {
                StatusText = Localizer.Instance["Discovery.Status.ManualAdd"];
                return new DiscoveryDialogResult(row.Device, GuessRtspUri(row.Device), null, creds);
            }

            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbingFormat"], row.HostPort);
            var endpoint = new OnvifEndpoint(onvifUri, creds);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var probeResult = await _probe.ProbeAsync(endpoint, cts.Token).ConfigureAwait(true);

            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbeOkFormat"], probeResult.Manufacturer ?? "?", probeResult.Model ?? "").TrimEnd();
            return new DiscoveryDialogResult(row.Device, probeResult.RtspMainUri, probeResult, creds);
        }
        catch (OperationCanceledException)
        {
            StatusText = Localizer.Instance["Discovery.Status.Cancelled"];
            return null;
        }
        catch (Exception ex)
        {
            // The ONVIF SOAP probe can't run on every platform — the WCF
            // XmlSerializer stack fails to build on Android (XmlType reflection
            // error over the generated contract types). Rather than dead-end the
            // user, degrade to the same guessed-RTSP add we use for non-ONVIF
            // finds: the camera is added and refined/tested in the editor.
            _logger.LogWarning(ex, "ONVIF probe failed for {Host}; falling back to guessed RTSP", row.HostPort);
            StatusText = Localizer.Instance["Discovery.Status.ManualAdd"];
            return new DiscoveryDialogResult(row.Device, GuessRtspUri(row.Device), null, creds);
        }
        finally
        {
            AddInProgress = false;
        }
    }

    // OpenIPC/Majestic RTSP convention (matches CameraEditor): rtsp://host/ for
    // the default 554, an explicit port otherwise. The user can refine it.
    private static Uri GuessRtspUri(DiscoveredDevice device)
    {
        var port = device.Ports.Contains(8554) && !device.Ports.Contains(554) ? 8554 : 554;
        return port == 554
            ? new Uri($"rtsp://{device.Host}/")
            : new Uri($"rtsp://{device.Host}:{port}/");
    }

    public void Cancel()
    {
        _scanCts?.Cancel();
    }
}

public sealed partial class DiscoveredDeviceRowVm : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(HostPort))]
    [NotifyPropertyChangedFor(nameof(ProtocolsText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    private DiscoveredDevice _device;

    public DiscoveredDeviceRowVm(DiscoveredDevice device) => _device = device;

    public string Host => Device.Host;
    public string DisplayName => Device.Name ?? Device.Model ?? Device.Host;
    public string Subtitle => Device.Model ?? Localizer.Instance["Discovery.UnknownModel"];

    public string HostPort
    {
        get
        {
            var port = Device.OnvifServiceUri?.Port ?? (Device.Ports.Count > 0 ? Device.Ports.First() : 0);
            return port is 0 or 80 ? Device.Host : $"{Device.Host}:{port}";
        }
    }

    // e.g. "ONVIF · RTSP" — how the device was detected.
    public string ProtocolsText => string.Join(" · ", DescribeProtocols(Device.Protocols));

    public string ConfidenceText => Localizer.Instance[Device.Confidence switch
    {
        DiscoveryConfidence.High => "Discovery.Confidence.High",
        DiscoveryConfidence.Medium => "Discovery.Confidence.Medium",
        _ => "Discovery.Confidence.Low",
    }];

    private static IEnumerable<string> DescribeProtocols(DiscoveryProtocol p)
    {
        if (p.HasFlag(DiscoveryProtocol.Onvif)) yield return "ONVIF";
        if (p.HasFlag(DiscoveryProtocol.Mdns)) yield return "mDNS";
        if (p.HasFlag(DiscoveryProtocol.Majestic)) yield return "Majestic";
        if (p.HasFlag(DiscoveryProtocol.Rtsp)) yield return "RTSP";
        if (p.HasFlag(DiscoveryProtocol.Http)) yield return "HTTP";
    }
}

// The dialog's output: the picked device, the RTSP URL to pre-fill (from the
// ONVIF probe when available, otherwise a sensible guess for non-ONVIF devices),
// the ONVIF probe result (null for non-ONVIF — no PTZ/profile metadata), and any
// credentials the user typed.
public sealed record DiscoveryDialogResult(
    DiscoveredDevice Device,
    Uri RtspMainUri,
    OnvifProbeResult? Probe,
    CameraCredentials? Credentials);
