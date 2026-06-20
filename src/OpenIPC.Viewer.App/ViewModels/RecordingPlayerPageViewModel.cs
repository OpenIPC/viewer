using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Timeline;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public enum PlayerEventFilter { All, Motion, Detection }

// Phase 16 — playback of a recorded segment. Hosts an IPlaybackSession (file
// decode, transport, seek) and exposes it as IVideoSession so the existing
// RtspVideoView renders frames unchanged. The timeline/calendar/export layers
// (Slices C–F) grow on top of this page.
public sealed partial class RecordingPlayerPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Recording _recording;
    private readonly IPlaybackEngine _engine;
    private readonly IMediaProbe _probe;
    private readonly IEventRepository _events;
    private readonly IClipExporter _exporter;
    private readonly IDialogService _dialogs;
    private readonly OpenIPC.Viewer.Core.Platform.IShareService _share;
    private readonly ILogger<RecordingPlayerPageViewModel> _logger;

    private IPlaybackSession? _playback;
    private IDisposable? _stateSub;
    private IDisposable? _positionSub;
    private bool _activating;
    private bool _disposed;

    public RecordingPlayerPageViewModel(
        Recording recording,
        string cameraName,
        IPlaybackEngine engine,
        IMediaProbe probe,
        IEventRepository events,
        IClipExporter exporter,
        IDialogService dialogs,
        OpenIPC.Viewer.Core.Platform.IShareService share,
        ILogger<RecordingPlayerPageViewModel> logger)
    {
        _recording = recording;
        CameraName = cameraName;
        _engine = engine;
        _probe = probe;
        _events = events;
        _exporter = exporter;
        _dialogs = dialogs;
        _share = share;
        _logger = logger;

        // Timeline starts as the single recording's span; refined once the exact
        // duration is probed. Times are UTC (the control formats to local).
        TimelineStart = recording.StartedAt;
        TimelineEnd = recording.EndedAt is { } e && e > recording.StartedAt
            ? e
            : recording.StartedAt.AddMinutes(1);
        Segments = new[] { new TimelineSegment(TimelineStart, TimelineEnd) };
    }

    public string Title => Path.GetFileName(_recording.FilePath);
    public string CameraName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    private SessionState _state = SessionState.Idle;

    [ObservableProperty] private IVideoSession? _videoSession;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionLabel))]
    private TimeSpan _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationLabel))]
    [NotifyPropertyChangedFor(nameof(DurationSeconds))]
    private TimeSpan _duration;

    // Timeline (16.4): absolute-time track range, segments, event markers, and
    // the playhead as an absolute time derived from Position.
    [ObservableProperty] private DateTime _timelineStart;
    [ObservableProperty] private DateTime _timelineEnd;
    [ObservableProperty] private IReadOnlyList<TimelineSegment>? _segments;
    [ObservableProperty] private IReadOnlyList<TimelineMarker>? _markers;
    [ObservableProperty] private DateTime? _playheadTime;

    // Day event list (16.6): the same motion/detection events as the timeline,
    // in a clickable side list with a type filter.
    private readonly List<TimelineMarker> _allEventMarkers = new();
    public ObservableCollection<TimelineMarker> EventList { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterMotion))]
    [NotifyPropertyChangedFor(nameof(IsFilterDetection))]
    [NotifyPropertyChangedFor(nameof(HasEvents))]
    private PlayerEventFilter _eventFilter = PlayerEventFilter.All;

    public bool IsFilterAll => EventFilter == PlayerEventFilter.All;
    public bool IsFilterMotion => EventFilter == PlayerEventFilter.Motion;
    public bool IsFilterDetection => EventFilter == PlayerEventFilter.Detection;
    public bool HasEvents => EventList.Count > 0;

    // Clip export (16.5): in/out marks (absolute UTC) on the timeline + state.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private DateTime? _selectionStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private DateTime? _selectionEnd;

    [ObservableProperty] private bool _preciseExport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isExporting;

    [ObservableProperty] private double _exportFraction;
    [ObservableProperty] private string? _exportStatus;

    public bool HasSelection =>
        SelectionStart is { } s && SelectionEnd is { } e && Math.Abs((e - s).TotalSeconds) >= 0.5;

    public string SelectionLabel =>
        HasSelection
            ? $"{Format(Min(SelectionStart!.Value, SelectionEnd!.Value) - TimelineStart)} – {Format(Max(SelectionStart!.Value, SelectionEnd!.Value) - TimelineStart)}"
            : "—";

    public bool IsConnecting => VideoSession is not null && State == SessionState.Connecting;
    public bool IsFailed => State == SessionState.Failed;
    public bool IsPlaying => State == SessionState.Playing;

    public string PositionLabel => Format(Position);
    public string DurationLabel => Format(Duration);
    public double DurationSeconds => Duration.TotalSeconds;

    // Two-way bound to the seek slider's Value. Incoming playback updates raise
    // PositionSeconds (via OnPositionChanged) so the thumb tracks; a drag from
    // the UI lands here and, if it diverges from the playhead by more than a
    // second, is treated as a seek. The threshold absorbs the rounding churn of
    // per-frame position pushes so they don't loop back as phantom seeks.
    public double PositionSeconds
    {
        get => Position.TotalSeconds;
        set
        {
            if (Math.Abs(value - Position.TotalSeconds) < 1.0)
                return;
            _ = SeekToAsync(TimeSpan.FromSeconds(value));
        }
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        if (VideoSession is not null || _activating || _disposed)
            return;
        _activating = true;
        try
        {
            // Pre-probe the exact duration so the seek bar has a correct range
            // before the first frame arrives (16.2 — don't guess the length).
            try
            {
                var info = await _probe.ProbeAsync(_recording.FilePath, ct).ConfigureAwait(true);
                if (info.Duration > TimeSpan.Zero)
                    Duration = info.Duration;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pre-probe failed for {Path}", _recording.FilePath);
            }

            await LoadMarkersAsync(ct).ConfigureAwait(true);

            var session = _engine.OpenFile(PlaybackOptions.Default(_recording.FilePath));
            _stateSub = session.StateChanged.Subscribe(s => Dispatcher.UIThread.Post(() =>
            {
                State = s;
                if (s == SessionState.Failed)
                    ErrorMessage = session.LastError;
            }));
            _positionSub = session.PositionChanged.Subscribe(p => Dispatcher.UIThread.Post(() =>
            {
                Position = p;
                // Duration is only known after the decode thread probes the file;
                // adopt it on the first tick that reports a non-zero length.
                if (session.Duration > TimeSpan.Zero && Duration != session.Duration)
                    Duration = session.Duration;
            }));
            _playback = session;
            VideoSession = session;
            await session.StartAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open recording {Path}", _recording.FilePath);
            ErrorMessage = ex.Message;
            State = SessionState.Failed;
        }
        finally
        {
            _activating = false;
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_playback is null) return;
        if (_playback.IsPaused) _playback.Play();
        else _playback.Pause();
    }

    [RelayCommand]
    private Task RestartAsync() => SeekToAsync(TimeSpan.Zero);

    // Invoked by the timeline on click/marker-tap with an absolute UTC time.
    [RelayCommand]
    private Task SeekToTime(DateTime target) => SeekToAsync(target - TimelineStart);

    [RelayCommand]
    private void SetIn()
    {
        if (PlayheadTime is { } t) SelectionStart = t;
    }

    [RelayCommand]
    private void SetOut()
    {
        if (PlayheadTime is { } t) SelectionEnd = t;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectionStart = null;
        SelectionEnd = null;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var startAbs = Min(SelectionStart!.Value, SelectionEnd!.Value);
        var endAbs = Max(SelectionStart!.Value, SelectionEnd!.Value);
        var startOff = startAbs - TimelineStart;
        if (startOff < TimeSpan.Zero) startOff = TimeSpan.Zero;
        var endOff = endAbs - TimelineStart;

        var suggested = Path.GetFileNameWithoutExtension(_recording.FilePath) + "_clip.mp4";
        var dest = await _dialogs.PickSaveFileAsync(suggested, Localizer.Instance["Recordings.ExportTitle"], "mp4")
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(dest)) return;

        IsExporting = true;
        ExportFraction = 0;
        ExportStatus = null;
        try
        {
            var request = new ClipExportRequest(_recording.FilePath, dest!, startOff, endOff, PreciseExport);
            var progress = new Progress<double>(p => ExportFraction = p);
            await _exporter.ExportAsync(request, progress, CancellationToken.None).ConfigureAwait(true);
            ExportStatus = Path.GetFileName(dest);

            // On mobile the picked file is invisible to the user, so hand the
            // clip to the native share sheet (Phase 16.5 "в галерею/share").
            if (_share.SupportsSystemShare)
                await _share.ShareFileAsync(dest!, "video/mp4", CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip export failed for {Path}", _recording.FilePath);
            ExportStatus = ex.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanExport() => HasSelection && !IsExporting;

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    private async Task SeekToAsync(TimeSpan position)
    {
        if (_playback is null) return;
        Position = position; // optimistic — keeps the slider responsive mid-drag
        try { await _playback.SeekAsync(position, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Seek to {Pos} failed", position); }
    }

    [RelayCommand]
    private void Back() => WeakReferenceMessenger.Default.Send(new GoBackToRecordingsMessage());

    partial void OnPositionChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(PositionSeconds));
        PlayheadTime = TimelineStart + value;
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return;
        TimelineEnd = TimelineStart + value;
        Segments = new[] { new TimelineSegment(TimelineStart, TimelineEnd) };
    }

    private async Task LoadMarkersAsync(CancellationToken ct)
    {
        try
        {
            var list = await _events
                .ListAsync(_recording.CameraId, kind: null, since: TimelineStart.AddSeconds(-1), limit: 2000, ct)
                .ConfigureAwait(true);

            var markers = new List<TimelineMarker>();
            foreach (var ev in list)
            {
                if (ev.OccurredAt < TimelineStart || ev.OccurredAt > TimelineEnd)
                    continue;
                var kind = ev.Kind switch
                {
                    EventKind.Detection => TimelineMarkerKind.Detection,
                    EventKind.Motion => TimelineMarkerKind.Motion,
                    _ => TimelineMarkerKind.Other,
                };
                if (kind == TimelineMarkerKind.Other)
                    continue; // only motion/detection belong on the archive track
                var label = $"{ev.OccurredAt.ToLocalTime():HH:mm:ss} · {ev.Summary ?? kind.ToString()}";
                markers.Add(new TimelineMarker(ev.OccurredAt, kind, label));
            }
            Markers = markers;
            _allEventMarkers.Clear();
            _allEventMarkers.AddRange(markers);
            ApplyEventFilter();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load timeline markers for {Path}", _recording.FilePath);
        }
    }

    [RelayCommand]
    private void SetEventFilter(string? filter) =>
        EventFilter = filter switch
        {
            "motion" => PlayerEventFilter.Motion,
            "detection" => PlayerEventFilter.Detection,
            _ => PlayerEventFilter.All,
        };

    partial void OnEventFilterChanged(PlayerEventFilter value) => ApplyEventFilter();

    private void ApplyEventFilter()
    {
        EventList.Clear();
        foreach (var m in _allEventMarkers)
        {
            var include = EventFilter switch
            {
                PlayerEventFilter.Motion => m.Kind == TimelineMarkerKind.Motion,
                PlayerEventFilter.Detection => m.Kind == TimelineMarkerKind.Detection,
                _ => true,
            };
            if (include) EventList.Add(m);
        }
        OnPropertyChanged(nameof(HasEvents));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stateSub?.Dispose();
        _positionSub?.Dispose();
        var session = _playback;
        _playback = null;
        VideoSession = null;
        if (session is not null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing playback session"); }
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", t.Minutes, t.Seconds);
}
