using System;
using System.Collections.Generic;
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
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Timeline;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

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
        ILogger<RecordingPlayerPageViewModel> logger)
    {
        _recording = recording;
        CameraName = cameraName;
        _engine = engine;
        _probe = probe;
        _events = events;
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
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load timeline markers for {Path}", _recording.FilePath);
        }
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
