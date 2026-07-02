using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Status;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraLibraryPageViewModel : ViewModelBase, IRecipient<ConfigImportedMessage>
{
    private readonly CameraDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly CameraEditorFactory _editorFactory;
    private readonly DiscoveryDialogFactory _discoveryFactory;
    private readonly SshTerminalFactory _terminalFactory;
    private readonly FileManagerFactory _fileManagerFactory;
    private readonly FirmwareDialogFactory _firmwareFactory;
    private readonly ILogger<CameraLibraryPageViewModel> _logger;

    public string Title => Localizer.Instance["Library.Title"];
    public ObservableCollection<CameraRowViewModel> Cameras { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameras))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    // Gates the centered loader. Empty-state is also suppressed while loading so
    // refresh doesn't flash "No cameras yet" between Clear() and Add().
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    public bool HasCameras => IsLoaded && Cameras.Count > 0;
    public bool IsEmpty => IsLoaded && !IsLoading && Cameras.Count == 0;

    private readonly UserSettingsService _userSettings;
    private readonly IDiscoveryService _discovery;
    private readonly IReachabilityProbe _reachability;
    private readonly CameraStatusRegistry _statusRegistry;
    private readonly ManageGroupsDialogFactory _manageGroupsFactory;
    private bool _autoScanRanThisSession;
    private System.Collections.Generic.IReadOnlyList<Camera> _allCameras = System.Array.Empty<Camera>();
    // CameraIds in the active layout (Phase 19.1) — drives the "in grid" checkbox.
    private System.Collections.Generic.HashSet<CameraId> _gridMembership = new();

    public ObservableCollection<CameraGroup?> AvailableGroups { get; } = new();

    [ObservableProperty] private CameraGroup? _selectedGroupFilter;

    partial void OnSelectedGroupFilterChanged(CameraGroup? value) => RefilterCameras();

    public CameraLibraryPageViewModel(
        CameraDirectoryService directory,
        IDialogService dialogs,
        CameraEditorFactory editorFactory,
        DiscoveryDialogFactory discoveryFactory,
        ManageGroupsDialogFactory manageGroupsFactory,
        SshTerminalFactory terminalFactory,
        FileManagerFactory fileManagerFactory,
        FirmwareDialogFactory firmwareFactory,
        UserSettingsService userSettings,
        IDiscoveryService discovery,
        IReachabilityProbe reachability,
        CameraStatusRegistry statusRegistry,
        ILogger<CameraLibraryPageViewModel> logger)
    {
        _directory = directory;
        _dialogs = dialogs;
        _editorFactory = editorFactory;
        _discoveryFactory = discoveryFactory;
        _terminalFactory = terminalFactory;
        _fileManagerFactory = fileManagerFactory;
        _firmwareFactory = firmwareFactory;
        _manageGroupsFactory = manageGroupsFactory;
        _userSettings = userSettings;
        _discovery = discovery;
        _reachability = reachability;
        _statusRegistry = statusRegistry;
        _logger = logger;
        WeakReferenceMessenger.Default.Register<ConfigImportedMessage>(this);
        // Toggling "risky device tools" in Settings shows/hides the Files button
        // without reopening the page. The update can arrive off the UI thread.
        _userSettings.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => OnPropertyChanged(nameof(IsFileManagerEnabled)));
        // Live status: a grid session's verdict (incl. Attention) flows here so a
        // library row reflects it, not just its own probe.
        _statusRegistry.Changed += OnStatusRegistryChanged;
        Cameras.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCameras));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    // Registry verdict moved for some camera — push it onto the matching row.
    // Marshalled to the UI thread since the report can come off a session thread.
    private void OnStatusRegistryChanged(object? sender, CameraStatusSnapshot snapshot)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var row in Cameras)
            {
                if (row.Camera.Id == snapshot.CameraId)
                {
                    row.ApplyStatus(snapshot.Result.Status);
                    break;
                }
            }
        });
    }

    // Config import (Phase 19.2): reload so imported cameras appear immediately.
    public async void Receive(ConfigImportedMessage message)
    {
        try { await LoadAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Library reload after import failed"); }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);
            // "In grid" now means membership in the active layout (Phase 19.1),
            // so the checkbox reflects the tab the grid is currently showing.
            var members = await _directory.GetActiveLayoutCameraIdsAsync(ct).ConfigureAwait(true);
            _gridMembership = new System.Collections.Generic.HashSet<CameraId>(members);
            await ReloadGroupsAsync(ct).ConfigureAwait(true);
            RefilterCameras();
            IsLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }

        // First-run welcome — only the very first time the library opens
        // empty. WelcomeShown persists across launches; the user can't be
        // nagged again after they've dismissed it once, even if they later
        // delete all cameras.
        if (Cameras.Count == 0 && !_userSettings.Current.WelcomeShown)
            await ShowWelcomeAsync().ConfigureAwait(true);

        if (_userSettings.Current.AutoScanLanOnStartup && !_autoScanRanThisSession)
            _ = MaybeAutoScanAsync();
    }

    private async Task MaybeAutoScanAsync()
    {
        _autoScanRanThisSession = true;
        try
        {
            // Existing host names already in library — discovery candidates
            // matching one of these are dropped before any UI prompt.
            var existing = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Cameras) existing.Add(row.Camera.Host);

            var found = new System.Collections.Generic.List<string>();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
            await foreach (var dc in _discovery.ScanAsync(TimeSpan.FromSeconds(4), cts.Token).ConfigureAwait(true))
            {
                if (existing.Add(dc.Host))
                    found.Add(dc.Host);
            }

            if (found.Count == 0)
            {
                _logger.LogInformation("Auto-scan: no new cameras on the LAN");
                return;
            }

            _logger.LogInformation("Auto-scan: {Count} new camera(s) on the LAN: {Hosts}", found.Count, string.Join(", ", found));

            var open = await _dialogs.ConfirmAsync(
                title: Localizer.Instance["Library.Dialog.NewCamerasTitle"],
                message: string.Format(Localizer.Instance["Library.Dialog.NewCamerasMessage"], found.Count),
                confirmLabel: Localizer.Instance["Library.Dialog.OpenDiscovery"],
                cancelLabel: Localizer.Instance["Library.Dialog.NotNow"]).ConfigureAwait(true);

            if (open)
                await DiscoverCameraAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-scan failed");
        }
    }

    private async Task ShowWelcomeAsync()
    {
        // Mark "shown" up front so a dialog crash doesn't loop us back into
        // the prompt on every refresh. If the user picks an action, we run
        // the matching command after persisting.
        await _userSettings.UpdateAsync(_userSettings.Current with { WelcomeShown = true })
            .ConfigureAwait(true);

        var pick = await _dialogs.ShowWelcomeAsync().ConfigureAwait(true);
        switch (pick)
        {
            case WelcomeResult.Discover:
                await DiscoverCameraAsync().ConfigureAwait(true);
                break;
            case WelcomeResult.ScanQr:
                await ScanQrAsync().ConfigureAwait(true);
                break;
            case WelcomeResult.AddManually:
                await AddCameraAsync().ConfigureAwait(true);
                break;
            // Skip → nothing.
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    private async Task ReloadGroupsAsync(CancellationToken ct)
    {
        var groups = await _directory.ListGroupsAsync(ct).ConfigureAwait(true);
        // Preserve the current selection's Id across reloads (record identity
        // changes when we re-query the DB).
        var prevId = SelectedGroupFilter?.Id;
        AvailableGroups.Clear();
        AvailableGroups.Add(null); // "All groups"
        foreach (var g in groups) AvailableGroups.Add(g);

        if (prevId is { } id)
        {
            foreach (var g in AvailableGroups)
                if (g is not null && g.Id.Equals(id)) { SelectedGroupFilter = g; return; }
        }
        SelectedGroupFilter = null;
    }

    private void RefilterCameras()
    {
        var filtered = SelectedGroupFilter is null
            ? _allCameras
            : (System.Collections.Generic.IReadOnlyList<Camera>)
                System.Linq.Enumerable.ToList(
                    System.Linq.Enumerable.Where(_allCameras, c => c.GroupId.Equals(SelectedGroupFilter.Id)));

        Cameras.Clear();
        foreach (var camera in filtered)
        {
            var row = new CameraRowViewModel(camera, _directory, _reachability, _statusRegistry, _logger, _gridMembership.Contains(camera.Id));
            // Seed from whatever the registry already knows (e.g. a live grid session).
            row.ApplyStatus(_statusRegistry.Get(camera.Id).Status);
            Cameras.Add(row);
        }

        // Kick off reachability probes for the freshly-built rows. Fire-and-forget:
        // each row updates its own Status independently, in parallel.
        _ = ProbeReachabilityAsync();
    }

    /// <summary>
    /// Re-runs reachability probes for the rows already on screen. Called by
    /// the view on every Loaded — the full LoadAsync only runs once (IsLoaded
    /// gate), so without this a status probed before e.g. a Wi-Fi hiccup
    /// stayed OFFLINE forever while the stream itself played fine.
    /// </summary>
    public Task ReprobeReachabilityAsync() => ProbeReachabilityAsync();

    private async Task ProbeReachabilityAsync()
    {
        var rows = new System.Collections.Generic.List<CameraRowViewModel>(Cameras);
        var tasks = new System.Collections.Generic.List<Task>(rows.Count);
        foreach (var row in rows)
            tasks.Add(row.RefreshReachabilityAsync(CancellationToken.None));
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ManageGroupsAsync()
    {
        var vm = _manageGroupsFactory.Create();
        await _dialogs.ShowManageGroupsAsync(vm).ConfigureAwait(true);
        // After the dialog closes, refresh both lists — groups may have been
        // added/renamed/removed; cameras' GroupId might have been orphaned.
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenCamera(CameraRowViewModel? row)
    {
        if (row is null)
            return;
        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(row.Camera.Id));
    }

    [RelayCommand]
    private async Task AddCameraAsync()
    {
        var editor = _editorFactory.CreateForNew();
        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.NewRequest is not { } req)
            return;

        try
        {
            await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add camera {Name}", req.Name);
        }
    }

    [RelayCommand]
    private async Task DiscoverCameraAsync()
    {
        // Multi-add loop: after each camera is saved (or its editor cancelled)
        // the discovery dialog reopens with the SAME scan results and creds
        // (DiscoverySessionCache), so several cameras go in from one scan.
        // Only cancelling the discovery dialog itself exits.
        var knownHosts = new System.Collections.Generic.HashSet<string>(
            System.Linq.Enumerable.Select(_allCameras, c => c.Host),
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var discoveryVm = _discoveryFactory.Create(knownHosts);
            var found = await _dialogs.ShowDiscoveryDialogAsync(discoveryVm).ConfigureAwait(true);
            // Stop background fingerprints of the instance that just closed.
            discoveryVm.Cancel();
            if (found is null)
                return;

            // Pre-fill the editor from the probe result so the user sees / can tweak
            // everything before saving (RTSP URI especially — phase-04 risks §"ONVIF
            // returns wrong RTSP URI behind NAT" applies).
            var editor = _editorFactory.CreateForNew();
            editor.Name = found.Device.Model ?? found.Device.Name ?? found.Device.Host;
            editor.Host = found.Device.Host;
            editor.OnvifPortText = (found.Device.OnvifServiceUri?.Port ?? 80).ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.RtspMainText = found.RtspMainUri.ToString();
            editor.Username = found.Credentials?.Username ?? "";
            editor.Password = found.Credentials?.Password ?? "";

            var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
            if (result?.NewRequest is not { } req)
                continue; // editor cancelled — back to the scan list

            try
            {
                var id = await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
                // Persist HasPtz / ProfileToken / manufacturer info from the probe so
                // SingleCameraPage knows whether to show the PTZ joystick (Phase 4c).
                // Non-ONVIF devices (sweep/mDNS) have no probe — nothing to persist.
                if (found.Probe is { } probe)
                    await _directory.SaveOnvifMetadataAsync(id, probe, CancellationToken.None).ConfigureAwait(true);
                await LoadAsync(CancellationToken.None).ConfigureAwait(true);
                knownHosts.Add(req.Host);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add discovered camera {Host}", req.Host);
            }
        }
    }

    [RelayCommand]
    private async Task ScanQrAsync()
    {
        // Desktop-only flow: pick a saved QR image, decode it via ZXing, parse
        // one of the three supported payload shapes, pre-fill CameraEditor so
        // the user reviews + confirms before save. Mobile in-camera scan is a
        // follow-up (phase-11.2 spec).
        var path = await _dialogs.PickImageFileAsync(Localizer.Instance["Library.ScanQr.PickerTitle"]).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;

        string? text;
        try
        {
            text = await QrImageDecoder.DecodeAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QR decode failed");
            await _dialogs.ConfirmAsync(
                title: Localizer.Instance["Library.ScanQr.DecodeFailedTitle"],
                message: string.Format(Localizer.Instance["Library.ScanQr.DecodeFailedFormat"], ex.Message),
                confirmLabel: Localizer.Instance["Common.Cancel"],
                cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            await _dialogs.ConfirmAsync(
                title: Localizer.Instance["Library.ScanQr.NoQrTitle"],
                message: Localizer.Instance["Library.ScanQr.NoQrMessage"],
                confirmLabel: Localizer.Instance["Common.Cancel"],
                cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
            return;
        }

        var payload = QrPayloadParser.TryParse(text);
        if (payload is null)
        {
            await _dialogs.ConfirmAsync(
                title: Localizer.Instance["Library.ScanQr.UnsupportedTitle"],
                message: Localizer.Instance["Library.ScanQr.UnsupportedMessage"],
                confirmLabel: Localizer.Instance["Common.Cancel"],
                cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
            return;
        }

        var editor = _editorFactory.CreateForNew();
        if (!string.IsNullOrEmpty(payload.Name)) editor.Name = payload.Name!;
        else if (!string.IsNullOrEmpty(payload.Host)) editor.Name = payload.Host!;
        if (!string.IsNullOrEmpty(payload.Host)) editor.Host = payload.Host!;
        if (!string.IsNullOrEmpty(payload.RtspMain)) editor.RtspMainText = payload.RtspMain!;
        if (payload.OnvifPort is { } op) editor.OnvifPortText = op.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (payload.HttpPort is { } hp) editor.HttpPort = hp;
        if (!string.IsNullOrEmpty(payload.Username)) editor.Username = payload.Username!;
        if (!string.IsNullOrEmpty(payload.Password)) editor.Password = payload.Password!;

        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.NewRequest is not { } req) return;

        try
        {
            await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add QR-scanned camera {Host}", req.Host);
        }
    }

    [RelayCommand]
    private async Task OpenWebInterfaceAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var url = row.Camera.WebInterfaceUrl;
        if (!await _dialogs.OpenUrlAsync(url).ConfigureAwait(true))
            _logger.LogWarning("Failed to open web interface for {CameraId} at {Url}", row.Camera.Id, url);
    }

    [RelayCommand]
    private async Task OpenSshTerminalAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;
        var vm = _terminalFactory.Create(row.Camera);
        await _dialogs.OpenSshTerminalAsync(vm).ConfigureAwait(true);
    }

    // The file manager browses/edits the camera's live root filesystem over
    // SSH — deleting or overwriting the wrong file can brick the device. The
    // button only shows once the shared "risky device tools" toggle (Settings →
    // Advanced) is on; that opt-in is the consent, so no per-open warning here.
    public bool IsFileManagerEnabled => _userSettings.Current.RawConfigEditorEnabled;

    [RelayCommand]
    private async Task OpenFileManagerAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;
        var vm = _fileManagerFactory.Create(row.Camera);
        await _dialogs.OpenFileManagerAsync(vm).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenFirmwareAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;
        var vm = _firmwareFactory.Create(row.Camera);
        await _dialogs.ShowFirmwareDialogAsync(vm).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EditCameraAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var creds = await _directory.GetCredentialsAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
        var sshCreds = await _directory.GetSshCredentialsAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
        var editor = _editorFactory.CreateForEdit(row.Camera, creds, sshCreds);
        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.UpdateRequest is not { } req)
            return;

        try
        {
            await _directory.UpdateAsync(row.Camera.Id, req, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update camera {Id}", row.Camera.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteCameraAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Library.Dialog.DeleteTitle"],
            message: string.Format(Localizer.Instance["Library.Dialog.DeleteMessage"], row.Camera.Name),
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);

        if (!confirmed)
            return;

        try
        {
            await _directory.RemoveAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete camera {Id}", row.Camera.Id);
        }
    }
}

public sealed partial class CameraRowViewModel : ViewModelBase
{
    // Probe timeout per camera. Kept short so a screen of offline cameras
    // settles quickly — probes run in parallel, so this is the worst-case
    // wait for the whole list, not a per-camera sum.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly CameraDirectoryService? _directory;
    private readonly IReachabilityProbe? _reachability;
    private readonly CameraStatusRegistry? _statusRegistry;
    private readonly ILogger? _logger;

    public Camera Camera { get; }
    public string Name => Camera.Name;
    public string HostAndPort => Camera.HttpPort == 80
        ? Camera.Host
        : $"{Camera.Host}:{Camera.HttpPort}";

    [ObservableProperty] private bool _isIncludedInGrid;

    // Displayed status, pushed from the shared CameraStatusRegistry via the page's
    // Changed handler. The registry merges this row's own probe with any live grid
    // session, so a wedged grid stream surfaces here as Attention too. Starts
    // Unknown → reads "Checking" until the first verdict lands.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private CameraStatus _status = CameraStatus.Unknown;

    public void ApplyStatus(CameraStatus status) => Status = status;

    public string StatusText => Localizer.Instance[Status switch
    {
        CameraStatus.Online => "Library.Online",
        CameraStatus.Attention => "Library.Attention",
        CameraStatus.Offline => "Library.Offline",
        _ => "Library.Checking", // Connecting / Unknown
    }];

    public CameraRowViewModel(Camera camera) : this(camera, null, null, null, null) { }

    public CameraRowViewModel(Camera camera, CameraDirectoryService? directory, ILogger? logger)
        : this(camera, directory, null, null, logger) { }

    public CameraRowViewModel(Camera camera, CameraDirectoryService? directory, IReachabilityProbe? reachability, CameraStatusRegistry? statusRegistry, ILogger? logger, bool? includedInGrid = null)
    {
        Camera = camera;
        _directory = directory;
        _reachability = reachability;
        _statusRegistry = statusRegistry;
        _logger = logger;
        // Field (not property) so seeding the checkbox doesn't trigger a persist.
        // Defaults to the active-layout membership (Phase 19.1), else the flag.
        _isIncludedInGrid = includedInGrid ?? camera.IncludedInGrid;
    }

    /// <summary>
    /// TCP-probes the camera's RTSP port and reports it into the registry; the
    /// merged verdict comes back via the page's Changed handler. With no registry
    /// (design-time) it sets <see cref="Status"/> directly.
    /// </summary>
    public async Task RefreshReachabilityAsync(CancellationToken ct)
    {
        if (_reachability is null)
            return;

        _statusRegistry?.ReportReachability(Camera.Id, null, probeInFlight: true);
        if (_statusRegistry is null) Status = CameraStatus.Connecting;

        var reachable = await _reachability
            .ProbeAsync(Camera, ProbeTimeout, ct, _logger)
            .ConfigureAwait(true);

        if (_statusRegistry is null)
            Status = reachable ? CameraStatus.Online : CameraStatus.Offline;
        else
            _statusRegistry.ReportReachability(Camera.Id, reachable);
    }

    partial void OnIsIncludedInGridChanged(bool value)
    {
        if (_directory is null) return;
        _ = PersistGridFlagAsync(value);
    }

    private async Task PersistGridFlagAsync(bool value)
    {
        try
        {
            await _directory!.SetIncludedInGridAsync(Camera.Id, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist IncludedInGrid for {CameraId}", Camera.Id);
        }
    }
}
