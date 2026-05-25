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

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraLibraryPageViewModel : ViewModelBase
{
    private readonly CameraDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly CameraEditorFactory _editorFactory;
    private readonly DiscoveryDialogFactory _discoveryFactory;
    private readonly ILogger<CameraLibraryPageViewModel> _logger;

    public string Title => "Cameras";
    public ObservableCollection<CameraRowViewModel> Cameras { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameras))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    public bool HasCameras => IsLoaded && Cameras.Count > 0;
    public bool IsEmpty => IsLoaded && Cameras.Count == 0;

    private readonly UserSettingsService _userSettings;
    private readonly IDiscoveryService _discovery;
    private readonly ManageGroupsDialogFactory _manageGroupsFactory;
    private bool _autoScanRanThisSession;
    private System.Collections.Generic.IReadOnlyList<Camera> _allCameras = System.Array.Empty<Camera>();

    public ObservableCollection<CameraGroup?> AvailableGroups { get; } = new();

    [ObservableProperty] private CameraGroup? _selectedGroupFilter;

    partial void OnSelectedGroupFilterChanged(CameraGroup? value) => RefilterCameras();

    public CameraLibraryPageViewModel(
        CameraDirectoryService directory,
        IDialogService dialogs,
        CameraEditorFactory editorFactory,
        DiscoveryDialogFactory discoveryFactory,
        ManageGroupsDialogFactory manageGroupsFactory,
        UserSettingsService userSettings,
        IDiscoveryService discovery,
        ILogger<CameraLibraryPageViewModel> logger)
    {
        _directory = directory;
        _dialogs = dialogs;
        _editorFactory = editorFactory;
        _discoveryFactory = discoveryFactory;
        _manageGroupsFactory = manageGroupsFactory;
        _userSettings = userSettings;
        _discovery = discovery;
        _logger = logger;
        Cameras.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCameras));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        await ReloadGroupsAsync(ct).ConfigureAwait(true);
        RefilterCameras();
        IsLoaded = true;

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
            Cameras.Add(new CameraRowViewModel(camera, _directory, _logger));
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
        var discoveryVm = _discoveryFactory.Create();
        var found = await _dialogs.ShowDiscoveryDialogAsync(discoveryVm).ConfigureAwait(true);
        if (found is null)
            return;

        // Pre-fill the editor from the probe result so the user sees / can tweak
        // everything before saving (RTSP URI especially — phase-04 risks §"ONVIF
        // returns wrong RTSP URI behind NAT" applies).
        var editor = _editorFactory.CreateForNew();
        editor.Name = found.Discovered.Model ?? found.Discovered.Name ?? found.Discovered.Host;
        editor.Host = found.Discovered.Host;
        editor.OnvifPortText = found.Discovered.OnvifPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        editor.RtspMainText = found.Probe.RtspMainUri.ToString();
        editor.Username = found.Credentials?.Username ?? "";
        editor.Password = found.Credentials?.Password ?? "";

        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.NewRequest is not { } req)
            return;

        try
        {
            var id = await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
            // Persist HasPtz / ProfileToken / manufacturer info from the probe so
            // SingleCameraPage knows whether to show the PTZ joystick (Phase 4c).
            await _directory.SaveOnvifMetadataAsync(id, found.Probe, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add discovered camera {Host}", req.Host);
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
    private async Task EditCameraAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var creds = await _directory.GetCredentialsAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
        var editor = _editorFactory.CreateForEdit(row.Camera, creds);
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
    private readonly CameraDirectoryService? _directory;
    private readonly ILogger? _logger;

    public Camera Camera { get; }
    public string Name => Camera.Name;
    public string HostAndPort => Camera.HttpPort == 80
        ? Camera.Host
        : $"{Camera.Host}:{Camera.HttpPort}";

    [ObservableProperty] private bool _isIncludedInGrid;

    public CameraRowViewModel(Camera camera) : this(camera, null, null) { }

    public CameraRowViewModel(Camera camera, CameraDirectoryService? directory, ILogger? logger)
    {
        Camera = camera;
        _directory = directory;
        _logger = logger;
        _isIncludedInGrid = camera.IncludedInGrid;
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
