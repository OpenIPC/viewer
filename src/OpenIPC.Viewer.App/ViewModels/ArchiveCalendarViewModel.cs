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
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.App.ViewModels;

// Archive month calendar (Phase 16.3). Days with recordings get an accent fill
// (intensity ∝ recording count); event-only days just get a dot, since picking
// them would filter the recordings list to nothing. Raises DaySelected so the
// list filters to the chosen local day. Aggregates are recomputed per month.
public sealed partial class ArchiveCalendarViewModel : ViewModelBase
{
    private readonly IRecordingRepository _recordings;
    private readonly IEventRepository _events;
    private readonly ILogger<ArchiveCalendarViewModel> _logger;

    // Per-month aggregate cache (Phase 16.3). Flipping months within a session
    // reuses it; LoadAsync (page re-entry) clears it so new recordings show.
    private readonly Dictionary<(int Year, int Month), MonthActivity> _cache = new();

    private sealed record MonthActivity(IReadOnlyDictionary<DateTime, DayActivity> Activity, int MaxRecordings);

    // The recordings page narrows the calendar to its camera / motion filter,
    // so only days that would actually list something light up.
    private Func<Recording, bool>? _recordingFilter;

    // Fired with the selected local date, or null for "all days".
    public event Action<DateTime?>? DaySelected;

    public ArchiveCalendarViewModel(
        IRecordingRepository recordings,
        IEventRepository events,
        ILogger<ArchiveCalendarViewModel> logger)
    {
        _recordings = recordings;
        _events = events;
        _logger = logger;
        var now = DateTime.Now;
        _year = now.Year;
        _month = now.Month;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthLabel))]
    private int _year;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthLabel))]
    private int _month;

    [ObservableProperty] private DateTime? _selectedDate;

    public ObservableCollection<CalendarDayCell> Days { get; } = new();

    public string MonthLabel =>
        UiCulture.TextInfo.ToTitleCase(new DateTime(Year, Month, 1).ToString("MMMM yyyy", UiCulture));

    // "Recordings this month: 12" — tells at a glance whether a month has any.
    [ObservableProperty] private string _monthSummary = "";

    // First-of-month dates that hold at least one (filtered) recording. Lets an
    // empty month offer a jump to the nearest one instead of blind paging.
    private List<DateTime> _recordingMonths = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNearestMonth))]
    private DateTime? _nearestMonth;

    public bool HasNearestMonth => NearestMonth is not null;

    public string NearestMonthLabel => NearestMonth is { } m
        ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Calendar.NearestFormat"],
            UiCulture.TextInfo.ToTitleCase(m.ToString("MMMM yyyy", UiCulture)))
        : "";

    partial void OnNearestMonthChanged(DateTime? value) => OnPropertyChanged(nameof(NearestMonthLabel));

    [RelayCommand]
    private Task JumpToNearestMonthAsync()
    {
        if (NearestMonth is not { } m) return Task.CompletedTask;
        Year = m.Year;
        Month = m.Month;
        return ShowMonthAsync(CancellationToken.None);
    }

    // Follows the app language setting rather than the OS culture.
    private static CultureInfo UiCulture => Localizer.Instance.Active == LangCode.Russian
        ? CultureInfo.GetCultureInfo("ru-RU")
        : CultureInfo.GetCultureInfo("en-US");

    public async Task SetRecordingFilterAsync(Func<Recording, bool>? filter)
    {
        _recordingFilter = filter;
        _cache.Clear();
        await ShowMonthAsync(CancellationToken.None).ConfigureAwait(true);
    }

    // Page-entry refresh: drop cached months so freshly-recorded days appear.
    public Task LoadAsync(CancellationToken ct)
    {
        _cache.Clear();
        return ShowMonthAsync(ct);
    }

    private async Task ShowMonthAsync(CancellationToken ct)
    {
        try
        {
            if (!_cache.TryGetValue((Year, Month), out var month))
            {
                var tz = TimeZoneInfo.Local;
                var recordings = await _recordings.ListAsync(cameraId: null, ct).ConfigureAwait(true);
                // Events since the start of the month (local) minus a day, in UTC.
                var monthStartUtc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(new DateTime(Year, Month, 1).AddDays(-1), DateTimeKind.Unspecified), tz);
                var events = await _events
                    .ListAsync(cameraId: null, kind: null, since: monthStartUtc, limit: 20000, ct)
                    .ConfigureAwait(true);

                var filter = _recordingFilter;
                var matching = recordings.Where(r => filter is null || filter(r)).ToList();
                _recordingMonths = matching
                    .Select(r => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc), tz))
                    .Select(local => new DateTime(local.Year, local.Month, 1))
                    .Distinct()
                    .OrderBy(d => d)
                    .ToList();
                var activity = CalendarActivity.ForMonth(
                    Year, Month,
                    matching.Select(r => r.StartedAt),
                    events.Select(e => e.OccurredAt),
                    tz);

                var maxRecordings = activity.Count == 0 ? 0 : activity.Values.Max(d => d.RecordingCount);
                month = new MonthActivity(activity, maxRecordings);
                _cache[(Year, Month)] = month;
            }

            BuildGrid(month.Activity, month.MaxRecordings);
            var monthCount = month.Activity.Values.Sum(d => d.RecordingCount);
            MonthSummary = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Calendar.MonthSummaryFormat"], monthCount);

            // Empty month: point at the closest month that has recordings,
            // preferring the past (that's where an archive usually is).
            var shown = new DateTime(Year, Month, 1);
            NearestMonth = monthCount > 0
                ? null
                : _recordingMonths.Where(m => m < shown).Select(m => (DateTime?)m).LastOrDefault()
                  ?? _recordingMonths.Where(m => m > shown).Select(m => (DateTime?)m).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load calendar activity for {Year}-{Month}", Year, Month);
        }
    }

    private void BuildGrid(IReadOnlyDictionary<DateTime, DayActivity> activity, int maxRecordings)
    {
        Days.Clear();
        var first = new DateTime(Year, Month, 1);
        // Grid starts on the Monday on/before the 1st (ISO week start).
        var offset = ((int)first.DayOfWeek + 6) % 7; // Mon=0 … Sun=6
        var gridStart = first.AddDays(-offset);
        var today = DateTime.Now.Date;

        for (var i = 0; i < 42; i++) // 6 weeks
        {
            var date = gridStart.AddDays(i).Date;
            activity.TryGetValue(date, out var day);
            var cell = new CalendarDayCell(
                date,
                inMonth: date.Month == Month && date.Year == Year,
                recordingCount: day.RecordingCount,
                eventCount: day.EventCount,
                intensity: maxRecordings <= 0 ? 0 : Math.Clamp(day.RecordingCount / (double)maxRecordings, 0.0, 1.0),
                isToday: date == today)
            {
                IsSelected = SelectedDate is { } sel && sel.Date == date,
            };
            Days.Add(cell);
        }
    }

    [RelayCommand]
    private Task PrevMonthAsync()
    {
        Shift(-1);
        return ShowMonthAsync(CancellationToken.None); // cached
    }

    [RelayCommand]
    private Task NextMonthAsync()
    {
        Shift(1);
        return ShowMonthAsync(CancellationToken.None); // cached
    }

    private void Shift(int months)
    {
        var d = new DateTime(Year, Month, 1).AddMonths(months);
        Year = d.Year;
        Month = d.Month;
    }

    [RelayCommand]
    private void SelectDay(CalendarDayCell? cell)
    {
        if (cell is null || !cell.HasRecordings) return;
        // Toggle off if the same day is tapped again.
        if (SelectedDate is { } cur && cur.Date == cell.Date.Date)
        {
            SelectedDate = null;
            foreach (var c in Days) c.IsSelected = false;
            DaySelected?.Invoke(null);
            return;
        }
        SelectedDate = cell.Date.Date;
        foreach (var c in Days) c.IsSelected = c.Date.Date == cell.Date.Date;
        DaySelected?.Invoke(cell.Date.Date);
    }

    [RelayCommand]
    private void ShowAll()
    {
        SelectedDate = null;
        foreach (var c in Days) c.IsSelected = false;
        DaySelected?.Invoke(null);
    }
}

public sealed partial class CalendarDayCell : ObservableObject
{
    public CalendarDayCell(DateTime date, bool inMonth, int recordingCount, int eventCount, double intensity, bool isToday)
    {
        Date = date;
        InMonth = inMonth;
        RecordingCount = recordingCount;
        EventCount = eventCount;
        Intensity = intensity;
        IsToday = isToday;
    }

    public DateTime Date { get; }
    public bool InMonth { get; }
    public int RecordingCount { get; }
    public int EventCount { get; }
    public double Intensity { get; }
    public bool IsToday { get; }
    public bool HasRecordings => RecordingCount > 0;
    public bool HasEventsOnly => RecordingCount == 0 && EventCount > 0;
    public string DayNumber => Date.Day.ToString(CultureInfo.CurrentCulture);

    // Accent opacity for a recording day: even a single recording reads clearly
    // (floor 0.45); days without recordings stay transparent.
    public double Shade => HasRecordings ? 0.45 + 0.55 * Intensity : 0.0;

    public string? Tooltip => RecordingCount == 0 && EventCount == 0
        ? null
        : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Calendar.DayTooltipFormat"], RecordingCount, EventCount);

    [ObservableProperty] private bool _isSelected;
}
