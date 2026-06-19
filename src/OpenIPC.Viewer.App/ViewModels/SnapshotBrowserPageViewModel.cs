using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.App.ViewModels;

public enum SnapshotDatePreset { All, Today, Last7Days, Last30Days }

public enum SnapshotSortOrder { Newest, Oldest }

public sealed partial class SnapshotBrowserPageViewModel : ViewModelBase
{
    // Generous cap — the gallery virtualizes, but we still bound the in-memory
    // list so a pathological library can't balloon. CountsByDay is computed off
    // the loaded set, so this also bounds the timeline.
    private const int LoadLimit = 5000;

    private readonly ISnapshotRepository _repo;
    private readonly CameraDirectoryService _cameras;
    private readonly IDialogService _dialogs;
    private readonly ImageViewerFactory _viewerFactory;
    private readonly IShareService _share;
    private readonly ILogger<SnapshotBrowserPageViewModel> _logger;

    // Guards the auto-reload that SelectedCamera's change handler triggers, so
    // populating the dropdown during a load doesn't recurse.
    private bool _initializing;

    // A specific day picked from the timeline; overrides the date preset until
    // cleared (or a preset / camera change resets it).
    private DateTime? _dayFilter;

    public ObservableCollection<SnapshotItemViewModel> Items { get; } = new();
    public ObservableCollection<CameraFilterOption> CameraOptions { get; } = new();
    public ObservableCollection<SnapshotDayCount> DayCounts { get; } = new();

    [ObservableProperty] private CameraFilterOption? _selectedCamera;
    [ObservableProperty] private SnapshotDatePreset _datePreset = SnapshotDatePreset.All;
    [ObservableProperty] private SnapshotSortOrder _sort = SnapshotSortOrder.Newest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _loadError;

    public bool IsEmpty => IsLoaded && !IsLoading && LoadError is null && Items.Count == 0;
    public bool HasLoadError => LoadError is not null;

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionLabel =>
        string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Snapshots.SelectedFormat"], SelectedCount);

    public bool IsPresetAll => DatePreset == SnapshotDatePreset.All && _dayFilter is null;
    public bool IsPresetToday => DatePreset == SnapshotDatePreset.Today && _dayFilter is null;
    public bool IsPreset7 => DatePreset == SnapshotDatePreset.Last7Days && _dayFilter is null;
    public bool IsPreset30 => DatePreset == SnapshotDatePreset.Last30Days && _dayFilter is null;
    public bool IsSortNewest => Sort == SnapshotSortOrder.Newest;
    public string SortLabel =>
        Localizer.Instance[IsSortNewest ? "Snapshots.SortNewest" : "Snapshots.SortOldest"];

    public SnapshotBrowserPageViewModel(
        ISnapshotRepository repo,
        CameraDirectoryService cameras,
        IDialogService dialogs,
        ImageViewerFactory viewerFactory,
        IShareService share,
        ILogger<SnapshotBrowserPageViewModel> logger)
    {
        _repo = repo;
        _cameras = cameras;
        _dialogs = dialogs;
        _viewerFactory = viewerFactory;
        _share = share;
        _logger = logger;
    }

    public string ShareLabel =>
        Localizer.Instance[_share.SupportsSystemShare ? "Snapshots.Share" : "Snapshots.Reveal"];

    [RelayCommand]
    private async Task ShareSelectedAsync()
    {
        // Native share sheets take one item; share the first selected snapshot
        // (on desktop this reveals it in the file manager).
        var first = Items.FirstOrDefault(i => i.IsSelected);
        if (first is null) return;
        try { await _share.ShareFileAsync(first.FilePath, "image/jpeg", CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Share from browser failed"); }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
            var nameById = new Dictionary<CameraId, string>();
            foreach (var c in cams) nameById[c.Id] = c.Name;

            if (CameraOptions.Count == 0)
            {
                _initializing = true;
                CameraOptions.Add(new CameraFilterOption(null, Localizer.Instance["Snapshots.AllCameras"]));
                foreach (var c in cams.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                    CameraOptions.Add(new CameraFilterOption(c.Id, c.Name));
                SelectedCamera = CameraOptions[0];
                _initializing = false;
            }

            var camId = SelectedCamera?.CameraId;
            var (since, until) = ResolveRange();
            var snaps = await _repo.ListAsync(camId, since, until, LoadLimit, ct).ConfigureAwait(true);
            if (Sort == SnapshotSortOrder.Oldest)
                snaps = snaps.Reverse().ToList();

            Items.Clear();
            foreach (var s in snaps)
            {
                var name = nameById.TryGetValue(s.CameraId, out var n) ? n : Localizer.Instance["Common.Unknown"];
                Items.Add(new SnapshotItemViewModel(s, name, OnItemSelectionChanged));
            }

            RebuildDayCounts(snaps);
            IsLoaded = true;
            OnSelectionChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load snapshots");
            LoadError = Localizer.Instance["Snapshots.LoadError"];
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (DateTime? Since, DateTime? Until) ResolveRange()
    {
        if (_dayFilter is { } day)
            return (day.ToUniversalTime(), day.AddDays(1).ToUniversalTime());

        var today = DateTime.Now.Date;
        return DatePreset switch
        {
            SnapshotDatePreset.Today => (today.ToUniversalTime(), (DateTime?)null),
            SnapshotDatePreset.Last7Days => (today.AddDays(-6).ToUniversalTime(), (DateTime?)null),
            SnapshotDatePreset.Last30Days => (today.AddDays(-29).ToUniversalTime(), (DateTime?)null),
            _ => ((DateTime?)null, (DateTime?)null),
        };
    }

    private void RebuildDayCounts(IReadOnlyList<Snapshot> snaps)
    {
        DayCounts.Clear();
        var groups = snaps
            .GroupBy(s => s.TakenAt.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new SnapshotDayCount(g.Key, g.Count(), g.Key == _dayFilter));
        foreach (var g in groups)
            DayCounts.Add(g);
    }

    private void OnItemSelectionChanged() => OnSelectionChanged();

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLabel));
    }

    private void RaiseFilterFlags()
    {
        OnPropertyChanged(nameof(IsPresetAll));
        OnPropertyChanged(nameof(IsPresetToday));
        OnPropertyChanged(nameof(IsPreset7));
        OnPropertyChanged(nameof(IsPreset30));
        OnPropertyChanged(nameof(IsSortNewest));
        OnPropertyChanged(nameof(SortLabel));
    }

    partial void OnSelectedCameraChanged(CameraFilterOption? value)
    {
        if (_initializing) return;
        _ = LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private Task SetPresetAsync(string preset)
    {
        DatePreset = preset switch
        {
            "today" => SnapshotDatePreset.Today,
            "7" => SnapshotDatePreset.Last7Days,
            "30" => SnapshotDatePreset.Last30Days,
            _ => SnapshotDatePreset.All,
        };
        _dayFilter = null;
        RaiseFilterFlags();
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private Task ToggleSortAsync()
    {
        Sort = Sort == SnapshotSortOrder.Newest ? SnapshotSortOrder.Oldest : SnapshotSortOrder.Newest;
        RaiseFilterFlags();
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private Task FilterByDayAsync(SnapshotDayCount? day)
    {
        if (day is null) return Task.CompletedTask;
        _dayFilter = _dayFilter == day.Day ? null : day.Day;
        RaiseFilterFlags();
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task OpenAsync(SnapshotItemViewModel? item)
    {
        if (item is null) return;
        var start = Items.IndexOf(item);
        if (start < 0) return;

        var entries = Items
            .Select(i => new SnapshotViewEntry(i.Snapshot, i.CameraName))
            .ToList();
        var vm = _viewerFactory.Create(entries, start);
        await _dialogs.ShowImageViewerAsync(vm).ConfigureAwait(true);

        // Deletions inside the viewer are reflected by reloading the gallery.
        if (vm.AnyChanges)
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(SnapshotItemViewModel? item)
    {
        if (item is null) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Snapshots.Dialog.DeleteTitle"],
            message: Localizer.Instance["Snapshots.Dialog.DeleteOneMessage"],
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;
        await DeleteItemsAsync(new[] { item }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Snapshots.Dialog.DeleteTitle"],
            message: string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["Snapshots.Dialog.DeleteManyMessageFormat"], selected.Count),
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;
        await DeleteItemsAsync(selected).ConfigureAwait(true);
    }

    private async Task DeleteItemsAsync(IReadOnlyList<SnapshotItemViewModel> items)
    {
        foreach (var item in items)
        {
            try
            {
                TryDeleteFile(item.Snapshot.Path);
                if (!string.IsNullOrEmpty(item.Snapshot.ThumbPath))
                    TryDeleteFile(item.Snapshot.ThumbPath!);
                await _repo.RemoveAsync(item.Snapshot.Id, CancellationToken.None).ConfigureAwait(true);
                Items.Remove(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete snapshot {Id}", item.Snapshot.Id);
            }
        }
        RebuildDayCounts(Items.Select(i => i.Snapshot).ToList());
        OnSelectionChanged();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { _logger.LogWarning(ex, "Snapshot file locked: {Path}", path); }
    }
}

public sealed record CameraFilterOption(CameraId? CameraId, string Name)
{
    public override string ToString() => Name;
}

public sealed record SnapshotDayCount(DateTime Day, int Count, bool IsActive)
{
    public string DayLabel => Day.ToString("MMM d", CultureInfo.CurrentCulture);
}

public sealed partial class SnapshotItemViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public Snapshot Snapshot { get; }
    public string CameraName { get; }

    // Prefer the cached thumbnail; fall back to the full image if it's missing.
    public string ThumbSource => Snapshot.ThumbPath ?? Snapshot.Path;
    public string FilePath => Snapshot.Path;
    public DateTime TakenAtLocal => Snapshot.TakenAt.ToLocalTime();
    public string TimeLabel => TakenAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string ResolutionLabel => Snapshot.Width > 0 ? $"{Snapshot.Width}×{Snapshot.Height}" : string.Empty;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    public SnapshotItemViewModel(Snapshot snapshot, string cameraName, Action onSelectionChanged)
    {
        Snapshot = snapshot;
        CameraName = cameraName;
        _onSelectionChanged = onSelectionChanged;
    }
}
