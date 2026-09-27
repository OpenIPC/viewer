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
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public enum MediaTab { Recordings, Snapshots }

public enum RecordingPeriod { All, Today, Days7, Days30 }

public sealed partial class RecordingsPageViewModel : ViewModelBase
{
    private readonly IRecordingRepository _repo;
    private readonly CameraDirectoryService _cameras;
    private readonly IDialogService _dialogs;
    private readonly ILogger<RecordingsPageViewModel> _logger;

    // Newest first; the filters below slice this into PageItems.
    private readonly List<RecordingRowViewModel> _allRows = new();
    private DateTime? _dayFilter;
    private bool _syncingDayAndPeriod;

    public string Title => Localizer.Instance["Nav.Recordings"];

    // Phase 16.3: archive activity calendar. Selecting a day filters the list.
    public ArchiveCalendarViewModel Calendar { get; }

    // Phase 14: the Recordings page doubles as the captured-media browser. A
    // segmented header flips between the recordings list and the snapshot
    // gallery; the snapshot tab loads lazily on first view.
    public SnapshotBrowserPageViewModel Snapshots { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordings))]
    [NotifyPropertyChangedFor(nameof(ShowSnapshots))]
    [NotifyPropertyChangedFor(nameof(IsRecordingsTabActive))]
    [NotifyPropertyChangedFor(nameof(IsSnapshotsTabActive))]
    private MediaTab _selectedTab = MediaTab.Recordings;

    public bool ShowRecordings => SelectedTab == MediaTab.Recordings;
    public bool ShowSnapshots => SelectedTab == MediaTab.Snapshots;

    // Settable so the "Recordings | Snapshots" segment binds TwoWay.
    public bool IsRecordingsTabActive
    {
        get => SelectedTab == MediaTab.Recordings;
        set { if (value) SelectRecordings(); }
    }

    public bool IsSnapshotsTabActive
    {
        get => SelectedTab == MediaTab.Snapshots;
        set { if (value) _ = SelectSnapshotsAsync(); }
    }

    // --- Filters ----------------------------------------------------------------
    // Searchable multi-select camera filter (shared with Events).
    public CameraPickerViewModel Cameras { get; } = new("Recordings.Cameras.WithRecordings", "Recordings.Cameras.FooterFormat");

    [ObservableProperty] private bool _motionOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeriodAll))]
    [NotifyPropertyChangedFor(nameof(IsPeriodToday))]
    [NotifyPropertyChangedFor(nameof(IsPeriod7))]
    [NotifyPropertyChangedFor(nameof(IsPeriod30))]
    private RecordingPeriod _period;

    public bool IsPeriodAll { get => Period == RecordingPeriod.All; set { if (value) Period = RecordingPeriod.All; } }
    public bool IsPeriodToday { get => Period == RecordingPeriod.Today; set { if (value) Period = RecordingPeriod.Today; } }
    public bool IsPeriod7 { get => Period == RecordingPeriod.Days7; set { if (value) Period = RecordingPeriod.Days7; } }
    public bool IsPeriod30 { get => Period == RecordingPeriod.Days30; set { if (value) Period = RecordingPeriod.Days30; } }

    partial void OnMotionOnlyChanged(bool value) => OnCameraOrMotionChanged();

    // Camera / motion also narrow the calendar highlight, so a lit day always
    // lists something.
    private void OnCameraOrMotionChanged()
    {
        ApplyFilter(resetPage: true);
        var cameras = Cameras.SelectedCameras.Select(i => i.Id).ToHashSet();
        var motionOnly = MotionOnly;
        _ = Calendar.SetRecordingFilterAsync(cameras.Count == 0 && !motionOnly
            ? null
            : r => (cameras.Count == 0 || cameras.Contains(r.CameraId)) && (!motionOnly || r.HasMotion));
    }

    // A period and a calendar day don't combine: picking one clears the other.
    partial void OnPeriodChanged(RecordingPeriod value)
    {
        if (!_syncingDayAndPeriod && value != RecordingPeriod.All && _dayFilter is not null)
        {
            _syncingDayAndPeriod = true;
            try { Calendar.ShowAllCommand.Execute(null); }
            finally { _syncingDayAndPeriod = false; }
            _dayFilter = null;
        }
        ApplyFilter(resetPage: true);
    }

    private void OnDaySelected(DateTime? day)
    {
        _dayFilter = day;
        if (!_syncingDayAndPeriod && day is not null && Period != RecordingPeriod.All)
        {
            _syncingDayAndPeriod = true;
            try { Period = RecordingPeriod.All; }
            finally { _syncingDayAndPeriod = false; }
        }
        ApplyFilter(resetPage: true);
    }

    // "Recordings: 12 · 1.4 GB" over the filtered set.
    [ObservableProperty] private string _summary = "";

    // --- Pagination ---------------------------------------------------------------
    // PageSize recordings per page; day headers are interleaved on top of that.
    public const int PageSize = 20;

    public ObservableCollection<object> PageItems { get; } = new();
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
        ApplyFilter(resetPage: false);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!CanNextPage) return;
        CurrentPage++;
        ApplyFilter(resetPage: false);
    }

    // CommandParameter is the boxed 1-based page number from the Pages binding
    // (object? sidesteps the RelayCommand<int> XAML render crash).
    [RelayCommand]
    private void GoToPage(object? page)
    {
        if (page is null) return;
        int oneBased;
        try { oneBased = Convert.ToInt32(page, CultureInfo.InvariantCulture); }
        catch (Exception) { return; }
        var target = oneBased - 1;
        if (target < 0 || target >= PageCount || target == CurrentPage) return;
        CurrentPage = target;
        ApplyFilter(resetPage: false);
    }

    // --- Load state -------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _isLoading;

    // Set when ListAsync throws — the page shows a localized error overlay
    // instead of the empty/loaded states. Cleared on the next successful load.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private string? _loadError;

    // No recordings at all vs. recordings hidden by the filters.
    public bool IsEmpty => IsLoaded && !IsLoading && LoadError is null && _allRows.Count == 0;
    public bool HasNoMatches => IsLoaded && !IsLoading && LoadError is null && _allRows.Count > 0 && PageItems.Count == 0;
    public bool HasRows => PageItems.Count > 0;
    public bool HasLoadError => LoadError is not null;

    // Opening the OS file manager only makes sense on desktop heads.
    public bool CanShowInFolder => !OverlayDialogPresenter.IsMobile;

    public RecordingsPageViewModel(
        IRecordingRepository repo,
        CameraDirectoryService cameras,
        IDialogService dialogs,
        SnapshotBrowserPageViewModel snapshots,
        ArchiveCalendarViewModel calendar,
        ILogger<RecordingsPageViewModel> logger)
    {
        _repo = repo;
        _cameras = cameras;
        _dialogs = dialogs;
        Snapshots = snapshots;
        Calendar = calendar;
        _logger = logger;
        Calendar.DaySelected += OnDaySelected;
        Cameras.SelectionChanged += OnCameraOrMotionChanged;
    }

    private void ApplyFilter(bool resetPage = false)
    {
        var today = DateTime.Now.Date;
        DateTime? since = Period switch
        {
            RecordingPeriod.Today => today,
            RecordingPeriod.Days7 => today.AddDays(-6),
            RecordingPeriod.Days30 => today.AddDays(-29),
            _ => null,
        };
        // Picker counts use every filter except the camera one itself.
        var inScope = _allRows.Where(r =>
                (!MotionOnly || r.HasMotion)
                && (since is null || r.StartedAtLocal >= since.Value)
                && (_dayFilter is null || r.StartedAtLocal.Date == _dayFilter.Value.Date))
            .ToList();
        Cameras.SetCounts(inScope.GroupBy(r => r.Recording.CameraId).ToDictionary(g => g.Key, g => g.Count()));
        var filtered = inScope.Where(r => Cameras.Matches(r.Recording.CameraId)).ToList();

        Summary = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Recordings.SummaryFormat"],
            filtered.Count, RecordingRowViewModel.FormatSize(filtered.Sum(r => Math.Max(0, r.Recording.SizeBytes))));

        var pageCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        if (pageCount != PageCount || Pages.Count != pageCount)
        {
            PageCount = pageCount;
            Pages.Clear();
            for (var i = 1; i <= pageCount; i++) Pages.Add(i);
        }
        CurrentPage = resetPage ? 0 : Math.Min(CurrentPage, pageCount - 1);

        // Per-day totals over the whole filtered set, so a header reads the
        // same whichever page the day starts on.
        var dayTotals = filtered
            .GroupBy(r => r.StartedAtLocal.Date)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Bytes: g.Sum(r => Math.Max(0, r.Recording.SizeBytes))));

        PageItems.Clear();
        DateTime? lastDay = null;
        foreach (var row in filtered.Skip(CurrentPage * PageSize).Take(PageSize))
        {
            var day = row.StartedAtLocal.Date;
            if (lastDay != day)
            {
                var totals = dayTotals[day];
                PageItems.Add(new RecordingDayHeader(day, totals.Count, totals.Bytes));
                lastDay = day;
            }
            PageItems.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    private void SelectRecordings() => SelectedTab = MediaTab.Recordings;

    [RelayCommand]
    private async Task SelectSnapshotsAsync()
    {
        SelectedTab = MediaTab.Snapshots;
        if (!Snapshots.IsLoaded && !Snapshots.IsLoading)
            await Snapshots.LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var recordings = await _repo.ListAsync(cameraId: null, ct).ConfigureAwait(true);
            var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
            var groups = (await _cameras.ListGroupsAsync(ct).ConfigureAwait(true)).ToDictionary(g => g.Id, g => g.Name);
            Cameras.SetCameras(cams.Select(c => new CameraPickSource(
                c.Id, c.Name, c.Host, c.GroupId is { } gid && groups.TryGetValue(gid, out var g) ? g : null)));
            var nameById = new Dictionary<CameraId, string>();
            foreach (var c in cams) nameById[c.Id] = c.Name;

            _allRows.Clear();
            foreach (var r in recordings.OrderByDescending(r => r.StartedAt))
            {
                var name = nameById.TryGetValue(r.CameraId, out var n) ? n : Localizer.Instance["Common.Unknown"];
                _allRows.Add(new RecordingRowViewModel(r, name));
            }
            ApplyFilter(resetPage: false);
            IsLoaded = true;

            await Calendar.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recordings");
            LoadError = Localizer.Instance["Recordings.LoadError"];
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void Play(RecordingRowViewModel? row)
    {
        if (row is null) return;
        WeakReferenceMessenger.Default.Send(new OpenRecordingMessage(row.Recording, row.CameraName));
    }

    [RelayCommand]
    private async Task ShowInFolderAsync(RecordingRowViewModel? row)
    {
        if (row is null) return;
        var dir = Path.GetDirectoryName(row.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (!await _dialogs.OpenUrlAsync(new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri).ConfigureAwait(true))
            _logger.LogWarning("Could not open folder {Dir}", dir);
    }

    [RelayCommand]
    private async Task CopyFileAsync(RecordingRowViewModel? row)
    {
        if (row is null) return;
        try { await _dialogs.CopyFileToClipboardAsync(row.FilePath).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Copy to clipboard failed for {Path}", row.FilePath); }
    }

    [RelayCommand]
    private async Task DeleteAsync(RecordingRowViewModel? row)
    {
        if (row is null) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Recordings.Dialog.DeleteTitle"],
            message: string.Format(Localizer.Instance["Recordings.Dialog.DeleteMessageFormat"], Path.GetFileName(row.FilePath)),
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            try { if (File.Exists(row.FilePath)) File.Delete(row.FilePath); }
            catch (IOException ex) { _logger.LogWarning(ex, "File still locked (recording in progress?)"); }

            await _repo.RemoveAsync(row.Recording.Id, CancellationToken.None).ConfigureAwait(true);
            _allRows.Remove(row);
            ApplyFilter(resetPage: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete recording {Id}", row.Recording.Id);
        }
    }
}

// "Today, 27 September · 3 · 142 MB" — interleaved into PageItems.
public sealed class RecordingDayHeader
{
    public RecordingDayHeader(DateTime day, int count, long bytes)
    {
        var culture = Localizer.Instance.Active == LangCode.Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");
        var today = DateTime.Now.Date;
        var date = day.ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy", culture);
        Title = day == today ? $"{Localizer.Instance["Recordings.Today"]}, {date}"
            : day == today.AddDays(-1) ? $"{Localizer.Instance["Recordings.Yesterday"]}, {date}"
            : date;
        Totals = $"{count} · {RecordingRowViewModel.FormatSize(bytes)}";
    }

    public string Title { get; }
    public string Totals { get; }
}

public sealed class RecordingRowViewModel
{
    public Recording Recording { get; }
    public string CameraName { get; }

    public string FilePath => Recording.FilePath;
    public string FileName => Path.GetFileName(Recording.FilePath);
    public DateTime StartedAtLocal => Recording.StartedAt.ToLocalTime();
    public bool IsLive => Recording.EndedAt is null;
    public bool HasMotion => Recording.HasMotion;

    // "14:52 – 14:58", or "15:02 —" while still recording.
    public string TimeRange => Recording.EndedAt is { } end
        ? $"{StartedAtLocal:HH:mm} – {end.ToLocalTime():HH:mm}"
        : $"{StartedAtLocal:HH:mm} —";

    public string Duration => Recording.EndedAt is { } end
        ? FormatDuration(end - Recording.StartedAt)
        : "—";

    public string SizeLabel => Recording.SizeBytes <= 0 ? "—" : FormatSize(Recording.SizeBytes);

    public RecordingRowViewModel(Recording recording, string cameraName)
    {
        Recording = recording;
        CameraName = cameraName;
    }

    // "6:29", "1:02:10".
    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{t.Minutes}:{t.Seconds:D2}";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F0} MB",
        _ => $"{bytes / 1024.0:F0} KB",
    };
}
