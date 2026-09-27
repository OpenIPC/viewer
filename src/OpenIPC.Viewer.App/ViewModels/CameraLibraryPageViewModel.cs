using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
using OpenIPC.Viewer.Core.Persistence;
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
    private readonly ILayoutRepository _layouts;
    private IReadOnlyList<Camera> _allCameras = Array.Empty<Camera>();
    private IReadOnlyList<GridLayout> _allLayouts = Array.Empty<GridLayout>();
    private Dictionary<GroupId, string> _groupNames = new();

    public ObservableCollection<CameraGroup?> AvailableGroups { get; } = new();

    // Every layout, for the row menu's "Layouts ▸" submenu.
    public IReadOnlyList<GridLayout> AllLayouts => _allLayouts;

    // --- Filters ------------------------------------------------------------
    // Rows are built once per load; group / search / status only re-slice them
    // into PageRows, so typing in the search box doesn't re-probe every camera.
    [ObservableProperty] private CameraGroup? _selectedGroupFilter;
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterOnline))]
    [NotifyPropertyChangedFor(nameof(IsFilterOffline))]
    [NotifyPropertyChangedFor(nameof(IsFilterAttention))]
    private LibraryStatusFilter _statusFilter;

    // One flag per status chip (RadioButtons bind TwoWay to these).
    public bool IsFilterAll { get => StatusFilter == LibraryStatusFilter.All; set { if (value) StatusFilter = LibraryStatusFilter.All; } }
    public bool IsFilterOnline { get => StatusFilter == LibraryStatusFilter.Online; set { if (value) StatusFilter = LibraryStatusFilter.Online; } }
    public bool IsFilterOffline { get => StatusFilter == LibraryStatusFilter.Offline; set { if (value) StatusFilter = LibraryStatusFilter.Offline; } }
    public bool IsFilterAttention { get => StatusFilter == LibraryStatusFilter.Attention; set { if (value) StatusFilter = LibraryStatusFilter.Attention; } }

    // Chip counters — over the rows that pass the group + search filters.
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention))]
    private int _attentionCount;
    public bool HasAttention => AttentionCount > 0 || StatusFilter == LibraryStatusFilter.Attention;

    // Cameras exist but the filters hide all of them.
    [ObservableProperty] private bool _hasNoMatches;

    // A new filter starts from the first page; a probe landing only re-clamps.
    partial void OnSelectedGroupFilterChanged(CameraGroup? value) => ApplyFilters(resetPage: true);
    partial void OnSearchTextChanged(string value) => ApplyFilters(resetPage: true);
    partial void OnStatusFilterChanged(LibraryStatusFilter value)
    {
        OnPropertyChanged(nameof(HasAttention));
        ApplyFilters(resetPage: true);
    }

    // --- Pagination -----------------------------------------------------------
    // Only the current page's rows are materialised (PageRows). Reachability
    // probes still cover every camera — a TCP connect is cheap and the status
    // chips need the full counts — but anything heavier per row (preview
    // stills) should run for PageRows only.
    public const int PageSize = 20;

    public ObservableCollection<CameraRowViewModel> PageRows { get; } = new();

    // 1-based page numbers for the pager buttons.
    public ObservableCollection<int> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    private int _currentPage; // 0-based

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiplePages))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    private int _pageCount = 1;

    public int CurrentPageDisplay => CurrentPage + 1;
    public bool HasMultiplePages => PageCount > 1;
    public bool CanPrevPage => CurrentPage > 0;
    public bool CanNextPage => CurrentPage + 1 < PageCount;

    [RelayCommand]
    private void PrevPage()
    {
        if (!CanPrevPage) return;
        CurrentPage--;
        ApplyFilters(resetPage: false);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!CanNextPage) return;
        CurrentPage++;
        ApplyFilters(resetPage: false);
    }

    // CommandParameter is the boxed 1-based page number from the Pages binding
    // (object? sidesteps the RelayCommand<int> XAML render crash).
    [RelayCommand]
    private void GoToPage(object? page)
    {
        if (page is null) return;
        int oneBased;
        try { oneBased = Convert.ToInt32(page, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return; }
        var target = oneBased - 1;
        if (target < 0 || target >= PageCount || target == CurrentPage) return;
        CurrentPage = target;
        ApplyFilters(resetPage: false);
    }

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
        ILayoutRepository layouts,
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
        _layouts = layouts;
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
            await ReloadGroupsAsync(ct).ConfigureAwait(true);
            RebuildRows();
            await LoadLayoutMembershipAsync(ct).ConfigureAwait(true);
            ApplyFilters();
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
        _groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
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

    private void RebuildRows()
    {
        foreach (var old in Cameras) old.PropertyChanged -= OnRowPropertyChanged;
        Cameras.Clear();
        foreach (var camera in _allCameras)
        {
            var row = new CameraRowViewModel(camera, _directory, _reachability, _statusRegistry, _logger)
            {
                GroupName = camera.GroupId is { } gid && _groupNames.TryGetValue(gid, out var name) ? name : null,
            };
            // Seed from whatever the registry already knows (e.g. a live grid session).
            row.ApplyStatus(_statusRegistry.Get(camera.Id).Status);
            row.PropertyChanged += OnRowPropertyChanged;
            Cameras.Add(row);
        }

        // Kick off reachability probes for the freshly-built rows. Fire-and-forget:
        // each row updates its own Status independently, in parallel.
        _ = ProbeReachabilityAsync();
    }

    // A probe / registry verdict landed — counters and the status filter move.
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraRowViewModel.Status))
            ApplyFilters();
    }

    private void ApplyFilters(bool resetPage = false)
    {
        var query = SearchText?.Trim() ?? "";
        var group = SelectedGroupFilter;
        int total = 0, online = 0, offline = 0, attention = 0;
        var shown = new List<CameraRowViewModel>();
        foreach (var row in Cameras)
        {
            var matches = (group is null || row.Camera.GroupId.Equals(group.Id)) && row.Matches(query);
            if (matches)
            {
                total++;
                switch (row.Status)
                {
                    case CameraStatus.Online: online++; break;
                    case CameraStatus.Offline: offline++; break;
                    case CameraStatus.Attention: attention++; break;
                }
            }
            var passesStatus = StatusFilter switch
            {
                LibraryStatusFilter.Online => row.Status == CameraStatus.Online,
                LibraryStatusFilter.Offline => row.Status == CameraStatus.Offline,
                LibraryStatusFilter.Attention => row.Status == CameraStatus.Attention,
                _ => true,
            };
            if (matches && passesStatus) shown.Add(row);
        }
        TotalCount = total;
        OnlineCount = online;
        OfflineCount = offline;
        AttentionCount = attention;
        HasNoMatches = Cameras.Count > 0 && shown.Count == 0;

        var pageCount = Math.Max(1, (shown.Count + PageSize - 1) / PageSize);
        if (pageCount != PageCount || Pages.Count != pageCount)
        {
            PageCount = pageCount;
            Pages.Clear();
            for (var i = 1; i <= pageCount; i++) Pages.Add(i);
        }
        CurrentPage = resetPage ? 0 : Math.Min(CurrentPage, pageCount - 1);

        // Swap the visible rows only when the page's set actually changed — a
        // probe verdict under the "All" filter must not re-template the list.
        var page = shown.Skip(CurrentPage * PageSize).Take(PageSize).ToList();
        if (!page.SequenceEqual(PageRows))
        {
            PageRows.Clear();
            foreach (var row in page) PageRows.Add(row);
        }
    }

    // --- Layout membership (replaces the old "in grid" checkbox) -----------
    // A camera can sit in any number of layouts; the row shows them and its
    // menu toggles each one.
    private async Task LoadLayoutMembershipAsync(CancellationToken ct)
    {
        _allLayouts = await _layouts.GetAllAsync(ct).ConfigureAwait(true);
        var byCamera = new Dictionary<CameraId, List<GridLayout>>();
        foreach (var layout in _allLayouts)
        {
            foreach (var id in await _layouts.GetTilesAsync(layout.Id, ct).ConfigureAwait(true))
            {
                if (!byCamera.TryGetValue(id, out var list)) byCamera[id] = list = new List<GridLayout>();
                list.Add(layout);
            }
        }
        foreach (var row in Cameras)
            row.SetLayouts(byCamera.TryGetValue(row.Camera.Id, out var m) ? m : new List<GridLayout>());
    }

    /// <summary>
    /// Re-reads layout membership for the rows on screen — layouts may have
    /// changed on the Live page since the library was loaded.
    /// </summary>
    public async Task RefreshLayoutMembershipAsync()
    {
        try { await LoadLayoutMembershipAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Layout membership refresh failed"); }
    }

    public async Task ToggleLayoutMembershipAsync(CameraRowViewModel row, GridLayout layout)
    {
        try
        {
            if (row.IsInLayout(layout.Id))
                await _layouts.RemoveTileAsync(layout.Id, row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
            else
                await _layouts.AddTileAsync(layout.Id, row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
            await LoadLayoutMembershipAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toggling layout {Layout} for {CameraId} failed", layout.Name, row.Camera.Id);
        }
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
        var knownHosts = new HashSet<string>(
            _allCameras.Select(c => c.Host),
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

    public string? GroupName { get; init; }
    public bool HasGroup => !string.IsNullOrEmpty(GroupName);

    // Layouts this camera is a tile of, as a "Default, Yard" label.
    private HashSet<LayoutId> _layoutIds = new();
    [ObservableProperty] private string _layoutsLabel = "—";

    public bool IsInLayout(LayoutId id) => _layoutIds.Contains(id);

    public void SetLayouts(IReadOnlyCollection<GridLayout> layouts)
    {
        _layoutIds = layouts.Select(l => l.Id).ToHashSet();
        LayoutsLabel = layouts.Count == 0 ? "—" : string.Join(", ", layouts.Select(l => l.Name));
    }

    internal bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || HostAndPort.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (GroupName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

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

    public CameraRowViewModel(Camera camera, CameraDirectoryService? directory, IReachabilityProbe? reachability, CameraStatusRegistry? statusRegistry, ILogger? logger)
    {
        Camera = camera;
        _directory = directory;
        _reachability = reachability;
        _statusRegistry = statusRegistry;
        _logger = logger;
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
}

public enum LibraryStatusFilter
{
    All,
    Online,
    Offline,
    Attention,
}
