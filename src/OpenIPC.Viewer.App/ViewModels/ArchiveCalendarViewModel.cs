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
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.App.ViewModels;

// Archive month calendar (Phase 16.3). Highlights days with recordings/events
// (intensity ∝ volume) and raises DaySelected so the recordings list filters to
// the chosen local day. Aggregates are recomputed per visible month.
public sealed partial class ArchiveCalendarViewModel : ViewModelBase
{
    private readonly IRecordingRepository _recordings;
    private readonly IEventRepository _events;
    private readonly ILogger<ArchiveCalendarViewModel> _logger;

    // Per-month aggregate cache (Phase 16.3). Flipping months within a session
    // reuses it; LoadAsync (page re-entry) clears it so new recordings show.
    private readonly Dictionary<(int Year, int Month), MonthActivity> _cache = new();

    private sealed record MonthActivity(IReadOnlyDictionary<DateTime, DayActivity> Activity, int MaxTotal);

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
        new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

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

                var activity = CalendarActivity.ForMonth(
                    Year, Month,
                    recordings.Select(r => r.StartedAt),
                    events.Select(e => e.OccurredAt),
                    tz);

                var maxTotal = activity.Count == 0 ? 0 : activity.Values.Max(d => d.Total);
                month = new MonthActivity(activity, maxTotal);
                _cache[(Year, Month)] = month;
            }

            BuildGrid(month.Activity, month.MaxTotal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load calendar activity for {Year}-{Month}", Year, Month);
        }
    }

    private void BuildGrid(IReadOnlyDictionary<DateTime, DayActivity> activity, int maxTotal)
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
                total: day.Total,
                intensity: CalendarActivity.Intensity(day, maxTotal),
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
        if (cell is null || !cell.HasActivity) return;
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
    public CalendarDayCell(DateTime date, bool inMonth, int total, double intensity, bool isToday)
    {
        Date = date;
        InMonth = inMonth;
        Total = total;
        Intensity = intensity;
        IsToday = isToday;
    }

    public DateTime Date { get; }
    public bool InMonth { get; }
    public int Total { get; }
    public double Intensity { get; }
    public bool IsToday { get; }
    public bool HasActivity => Total > 0;
    public string DayNumber => Date.Day.ToString(CultureInfo.CurrentCulture);

    // Accent opacity for the cell: faint days still read as active (floor 0.25);
    // empty days stay transparent.
    public double Shade => HasActivity ? Math.Max(0.25, Intensity) : 0.0;

    [ObservableProperty] private bool _isSelected;
}
