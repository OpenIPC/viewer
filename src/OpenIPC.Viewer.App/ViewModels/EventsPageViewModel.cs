using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public enum EventKindFilter { All, Motion, Detection }

public enum EventPeriod { Today, Days7, Days30, All }

public sealed partial class EventsPageViewModel : ViewModelBase, IDisposable
{
    // Upper bound on events pulled for one filter set; the list pages over it.
    private const int LoadLimit = 10000;

    // Events of one camera and kind closer than this fold into one episode.
    private static readonly TimeSpan EpisodeGap = TimeSpan.FromSeconds(60);

    // An event opens its recording slightly early, to show what led up to it.
    private static readonly TimeSpan PlayLeadIn = TimeSpan.FromSeconds(3);

    private readonly IEventRepository _repo;
    private readonly IRecordingRepository _recordingRepo;
    private readonly EventIngestionService _ingestion;
    private readonly ManualMotionEventSource _manualSource;
    private readonly CameraDirectoryService _cameras;
    private readonly ILogger<EventsPageViewModel> _logger;

    private readonly Dictionary<CameraId, string> _cameraNames = new();
    private readonly IDisposable _liveSub;

    // Newest first; the kind filter and episode folding slice it into PageItems.
    private readonly List<CameraEvent> _events = new();
    private Dictionary<CameraId, List<Recording>> _recordings = new();

    public string Title => Localizer.Instance["Nav.Events"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    private bool _isLoading;

    // No events for the camera/period at all vs. hidden by the kind filter.
    public bool IsEmpty => IsLoaded && !IsLoading && _events.Count == 0;
    public bool HasNoMatches => IsLoaded && !IsLoading && _events.Count > 0 && PageItems.Count == 0;
    public bool HasRows => PageItems.Count > 0;

    // Searchable multi-select camera filter (shared with Recordings).
    public CameraPickerViewModel Cameras { get; } = new("Events.Cameras.WithEvents", "Events.Cameras.FooterFormat");

    // Clicking a camera name in a row narrows the list to it.
    [RelayCommand]
    private void AddCameraFilter(EventEpisodeRow? row)
    {
        if (row is not null) Cameras.Select(row.CameraId);
    }

    // --- Filters ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKindAll))]
    [NotifyPropertyChangedFor(nameof(IsKindMotion))]
    [NotifyPropertyChangedFor(nameof(IsKindDetection))]
    private EventKindFilter _kindFilter;

    public bool IsKindAll { get => KindFilter == EventKindFilter.All; set { if (value) KindFilter = EventKindFilter.All; } }
    public bool IsKindMotion { get => KindFilter == EventKindFilter.Motion; set { if (value) KindFilter = EventKindFilter.Motion; } }
    public bool IsKindDetection { get => KindFilter == EventKindFilter.Detection; set { if (value) KindFilter = EventKindFilter.Detection; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeriodToday))]
    [NotifyPropertyChangedFor(nameof(IsPeriod7))]
    [NotifyPropertyChangedFor(nameof(IsPeriod30))]
    [NotifyPropertyChangedFor(nameof(IsPeriodAll))]
    private EventPeriod _period = EventPeriod.Days7;

    public bool IsPeriodToday { get => Period == EventPeriod.Today; set { if (value) Period = EventPeriod.Today; } }
    public bool IsPeriod7 { get => Period == EventPeriod.Days7; set { if (value) Period = EventPeriod.Days7; } }
    public bool IsPeriod30 { get => Period == EventPeriod.Days30; set { if (value) Period = EventPeriod.Days30; } }
    public bool IsPeriodAll { get => Period == EventPeriod.All; set { if (value) Period = EventPeriod.All; } }

    // Fold bursts (e.g. a car parked under an AI camera) into one row.
    [ObservableProperty] private bool _groupBursts = true;

    // Period changes what is loaded; cameras, kind and grouping only re-slice.
    partial void OnPeriodChanged(EventPeriod value) => _ = ReloadAsync(CancellationToken.None);
    partial void OnKindFilterChanged(EventKindFilter value) => ApplyFilter(resetPage: true);
    partial void OnGroupBurstsChanged(bool value) => ApplyFilter(resetPage: true);

    // "Events: 87 · episodes: 9"
    [ObservableProperty] private string _summary = "";

    // --- Pagination ---------------------------------------------------------------
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

    public EventsPageViewModel(
        IEventRepository repo,
        IRecordingRepository recordingRepo,
        EventIngestionService ingestion,
        ManualMotionEventSource manualSource,
        CameraDirectoryService cameras,
        ILogger<EventsPageViewModel> logger)
    {
        _repo = repo;
        _recordingRepo = recordingRepo;
        _ingestion = ingestion;
        _manualSource = manualSource;
        _cameras = cameras;
        _logger = logger;

        // Live updates: new events stream in as the ingestion service emits
        // them. Both new (open) and finalized events come through Events; we
        // de-dup by Id in OnLiveEvent.
        _liveSub = _ingestion.Events.Subscribe(new EventObserver(this));
        Cameras.SelectionChanged += () => ApplyFilter(resetPage: true);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
        var groups = (await _cameras.ListGroupsAsync(ct).ConfigureAwait(true)).ToDictionary(g => g.Id, g => g.Name);
        _cameraNames.Clear();
        foreach (var c in cams) _cameraNames[c.Id] = c.Name;
        Cameras.SetCameras(cams.Select(c => new CameraPickSource(
            c.Id, c.Name, c.Host, c.GroupId is { } gid && groups.TryGetValue(gid, out var g) ? g : null)));
        await ReloadAsync(ct).ConfigureAwait(true);
    }

    private DateTime? PeriodStartUtc()
    {
        var today = DateTime.Now.Date;
        DateTime? local = Period switch
        {
            EventPeriod.Today => today,
            EventPeriod.Days7 => today.AddDays(-6),
            EventPeriod.Days30 => today.AddDays(-29),
            _ => null,
        };
        return local?.ToUniversalTime();
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var events = await _repo.ListAsync(
                cameraId: null,
                kind: null,
                since: PeriodStartUtc(),
                limit: LoadLimit,
                ct).ConfigureAwait(true);
            _events.Clear();
            _events.AddRange(events.OrderByDescending(e => e.OccurredAt));

            // Recordings back the "▶ play from here" action.
            var recordings = await _recordingRepo.ListAsync(cameraId: null, ct).ConfigureAwait(true);
            _recordings = recordings
                .GroupBy(r => r.CameraId)
                .ToDictionary(g => g.Key, g => g.ToList());

            ApplyFilter(resetPage: true);
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load events");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool MatchesKind(CameraEvent e) => KindFilter switch
    {
        EventKindFilter.Motion => e.Kind == EventKind.Motion,
        EventKindFilter.Detection => e.Kind == EventKind.Detection,
        _ => true,
    };

    private void ApplyFilter(bool resetPage)
    {
        var ofKind = _events.Where(MatchesKind).ToList();

        // Picker counts ignore the camera selection itself, so unticked cameras
        // still show how busy they are.
        Cameras.SetCounts(ofKind.GroupBy(e => e.CameraId).ToDictionary(g => g.Key, g => g.Count()));
        var filtered = ofKind.Where(e => Cameras.Matches(e.CameraId)).ToList();
        var episodes = GroupBursts ? FoldEpisodes(filtered) : filtered.Select(e => new List<CameraEvent> { e }).ToList();

        Summary = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Events.SummaryFormat"],
            filtered.Count, episodes.Count);

        var pageCount = Math.Max(1, (episodes.Count + PageSize - 1) / PageSize);
        if (pageCount != PageCount || Pages.Count != pageCount)
        {
            PageCount = pageCount;
            Pages.Clear();
            for (var i = 1; i <= pageCount; i++) Pages.Add(i);
        }
        CurrentPage = resetPage ? 0 : Math.Min(CurrentPage, pageCount - 1);

        // Per-day event counts over the whole filtered set.
        var dayTotals = filtered
            .GroupBy(e => e.OccurredAt.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Count());

        PageItems.Clear();
        DateTime? lastDay = null;
        foreach (var episode in episodes.Skip(CurrentPage * PageSize).Take(PageSize))
        {
            var row = BuildRow(episode);
            var day = row.LatestLocal.Date;
            if (lastDay != day)
            {
                PageItems.Add(new EventDayHeader(day, dayTotals.TryGetValue(day, out var n) ? n : 0));
                lastDay = day;
            }
            PageItems.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasRows));
    }

    // Walks newest → oldest; an event joins its camera+kind's open episode when
    // it is within EpisodeGap of that episode's oldest event. Episodes come out
    // ordered by their latest event, newest first. Each is newest-first too.
    private static List<List<CameraEvent>> FoldEpisodes(IReadOnlyList<CameraEvent> newestFirst)
    {
        var result = new List<List<CameraEvent>>();
        var open = new Dictionary<(CameraId, EventKind), List<CameraEvent>>();
        foreach (var e in newestFirst)
        {
            var key = (e.CameraId, e.Kind);
            if (open.TryGetValue(key, out var episode) && episode[^1].OccurredAt - e.OccurredAt <= EpisodeGap)
            {
                episode.Add(e);
                continue;
            }
            episode = new List<CameraEvent> { e };
            open[key] = episode;
            result.Add(episode);
        }
        return result;
    }

    private EventEpisodeRow BuildRow(IReadOnlyList<CameraEvent> episode)
    {
        var first = episode[^1];
        var name = _cameraNames.TryGetValue(first.CameraId, out var n) ? n : Localizer.Instance["Common.Unknown"];
        var recording = FindRecording(first.CameraId, first.OccurredAt);
        TimeSpan? offset = null;
        if (recording is not null)
        {
            var o = first.OccurredAt - recording.StartedAt - PlayLeadIn;
            offset = o > TimeSpan.Zero ? o : TimeSpan.Zero;
        }
        return new EventEpisodeRow(episode, name, recording, offset);
    }

    // The camera's recording whose span covers the moment, if any.
    private Recording? FindRecording(CameraId camera, DateTime atUtc)
    {
        if (!_recordings.TryGetValue(camera, out var list)) return null;
        foreach (var r in list)
        {
            var end = r.EndedAt ?? DateTime.UtcNow;
            if (r.StartedAt <= atUtc && atUtc <= end) return r;
        }
        return null;
    }

    [RelayCommand]
    private void PlayFromEvent(EventEpisodeRow? row)
    {
        if (row?.Recording is not { } recording) return;
        WeakReferenceMessenger.Default.Send(new OpenRecordingMessage(recording, row.CameraName, row.RecordingOffset));
    }

    [RelayCommand]
    private void OpenCamera(EventEpisodeRow? row)
    {
        if (row is null) return;
        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(row.CameraId));
    }

    [RelayCommand]
    private void ShowAllTime() => Period = EventPeriod.All;

    [RelayCommand]
    private void SimulateMotion()
    {
        // Phase 7 §7.1 punt: real per-protocol motion sources aren't built yet
        // (Majestic endpoint TBD; ONVIF PullPoint not in Onvif.Core). This
        // exercises the full ingestion -> repo -> UI path against the first
        // available camera so the plumbing is testable.
        var target = Cameras.SelectedCameras.Select(i => (CameraId?)i.Id).FirstOrDefault()
                     ?? _cameraNames.Keys.FirstOrDefault();
        if (target == default)
        {
            _logger.LogInformation("Simulate motion: no cameras to target");
            return;
        }
        _manualSource.Trigger(target);
    }

    private void OnLiveEvent(CameraEvent ev)
    {
        // Respect the period for live updates too (cameras filter in memory).
        if (PeriodStartUtc() is { } since && ev.OccurredAt < since)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var idx = _events.FindIndex(e => e.Id == ev.Id);
            if (idx >= 0) _events[idx] = ev;
            else _events.Insert(0, ev);
            if (_events.Count > LoadLimit) _events.RemoveAt(_events.Count - 1);
            ApplyFilter(resetPage: false);
        });
    }

    public void Dispose()
    {
        _liveSub.Dispose();
    }

    private sealed class EventObserver : IObserver<CameraEvent>
    {
        private readonly EventsPageViewModel _owner;
        public EventObserver(EventsPageViewModel owner) => _owner = owner;
        public void OnNext(CameraEvent value) => _owner.OnLiveEvent(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

// "5 July · 87" — interleaved into PageItems.
public sealed class EventDayHeader
{
    public EventDayHeader(DateTime day, int count)
    {
        var culture = Localizer.Instance.Active == LangCode.Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");
        var today = DateTime.Now.Date;
        var date = day.ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy", culture);
        Title = day == today ? $"{Localizer.Instance["Recordings.Today"]}, {date}"
            : day == today.AddDays(-1) ? $"{Localizer.Instance["Recordings.Yesterday"]}, {date}"
            : date;
        Count = count.ToString(CultureInfo.CurrentCulture);
    }

    public string Title { get; }
    public string Count { get; }
}

// One row: a single event, or a burst of same-camera same-kind events.
public sealed class EventEpisodeRow
{
    public EventEpisodeRow(IReadOnlyList<CameraEvent> newestFirst, string cameraName, Recording? recording, TimeSpan? recordingOffset)
    {
        var latest = newestFirst[0];
        var earliest = newestFirst[^1];
        CameraId = latest.CameraId;
        CameraName = cameraName;
        Kind = latest.Kind;
        Count = newestFirst.Count;
        Recording = recording;
        RecordingOffset = recordingOffset;
        LatestLocal = latest.OccurredAt.ToLocalTime();
        var earliestLocal = earliest.OccurredAt.ToLocalTime();
        TimeLabel = Count == 1 || LatestLocal - earliestLocal < TimeSpan.FromMinutes(1)
            ? $"{LatestLocal:HH:mm:ss}"
            : $"{earliestLocal:HH:mm} – {LatestLocal:HH:mm}";
        Description = Describe(newestFirst);
        Classes = newestFirst[0].Kind == EventKind.Detection ? MaxClasses(newestFirst) : Array.Empty<DetectionClassChip>();
        Tooltip = string.Join("  ·  ", new[] { latest.Source, CountLabel }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public CameraId CameraId { get; }
    public string CameraName { get; }
    public EventKind Kind { get; }
    public int Count { get; }
    public DateTime LatestLocal { get; }
    public string TimeLabel { get; }
    public string Description { get; }

    // Detection: one icon chip per class ("🚗 машина ×4"); empty for motion,
    // which shows Description instead.
    public IReadOnlyList<DetectionClassChip> Classes { get; }
    public bool HasClasses => Classes.Count > 0;
    public string Tooltip { get; }
    public Recording? Recording { get; }
    public TimeSpan? RecordingOffset { get; }

    public bool IsDetection => Kind == EventKind.Detection;
    public bool IsMotion => Kind != EventKind.Detection;
    public bool HasRecording => Recording is not null;
    public bool IsBurst => Count > 1;
    public string CountLabel => Count > 1 ? $"×{Count}" : "";

    // Detection: per-class maximum across the burst ("car ×5, person ×1"),
    // with class names localized where we have them. Motion: its summary or
    // a plain "motion".
    private static IReadOnlyList<DetectionClassChip> MaxClasses(IReadOnlyList<CameraEvent> events)
    {
        var max = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
            foreach (var (label, count) in DetectionClasses.Parse(e.Summary))
                max[label] = max.TryGetValue(label, out var cur) ? Math.Max(cur, count) : count;
        return max.OrderByDescending(kv => kv.Value)
            .Select(kv => new DetectionClassChip(kv.Key, kv.Value, DetectionClasses.IconKey(kv.Key)))
            .ToList();
    }

    private static string Describe(IReadOnlyList<CameraEvent> events)
    {
        if (events[0].Kind != EventKind.Detection)
            return events.Select(e => e.Summary).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                   ?? Localizer.Instance["Recordings.Motion"];

        var max = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
            foreach (var (label, count) in DetectionClasses.Parse(e.Summary))
                max[label] = max.TryGetValue(label, out var cur) ? Math.Max(cur, count) : count;
        if (max.Count == 0) return Localizer.Instance["Events.Kind.Detection"];
        return string.Join(", ", max.OrderByDescending(kv => kv.Value).Select(kv => $"{DetectionClasses.Localize(kv.Key)} ×{kv.Value}"));
    }
}
