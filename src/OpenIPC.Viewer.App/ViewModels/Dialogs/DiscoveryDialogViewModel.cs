using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

public enum DiscoveryPhase { Idle, Quick, Deep }

// Finds cameras on the network. Opening the dialog runs a quick passive scan
// (ONVIF + mDNS); an opt-in deep scan walks the addresses of the ticked
// subnets. Each found device is one row with its own "Add", which hands the
// device to the camera editor — identification, credentials and the stream
// check happen there (its Connect). Results survive closing the dialog so
// several cameras go in from one scan.
public sealed partial class DiscoveryDialogViewModel : ViewModelBase
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly OpenIPC.Viewer.Core.Majestic.IMajesticClient _majestic;
    private readonly DiscoverySessionCache _cache;
    private readonly IReadOnlySet<string> _knownHosts;
    private readonly ILogger<DiscoveryDialogViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    // Cancels in-flight Majestic fingerprints when the dialog goes away.
    private readonly CancellationTokenSource _lifetimeCts = new();
    // What the deep scan will walk: the ticked subnets folded together with
    // anything typed. Null = nothing chosen (the sweep then works out the
    // local subnet by itself, when no subnets are offered at all).
    private IpRange? _effectiveRange;
    private readonly Dictionary<string, DiscoveredDeviceRowVm> _rowsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    // Hosts we already fingerprinted (or are fingerprinting) — one ping per host.
    private readonly HashSet<string> _fingerprinted = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fingerprintGate = new(6);
    private DiscoveryPhase _lastScan = DiscoveryPhase.Idle;
    private bool _stopped;
    private string? _failure;

    // New finds first; cameras already in the library collect underneath.
    public ObservableCollection<DiscoveredDeviceRowVm> NewCameras { get; } = new();
    public ObservableCollection<DiscoveredDeviceRowVm> AddedCameras { get; } = new();

    // Subnets offered for the deep scan. Local ones arrive ticked, routed ones
    // (behind a VPN / on another VLAN) don't — sweeping the far side of a
    // tunnel shouldn't happen without a deliberate tick.
    public ObservableCollection<ScanTargetRowVm> ScanTargets { get; } = new();
    public bool HasScanTargets => ScanTargets.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(IsDeepScanning), nameof(IsIdle), nameof(StatusText),
        nameof(ShowEmpty), nameof(ShowEmptyDeepHint), nameof(ShowEmptyManualHint))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand), nameof(DeepScanCommand))]
    private DiscoveryPhase _phase = DiscoveryPhase.Idle;

    public bool IsScanning => Phase != DiscoveryPhase.Idle;
    public bool IsDeepScanning => Phase == DiscoveryPhase.Deep;
    public bool IsIdle => Phase == DiscoveryPhase.Idle;

    // 0..1, mean across sources.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _scanProgress;

    // The deep-scan section is expanded.
    [ObservableProperty] private bool _isDeepOpen;

    // Extra hand-typed range on top of the ticked subnets.
    [ObservableProperty] private string _ipRangeText = "";

    // Null while the ticked + typed targets add up to something sweepable.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeepScanCommand))]
    private string? _ipRangeError;

    // "508 addresses" / "this computer's subnet".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepButtonLabel))]
    private string _sweepSummary = "";

    public string DeepButtonLabel => string.IsNullOrEmpty(SweepSummary)
        ? Localizer.Instance["Discovery.Deep.Start"]
        : $"{Localizer.Instance["Discovery.Deep.Start"]} · {SweepSummary}";

    public bool HasNew => NewCameras.Count > 0;
    public bool HasAdded => AddedCameras.Count > 0;
    // Not after a manual Stop: nobody failed to answer, the user cut it short.
    public bool ShowEmpty => IsIdle && _lastScan != DiscoveryPhase.Idle && !_stopped && !HasNew && !HasAdded;
    public bool ShowEmptyDeepHint => ShowEmpty && _lastScan == DiscoveryPhase.Quick;
    public bool ShowEmptyManualHint => ShowEmpty && _lastScan == DiscoveryPhase.Deep;

    public string StatusText
    {
        get
        {
            var total = NewCameras.Count + AddedCameras.Count;
            switch (Phase)
            {
                case DiscoveryPhase.Quick:
                    return Localizer.Instance["Discovery.Status.Quick"];
                case DiscoveryPhase.Deep:
                    return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Discovery.Status.DeepFormat"],
                        (int)Math.Round(ScanProgress * 100), total);
            }
            if (_failure is not null) return _failure;
            if (_lastScan == DiscoveryPhase.Idle) return "";
            if (total == 0)
                return Localizer.Instance[_stopped ? "Discovery.Status.Stopped"
                    : _lastScan == DiscoveryPhase.Deep ? "Discovery.Status.NothingDeep" : "Discovery.Status.NothingQuick"];
            var found = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Discovery.Status.FoundFormat"], total);
            return AddedCameras.Count == 0
                ? found
                : found + " · " + string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Discovery.Status.InLibraryFormat"], AddedCameras.Count);
        }
    }

    public DiscoveryDialogViewModel(
        IDiscoveryAggregator aggregator,
        OpenIPC.Viewer.Core.Majestic.IMajesticClient majestic,
        IScanTargetProvider scanTargets,
        DiscoverySessionCache cache,
        IReadOnlySet<string> knownHosts,
        ILogger<DiscoveryDialogViewModel> logger)
    {
        _aggregator = aggregator;
        _majestic = majestic;
        _cache = cache;
        _knownHosts = knownHosts;
        _logger = logger;

        _isDeepOpen = cache.DeepScan;
        _ipRangeText = cache.IpRangeText;

        // A choice the user already made this session wins over the default.
        foreach (var target in SafeTargets(scanTargets))
        {
            var selected = cache.TargetSelections.TryGetValue(target.Cidr, out var choice)
                ? choice
                : target.Origin == ScanTargetOrigin.LocalSubnet;
            ScanTargets.Add(new ScanTargetRowVm(target, selected, OnTargetToggled));
        }

        // Field init skips the generated On*Changed hooks, so the rehydrated
        // state has to be folded in by hand.
        RecomputeRange();
        foreach (var device in cache.Snapshot())
            Upsert(device);
        if (NewCameras.Count + AddedCameras.Count > 0)
            _lastScan = DiscoveryPhase.Quick;
    }

    // Called when the dialog opens: a fresh session scans straight away; a
    // reopened one (multi-add) shows what it already found.
    public Task StartAsync() =>
        _lastScan == DiscoveryPhase.Idle && !IsScanning ? ScanAsync(deep: false) : Task.CompletedTask;

    partial void OnIsDeepOpenChanged(bool value) => _cache.DeepScan = value;

    partial void OnIpRangeTextChanged(string value)
    {
        _cache.IpRangeText = value;
        RecomputeRange();
    }

    private void OnTargetToggled(ScanTargetRowVm row)
    {
        _cache.TargetSelections[row.Target.Cidr] = row.IsSelected;
        RecomputeRange();
    }

    // Folds the ticked subnets and the typed range into the single range the
    // sweep walks. Runs per keystroke and per tick — pure arithmetic.
    private void RecomputeRange()
    {
        IpRange? typed = null;
        if (!string.IsNullOrWhiteSpace(IpRangeText)
            && !IpRange.TryParse(IpRangeText, out typed, out var typedError))
        {
            Reject(typedError);
            return;
        }

        var parts = new List<IpRange>();
        if (typed is not null)
            parts.Add(typed);
        parts.AddRange(ScanTargets.Where(t => t.IsSelected).Select(t => t.Target.Range));

        if (parts.Count == 0)
        {
            _effectiveRange = null;
            // No subnets offered at all: the sweep works out the local /24 itself.
            SweepSummary = ScanTargets.Count == 0 ? Localizer.Instance["Discovery.Sweep.SummaryLocal"] : "";
            IpRangeError = ScanTargets.Count == 0 ? null : Localizer.Instance["Discovery.Deep.NothingTicked"];
            return;
        }

        if (!IpRange.TryCombine(parts, out var combined, out var combineError))
        {
            Reject(combineError);
            return;
        }

        _effectiveRange = combined;
        SweepSummary = AddressCount(combined.Count);
        IpRangeError = null;
    }

    // "1 address" / "5 addresses"; Russian has three forms (1 / 2–4 / 5+).
    private static string AddressCount(int n)
    {
        var key = "Discovery.Sweep.Many";
        if (Localizer.Instance.Active == LangCode.Russian)
        {
            var mod100 = n % 100;
            var mod10 = n % 10;
            if (mod10 == 1 && mod100 != 11) key = "Discovery.Sweep.One";
            else if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) key = "Discovery.Sweep.Few";
        }
        else if (n == 1)
        {
            key = "Discovery.Sweep.One";
        }
        return string.Format(CultureInfo.CurrentCulture, Localizer.Instance[key], n);
    }

    private void Reject(IpRangeParseError error)
    {
        _effectiveRange = null;
        SweepSummary = "";
        IpRangeError = error == IpRangeParseError.TooLarge
            ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Discovery.IpRange.TooLargeFormat"], IpRange.MaxHosts)
            : Localizer.Instance["Discovery.IpRange.Invalid"];
    }

    // A provider that trips over an exotic adapter must not stop the dialog
    // from opening — the manual range still works with no targets at all.
    private IReadOnlyList<ScanTarget> SafeTargets(IScanTargetProvider provider)
    {
        try
        {
            return provider.GetTargets();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating scan targets failed");
            return Array.Empty<ScanTarget>();
        }
    }

    private bool CanRescan() => !IsScanning;
    private bool CanDeepScan() => !IsScanning && IpRangeError is null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => ScanAsync(deep: false);

    [RelayCommand(CanExecute = nameof(CanDeepScan))]
    private Task DeepScanAsync()
    {
        IsDeepOpen = true;
        return ScanAsync(deep: true);
    }

    [RelayCommand]
    private void Stop() => _scanCts?.Cancel();

    [RelayCommand]
    private void ToggleDeep() => IsDeepOpen = !IsDeepOpen;

    // Quick: passive sources only, starts the list over. Deep: adds the
    // address sweep and keeps what's already listed.
    private async Task ScanAsync(bool deep)
    {
        if (!deep)
        {
            NewCameras.Clear();
            AddedCameras.Clear();
            _rowsByHost.Clear();
            _fingerprinted.Clear();
            _cache.Clear();
            ListChanged();
        }
        _stopped = false;
        _failure = null;
        ScanProgress = 0;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        Phase = deep ? DiscoveryPhase.Deep : DiscoveryPhase.Quick;

        try
        {
            var options = new DiscoveryOptions(TimeSpan.FromSeconds(6), deep, deep ? _effectiveRange : null);
            var progress = new Progress<double>(p => ScanProgress = p);
            await foreach (var device in _aggregator.ScanAsync(options, progress, ct).ConfigureAwait(true))
                Upsert(device);
        }
        catch (OperationCanceledException)
        {
            _stopped = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery scan failed");
            _failure = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Discovery.Status.ScanFailedFormat"], ex.Message);
        }
        finally
        {
            _lastScan = deep ? DiscoveryPhase.Deep : DiscoveryPhase.Quick;
            ScanProgress = 0;
            Phase = DiscoveryPhase.Idle;
        }
    }

    // Merge-by-host upsert: a device can be yielded repeatedly as more sources
    // confirm it, so update the existing row in place instead of duplicating.
    private void Upsert(DiscoveredDevice device)
    {
        if (_rowsByHost.TryGetValue(device.Host, out var row))
        {
            row.Device = row.Device.MergeWith(device);
        }
        else
        {
            row = new DiscoveredDeviceRowVm(device)
            {
                IsAlreadyAdded = _knownHosts.Contains(device.Host),
            };
            _rowsByHost[device.Host] = row;
            (row.IsAlreadyAdded ? AddedCameras : NewCameras).Add(row);
            ListChanged();
        }

        _cache.Put(row.Device);
        ScheduleFingerprint(row.Device);
    }

    private void ListChanged()
    {
        OnPropertyChanged(nameof(HasNew));
        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowEmptyDeepHint));
        OnPropertyChanged(nameof(ShowEmptyManualHint));
    }

    // Newer OpenIPC firmwares always run the Majestic web UI, so an HTTP ping
    // identifies them even when they answer neither ONVIF nor mDNS with a
    // model. One bounded background ping per host; on a hit the row upgrades
    // in place (Majestic protocol + "OpenIPC" label).
    private void ScheduleFingerprint(DiscoveredDevice device)
    {
        if (device.Protocols.HasFlag(DiscoveryProtocol.Majestic) && device.Model is not null)
            return;
        if (!_fingerprinted.Add(device.Host))
            return;

        var ct = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            await _fingerprintGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var hit = device.Protocols.HasFlag(DiscoveryProtocol.Majestic);
                var ports = device.Ports.Where(p => p is 80 or 8080).DefaultIfEmpty(80);
                foreach (var port in ports)
                {
                    if (hit) break;
                    hit = await _majestic.PingAsync(
                        new OpenIPC.Viewer.Core.Majestic.MajesticEndpoint(device.Host, port, null), ct).ConfigureAwait(false);
                }
                if (!hit) return;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_rowsByHost.TryGetValue(device.Host, out var row)) return;
                    row.Device = row.Device.MergeWith(new DiscoveredDevice(
                        device.Host, DiscoveryProtocol.Majestic, Array.Empty<int>(), Model: "OpenIPC"));
                    _cache.Put(row.Device);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Majestic fingerprint failed for {Host}", device.Host);
            }
            finally
            {
                _fingerprintGate.Release();
            }
        }, ct);
    }

    // The row's "Add": hand the device (and the login last used this session)
    // to the camera editor.
    public DiscoveryDialogResult BuildResult(DiscoveredDeviceRowVm row)
    {
        var creds = string.IsNullOrEmpty(_cache.Username) && string.IsNullOrEmpty(_cache.Password)
            ? null
            : new CameraCredentials(_cache.Username, _cache.Password);
        return new DiscoveryDialogResult(row.Device, creds);
    }

    public void Cancel()
    {
        _scanCts?.Cancel();
        _lifetimeCts.Cancel();
    }
}

// One auto-detected subnet in the deep-scan tick list. The callback (rather
// than the VM watching the collection) keeps the "remember what I unticked"
// bookkeeping in one place.
public sealed partial class ScanTargetRowVm : ViewModelBase
{
    private readonly Action<ScanTargetRowVm> _onToggled;

    public ScanTargetRowVm(ScanTarget target, bool isSelected, Action<ScanTargetRowVm> onToggled)
    {
        Target = target;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    public ScanTarget Target { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);

    public string Cidr => Target.Cidr;

    // "Ethernet 2 · this network" / "through a VPN or route".
    public string Detail => string.Format(
        CultureInfo.CurrentCulture,
        Localizer.Instance["Discovery.Target.DetailFormat"],
        Target.InterfaceName,
        Localizer.Instance[Target.Origin == ScanTargetOrigin.LocalSubnet
            ? "Discovery.Target.Local"
            : "Discovery.Target.Routed"]);
}

public enum DiscoveredKind { OpenIpc, Onvif, Rtsp, Other }

public sealed partial class DiscoveredDeviceRowVm : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsOpenIpc), nameof(IsOnvif), nameof(IsGeneric),
        nameof(Title), nameof(Subtitle), nameof(HostPort), nameof(Tags))]
    private DiscoveredDevice _device;

    // A camera with this host already exists in the library.
    [ObservableProperty] private bool _isAlreadyAdded;

    public DiscoveredDeviceRowVm(DiscoveredDevice device) => _device = device;

    public string Host => Device.Host;

    public DiscoveredKind Kind =>
        Device.Protocols.HasFlag(DiscoveryProtocol.Majestic) ? DiscoveredKind.OpenIpc
        : Device.Protocols.HasFlag(DiscoveryProtocol.Onvif) ? DiscoveredKind.Onvif
        : Device.Protocols.HasFlag(DiscoveryProtocol.Rtsp) ? DiscoveredKind.Rtsp
        : DiscoveredKind.Other;

    public bool IsOpenIpc => Kind == DiscoveredKind.OpenIpc;
    public bool IsOnvif => Kind == DiscoveredKind.Onvif;
    public bool IsGeneric => Kind is DiscoveredKind.Rtsp or DiscoveredKind.Other;

    public string Title => Device.Model ?? Device.Name ?? Localizer.Instance[Kind switch
    {
        DiscoveredKind.OpenIpc => "Discovery.Kind.OpenIpc",
        DiscoveredKind.Onvif => "Discovery.Kind.Onvif",
        _ => "Discovery.Kind.Unknown",
    }];

    // What kind of thing it is, in words (the model is already the title).
    public string Subtitle => Kind switch
    {
        DiscoveredKind.OpenIpc => "OpenIPC · Majestic",
        DiscoveredKind.Onvif => Device.Name is { } n && n != Device.Model ? $"ONVIF · {n}" : Localizer.Instance["Discovery.Kind.Onvif"],
        DiscoveredKind.Rtsp => Localizer.Instance["Discovery.Kind.RtspOnly"],
        _ => Localizer.Instance["Discovery.Kind.HttpOnly"],
    };

    public string HostPort
    {
        get
        {
            var port = Device.OnvifServiceUri?.Port ?? 0;
            return port is 0 or 80 ? Device.Host : $"{Device.Host}:{port}";
        }
    }

    // How the device was found, as small tags.
    public IReadOnlyList<string> Tags
    {
        get
        {
            var p = Device.Protocols;
            var tags = new List<string>();
            if (p.HasFlag(DiscoveryProtocol.Majestic)) tags.Add("OpenIPC");
            if (p.HasFlag(DiscoveryProtocol.Onvif)) tags.Add("ONVIF");
            if (p.HasFlag(DiscoveryProtocol.Mdns)) tags.Add("mDNS");
            if (p.HasFlag(DiscoveryProtocol.Rtsp)) tags.Add("RTSP");
            return tags;
        }
    }
}

// The dialog's output: the picked device and the login last used this session
// (the editor pre-fills and connects with it) — or ManualEntry when the user
// chose to type an address instead.
public sealed record DiscoveryDialogResult(
    DiscoveredDevice? Device,
    CameraCredentials? Credentials,
    bool ManualEntry = false);
