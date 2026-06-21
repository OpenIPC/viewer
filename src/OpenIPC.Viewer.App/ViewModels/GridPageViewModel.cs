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
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class GridPageViewModel : ViewModelBase,
    IRecipient<WindowMinimizedMessage>,
    IRecipient<WindowRestoredMessage>,
    IRecipient<CloseTileMessage>,
    IRecipient<ConfigImportedMessage>,
    IAsyncDisposable
{
    private readonly CameraDirectoryService _directory;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly UserSettingsService _userSettings;
    private readonly ISnapshotService _snapshots;
    private readonly OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine _analytics;
    private readonly AnalyticsBootstrap _analyticsBootstrap;
    private readonly AudioMonitor _audio;
    private readonly OpenIPC.Viewer.Core.Persistence.ILayoutRepository _layouts;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GridPageViewModel> _logger;

    private IReadOnlyList<Camera> _allCameras = Array.Empty<Camera>();
    private bool _minimized;
    private bool _suppressSettingsRefresh;
    private CancellationTokenSource? _graceCts;

    public string Title => Localizer.Instance["Nav.Live"];

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = new();
    public ObservableCollection<CameraTileViewModel?> Slots { get; } = new();

    // Tabbed layouts (Phase 19.1). Tabs bind to Layouts; the grid shows
    // ActiveLayout's tiles. Switching persists the choice to UserSettings.
    public ObservableCollection<GridLayout> Layouts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteLayout))]
    private GridLayout? _activeLayout;

    // Keep at least one layout — deleting the last would leave no grid.
    public bool CanDeleteLayout => Layouts.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns))]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private int _layoutSize = 2;

    public int Columns => LayoutSize;
    public int Rows => LayoutSize;

    public GridPageViewModel(
        CameraDirectoryService directory,
        LiveStreamCoordinator coordinator,
        UserSettingsService userSettings,
        ISnapshotService snapshots,
        OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine analytics,
        AnalyticsBootstrap analyticsBootstrap,
        AudioMonitor audio,
        OpenIPC.Viewer.Core.Persistence.ILayoutRepository layouts,
        IDialogService dialogs,
        ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _coordinator = coordinator;
        _userSettings = userSettings;
        _snapshots = snapshots;
        _analytics = analytics;
        _analyticsBootstrap = analyticsBootstrap;
        _audio = audio;
        _layouts = layouts;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GridPageViewModel>();

        WeakReferenceMessenger.Default.Register<WindowMinimizedMessage>(this);
        WeakReferenceMessenger.Default.Register<WindowRestoredMessage>(this);
        WeakReferenceMessenger.Default.Register<CloseTileMessage>(this);
        WeakReferenceMessenger.Default.Register<ConfigImportedMessage>(this);

        // Re-render when the user changes the max-sessions cap so currently-
        // dropped cameras come back (or excess ones go away) without a relaunch.
        _userSettings.Changed += async (_, _) =>
        {
            // Our own active-layout persistence raises Changed too — skip the
            // refresh then (the switch already refreshed).
            if (_suppressSettingsRefresh) return;
            try { await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RefreshTilesAsync(CancellationToken.None)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Grid refresh after settings change failed"); }
        };
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_minimized) return;
        _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        await LoadLayoutsAsync(ct).ConfigureAwait(true);
        await RefreshTilesAsync(ct).ConfigureAwait(true);
    }

    private async Task LoadLayoutsAsync(CancellationToken ct)
    {
        var all = await _layouts.GetAllAsync(ct).ConfigureAwait(true);
        Layouts.Clear();
        foreach (var l in all) Layouts.Add(l);
        OnPropertyChanged(nameof(CanDeleteLayout));

        // Restore the persisted active layout, else fall back to the first.
        var activeId = _userSettings.Current.ActiveLayoutId;
        ActiveLayout = Layouts.FirstOrDefault(l => l.Id.Value == activeId) ?? Layouts.FirstOrDefault();
        if (ActiveLayout is { } a) LayoutSize = a.GridSize;
    }

    // Parameter is string because XAML CommandParameter literals are strings; using
    // int here would make AsyncRelayCommand<int>.Execute throw at first render.
    [RelayCommand]
    private async Task SetLayoutAsync(string size)
    {
        if (!int.TryParse(size, out var n) || n < 1 || n > 3) return;
        LayoutSize = n;
        if (ActiveLayout is { } a)
        {
            await _layouts.SetGridSizeAsync(a.Id, n, CancellationToken.None).ConfigureAwait(true);
            ActiveLayout = a with { GridSize = n };
            ReplaceLayoutInList(ActiveLayout);
        }
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    // --- Tab operations (Phase 19.1) --------------------------------------
    [RelayCommand]
    private async Task SwitchLayoutAsync(GridLayout? layout)
    {
        if (layout is null || (ActiveLayout is { } cur && cur.Id == layout.Id)) return;
        ActiveLayout = layout;
        LayoutSize = layout.GridSize;
        await PersistActiveLayoutAsync(layout.Id.Value).ConfigureAwait(true);
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddLayoutAsync()
    {
        var name = await _dialogs.PromptAsync(
            Localizer.Instance["Layouts.NewTitle"], "", Localizer.Instance["Common.Create"], Localizer.Instance["Common.Cancel"])
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = await _layouts.AddAsync(name.Trim(), 2, Layouts.Count, CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true);
        var created = Layouts.FirstOrDefault(l => l.Id.Value == id.Value);
        if (created is not null) await SwitchLayoutAsync(created).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameLayoutAsync()
    {
        if (ActiveLayout is not { } a) return;
        var name = await _dialogs.PromptAsync(
            Localizer.Instance["Layouts.RenameTitle"], a.Name, Localizer.Instance["Common.Rename"], Localizer.Instance["Common.Cancel"])
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == a.Name) return;
        await _layouts.RenameAsync(a.Id, name.Trim(), CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteLayoutAsync()
    {
        if (ActiveLayout is not { } a || Layouts.Count <= 1) return;
        var ok = await _dialogs.ConfirmAsync(
            Localizer.Instance["Layouts.DeleteTitle"],
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Layouts.DeleteMessageFormat"], a.Name),
            Localizer.Instance["Common.Delete"], Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!ok) return;
        await _layouts.RemoveAsync(a.Id, CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true); // active id gone → falls back to first
        if (ActiveLayout is { } now) await PersistActiveLayoutAsync(now.Id.Value).ConfigureAwait(true);
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    // Drag-reorder of the tabs themselves (Phase 19.1 polish). Both indices are
    // in the Layouts collection; persists SortOrder = new index.
    public async Task MoveLayoutAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || fromIndex >= Layouts.Count) return;
        if (toIndex < 0 || toIndex >= Layouts.Count) return;
        if (fromIndex == toIndex) return;

        Layouts.Move(fromIndex, toIndex);
        try
        {
            await _layouts.ReorderAsync(Layouts.Select(l => l.Id).ToList(), ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting layout order failed");
        }
    }

    private async Task PersistActiveLayoutAsync(int id)
    {
        _suppressSettingsRefresh = true;
        try { await _userSettings.UpdateAsync(_userSettings.Current with { ActiveLayoutId = id }).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persisting active layout failed"); }
        finally { _suppressSettingsRefresh = false; }
    }

    private void ReplaceLayoutInList(GridLayout updated)
    {
        for (var i = 0; i < Layouts.Count; i++)
            if (Layouts[i].Id == updated.Id) { Layouts[i] = updated; break; }
    }

    private async Task RefreshTilesAsync(CancellationToken ct)
    {
        // Two caps stack: the layout selector (1/2x2/3x3 = up to 9 slots) and
        // the Settings → Video → MaxConcurrentGridSessions ceiling. The lower
        // of the two wins, so a "max 4" user with a 3x3 grid sees 4 live tiles
        // and 5 empty placeholders (which still render via the Slots padding
        // below).
        var capacity = Math.Min(LayoutSize * LayoutSize, Math.Max(1, _userSettings.MaxConcurrentGridSessions));

        // Tiles come from the active layout's ordered membership (Phase 19.1),
        // mapped onto the loaded camera records. Falls back to the legacy
        // IncludedInGrid flag if there's no layout (shouldn't happen post-migration).
        List<Camera> visible;
        if (ActiveLayout is { } layout)
        {
            var tileIds = await _layouts.GetTilesAsync(layout.Id, ct).ConfigureAwait(true);
            var byId = _allCameras.ToDictionary(c => c.Id);
            visible = tileIds.Where(byId.ContainsKey).Select(id => byId[id]).Take(capacity).ToList();
        }
        else
        {
            visible = _allCameras.Where(c => c.IncludedInGrid).Take(capacity).ToList();
        }
        var visibleIds = visible.Select(c => c.Id).ToHashSet();

        // Drop tiles that aren't in the new visible set.
        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(Tiles[i].Camera.Id))
            {
                var stale = Tiles[i];
                Tiles.RemoveAt(i);
                try { await stale.DisposeAsync().ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile"); }
            }
        }

        // Reconcile against the freshly-loaded records: keep unchanged tiles,
        // rebuild ones whose stream URL was edited, add new ones. Without the
        // rebuild branch an edited RTSP URL never reached the grid — the tile
        // was matched by Id and kept, so it kept streaming the old URL until an
        // app restart. RefreshTilesAsync re-runs on every Live-tab entry (the
        // only time an edit can have landed), so the swap happens on next view.
        foreach (var camera in visible)
        {
            var quality = DesiredQuality(camera);
            var existing = Tiles.FirstOrDefault(t => t.Camera.Id == camera.Id);
            if (existing is not null)
            {
                // Rebuild on a stream-URL change OR an analytics-settings change:
                // the tile holds an immutable Camera snapshot, so toggling
                // analytics in the editor only takes effect by rebuilding it.
                if (!StreamUriChanged(existing.Camera, camera) && !AnalyticsChanged(existing.Camera, camera))
                {
                    // Kept tile — re-evaluate SD/HD against the (possibly new)
                    // layout. No-op when quality is unchanged.
                    try { await existing.SetQualityAsync(quality, ct).ConfigureAwait(true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to switch quality for {Camera}", camera.Name); }
                    continue;
                }

                var idx = Tiles.IndexOf(existing);
                Tiles.RemoveAt(idx);
                try { await existing.DisposeAsync().ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error releasing stale tile for {Camera}", camera.Name); }

                var rebuilt = new CameraTileViewModel(camera, _coordinator, _directory, _userSettings, _snapshots, _analytics, _analyticsBootstrap, _audio, _loggerFactory.CreateLogger<CameraTileViewModel>());
                rebuilt.SetInitialQuality(quality);
                Tiles.Insert(idx, rebuilt);
                try { await rebuilt.ActivateAsync(ct).ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to activate rebuilt tile for {Camera}", camera.Name); }
                continue;
            }

            var tile = new CameraTileViewModel(camera, _coordinator, _directory, _userSettings, _snapshots, _analytics, _analyticsBootstrap, _audio, _loggerFactory.CreateLogger<CameraTileViewModel>());
            tile.SetInitialQuality(quality);
            Tiles.Add(tile);
            try { await tile.ActivateAsync(ct).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to activate tile for {Camera}", camera.Name); }
        }

        // Slots fills the *visual* grid (always LayoutSize²), padding with
        // nulls when MaxConcurrentGridSessions is below the layout capacity.
        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);
    }

    // A tile is rebuilt only when the URL it actually streams changes — the
    // grid uses the substream, falling back to main (mirrors
    // CameraTileViewModel.ActivateAsync). Comparing this (rather than the whole
    // record) avoids churning sessions on cosmetic edits like a rename.
    private static bool StreamUriChanged(Camera a, Camera b) =>
        (a.RtspSubUri ?? a.RtspMainUri) != (b.RtspSubUri ?? b.RtspMainUri);

    // True when a camera's analytics config changed (Phase 15) — the tile's
    // frame tap reads its immutable Camera snapshot, so any change needs a
    // rebuild. Compared field-by-field because AnalyticsSettings.ClassIds is a
    // collection (record equality would compare it by reference).
    private static bool AnalyticsChanged(Camera a, Camera b)
    {
        var x = a.AnalyticsOrDefault;
        var y = b.AnalyticsOrDefault;
        if (x.Enabled != y.Enabled || x.AutoRecord != y.AutoRecord || x.AnalyticsFps != y.AnalyticsFps
            || x.PostEventSeconds != y.PostEventSeconds
            || Math.Abs(x.ConfidenceThreshold - y.ConfidenceThreshold) > 0.001f)
            return true;
        var xc = (x.ClassIds ?? Array.Empty<int>()).OrderBy(i => i);
        var yc = (y.ClassIds ?? Array.Empty<int>()).OrderBy(i => i);
        return !xc.SequenceEqual(yc);
    }

    // Auto SD/HD policy (Phase 12.2): mainstream only when a single tile fills
    // the view (1×1 layout) and the user hasn't disabled the feature; otherwise
    // the substream keeps the multi-camera grid light.
    private StreamQuality DesiredQuality(Camera camera) =>
        StreamQualityPolicy.Resolve(camera.StreamQualityOverride, _userSettings.Current.AutoSdHd, LayoutSize);

    // Drag-reorder hook called from GridPage code-behind. Both indices are in
    // the *Tiles* collection (live cameras only — empty Slots placeholders are
    // not draggable and can't be drop targets). Persists SortOrder = newIndex
    // for the affected tiles; cameras outside the grid keep their existing
    // SortOrder (so library ordering only shifts grid-included rows).
    public async Task MoveTileAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || fromIndex >= Tiles.Count) return;
        if (toIndex < 0 || toIndex >= Tiles.Count) return;
        if (fromIndex == toIndex) return;

        Tiles.Move(fromIndex, toIndex);

        // Re-pad Slots so visual order matches the new Tiles order.
        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);

        if (ActiveLayout is not { } a) return;

        try
        {
            // Tiles is only the visible (capacity-capped) prefix of the layout's
            // membership; reorder within the full member list so cameras beyond
            // the cap keep their place.
            var full = (await _layouts.GetTilesAsync(a.Id, ct).ConfigureAwait(true)).ToList();
            if (fromIndex < full.Count && toIndex < full.Count)
            {
                var moved = full[fromIndex];
                full.RemoveAt(fromIndex);
                full.Insert(toIndex, moved);
                await _layouts.SetTilesAsync(a.Id, full, ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting layout tile order failed");
        }
    }

    // Close button on a tile's error cell — drop it from the grid for this
    // session and leave an empty slot in its place. The camera stays
    // IncludedInGrid, so re-entering Live re-adds it via RefreshTilesAsync.
    public async void Receive(CloseTileMessage message)
    {
        var tile = Tiles.FirstOrDefault(t => t.Camera.Id == message.CameraId);
        if (tile is null) return;
        Tiles.Remove(tile);
        RebuildSlots();
        try { await tile.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error releasing closed tile"); }
    }

    // Re-pad Slots to the visual grid size (LayoutSize²), filling trailing
    // gaps with null placeholders.
    private void RebuildSlots()
    {
        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);
    }

    // Smart Pause (Phase 12.1): on minimize, pause decode immediately (CPU
    // drops, last frame stays frozen for an instant resume) and start a grace
    // timer. Only if the window is still hidden after the grace period do we
    // fully release the sessions to free RTSP connections.
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(10);

    public void Receive(WindowMinimizedMessage message)
    {
        _minimized = true;
        foreach (var tile in Tiles)
            tile.Pause();

        _graceCts?.Cancel();
        _graceCts = new CancellationTokenSource();
        var token = _graceCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(PauseGrace, token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_minimized) await ReleaseAllAsync().ConfigureAwait(true);
            });
        });
    }

    // Config import (Phase 19.2): reload cameras + layouts so the grid reflects
    // the imported set without a restart.
    public async void Receive(ConfigImportedMessage message)
    {
        try { await LoadAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Grid reload after import failed"); }
    }

    public async void Receive(WindowRestoredMessage message)
    {
        _minimized = false;
        _graceCts?.Cancel();
        if (Tiles.Count > 0)
        {
            // Still paused (within grace) — resume in place, frozen frame intact.
            foreach (var tile in Tiles)
                tile.Resume();
        }
        else
        {
            // Grace elapsed and sessions were released — rebuild the grid.
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task ReleaseAllAsync()
    {
        var copy = Tiles.ToArray();
        Tiles.Clear();
        Slots.Clear();
        foreach (var tile in copy)
        {
            try { await tile.DisposeAsync().ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile during minimize"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _graceCts?.Cancel();
        await ReleaseAllAsync().ConfigureAwait(false);
    }
}
