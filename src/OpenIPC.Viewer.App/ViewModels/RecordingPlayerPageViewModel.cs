using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Timeline;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public enum PlayerEventFilter { All, Motion, Detection }

// Phase 16 — playback of a recorded segment. Hosts an IPlaybackSession (file
// decode, transport, seek) and exposes it as IVideoSession so the existing
// RtspVideoView renders frames unchanged. Around it: the camera/time header
// with neighbour recordings, the event list, speed, clip export and frame grab.
public sealed partial class RecordingPlayerPageViewModel : ViewModelBase, IAsyncDisposable
{
    // Skip buttons jump 10 s, arrow keys 5 s.
    private static readonly TimeSpan SkipStep = TimeSpan.FromSeconds(10);
    // A next recording starting within this gap of our end plays on by itself.
    private static readonly TimeSpan AutoContinueGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultClipLength = TimeSpan.FromSeconds(10);
    private static readonly double[] RateSteps = { 0.5, 1, 2, 4, 8 };

    private readonly Recording _recording;
    private readonly IPlaybackEngine _engine;
    private readonly IMediaProbe _probe;
    private readonly IEventRepository _events;
    private readonly IRecordingRepository _recordings;
    private readonly ISnapshotService _snapshots;
    private readonly IClipExporter _exporter;
    private readonly AudioMonitor _audio;
    private readonly UserSettingsService _userSettings;
    private readonly IDialogService _dialogs;
    private readonly OpenIPC.Viewer.Core.Platform.IShareService _share;
    private readonly ILogger<RecordingPlayerPageViewModel> _logger;

    private IPlaybackSession? _playback;
    private IDisposable? _stateSub;
    private IDisposable? _positionSub;
    private Recording? _previous;
    private Recording? _next;
    private CancellationTokenSource? _noticeCts;
    private bool _audioAttached;
    private bool _userPaused;
    private bool _continued;
    private bool _activating;
    private bool _disposed;

    public RecordingPlayerPageViewModel(
        Recording recording,
        string cameraName,
        IPlaybackEngine engine,
        IMediaProbe probe,
        IEventRepository events,
        IRecordingRepository recordings,
        ISnapshotService snapshots,
        IClipExporter exporter,
        AudioMonitor audio,
        UserSettingsService userSettings,
        IDialogService dialogs,
        OpenIPC.Viewer.Core.Platform.IShareService share,
        ILogger<RecordingPlayerPageViewModel> logger)
    {
        _recording = recording;
        CameraName = cameraName;
        _engine = engine;
        _probe = probe;
        _events = events;
        _recordings = recordings;
        _snapshots = snapshots;
        _exporter = exporter;
        _audio = audio;
        _userSettings = userSettings;
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

        foreach (var r in RateSteps)
            RateOptions.Add(new PlaybackRateOption(r, r == 1));
    }

    // Initial seek applied once playback starts (e.g. opened from an event).
    public TimeSpan? StartAt { get; set; }

    public string CameraName { get; }
    public string FileName => Path.GetFileName(_recording.FilePath);

    // Opening the OS file manager only makes sense on desktop heads.
    public bool CanShowInFolder => !OverlayDialogPresenter.IsMobile;

    // "20 June 2026 · 23:12:57 – 23:13:01 · 0:04 · 574 KB".
    public string Subtitle
    {
        get
        {
            var start = TimelineStart.ToLocalTime();
            var end = TimelineEnd.ToLocalTime();
            var parts = new List<string>
            {
                start.ToString("d MMMM yyyy", UiCulture),
                $"{start:HH:mm:ss} – {end:HH:mm:ss}",
                FormatShort(TimelineEnd - TimelineStart),
            };
            if (_recording.SizeBytes > 0)
                parts.Add(RecordingRowViewModel.FormatSize(_recording.SizeBytes));
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    private SessionState _state = SessionState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyCanExecuteChangedFor(nameof(SaveFrameCommand))]
    private IVideoSession? _videoSession;

    [ObservableProperty] private string? _errorMessage;

    // Chrome-free fullscreen; owned by MainWindowViewModel (see
    // SetPlayerFullscreenMessage), pushed back here.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEventSidebar))]
    private bool _isFullscreen;

    // Sound: the file must carry a decodable audio track and a sink must exist.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanListen))]
    private bool _hasAudio;

    public bool CanListen => HasAudio && _audio.IsAvailable;

    // Transient confirmation over the video ("Frame saved"); clears itself.
    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    [NotifyPropertyChangedFor(nameof(WallClockLabel))]
    private TimeSpan _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private TimeSpan _duration;

    // Timeline (16.4): absolute-time track range, segments, event markers, and
    // the playhead as an absolute time derived from Position.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(WallClockLabel))]
    private DateTime _timelineStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private DateTime _timelineEnd;

    [ObservableProperty] private IReadOnlyList<TimelineSegment>? _segments;
    [ObservableProperty] private IReadOnlyList<TimelineMarker>? _markers;
    [ObservableProperty] private DateTime? _playheadTime;

    // Neighbour recordings of the same camera.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private string? _previousTip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string? _nextTip;

    // Speed (0.5×–8×), applied to the session and carried to neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLabel))]
    private double _rate = 1;

    public ObservableCollection<PlaybackRateOption> RateOptions { get; } = new();
    public string RateLabel => FormatRate(Rate);

    // Event list (16.6): motion/detection inside this recording, clickable to
    // seek, filtered by type. Hidden entirely when the recording has none.
    private readonly List<PlayerEventRow> _allEvents = new();
    public ObservableCollection<PlayerEventRow> EventList { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterMotion))]
    [NotifyPropertyChangedFor(nameof(IsFilterDetection))]
    private PlayerEventFilter _eventFilter = PlayerEventFilter.All;

    public bool IsFilterAll => EventFilter == PlayerEventFilter.All;
    public bool IsFilterMotion => EventFilter == PlayerEventFilter.Motion;
    public bool IsFilterDetection => EventFilter == PlayerEventFilter.Detection;
    public bool HasAnyEvents => _allEvents.Count > 0;
    public bool ShowEventSidebar => HasAnyEvents && !IsFullscreen;
    public bool HasEvents => EventList.Count > 0;
    public string FilterAllLabel => $"{Localizer.Instance["Player.Filter.All"]} {_allEvents.Count}";
    public string FilterMotionLabel => $"{Localizer.Instance["Player.Filter.Motion"]} {_allEvents.Count(e => e.IsMotion)}";
    public string FilterDetectionLabel => $"{Localizer.Instance["Player.Filter.Detection"]} {_allEvents.Count(e => !e.IsMotion)}";
    public string EventsTitle => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Player.EventsFormat"], _allEvents.Count);

    // Clip mode (16.5): in/out marks (absolute UTC) on the timeline + export.
    [ObservableProperty] private bool _isClipMode;

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

    public bool HasSelection =>
        SelectionStart is { } s && SelectionEnd is { } e && Math.Abs((e - s).TotalSeconds) >= 0.5;

    // "23:13:00 – 23:13:05 · 5 s".
    public string SelectionLabel
    {
        get
        {
            if (!HasSelection) return Localizer.Instance["Player.Clip.Hint"];
            var s = Min(SelectionStart!.Value, SelectionEnd!.Value);
            var e = Max(SelectionStart!.Value, SelectionEnd!.Value);
            return $"{s.ToLocalTime():HH:mm:ss} – {e.ToLocalTime():HH:mm:ss} · " +
                   string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Player.Clip.SecondsFormat"],
                       Math.Round((e - s).TotalSeconds));
        }
    }

    public bool IsConnecting => VideoSession is not null && State == SessionState.Connecting;
    public bool IsFailed => State == SessionState.Failed;
    public bool IsPlaying => State == SessionState.Playing;

    // Big wall-clock time of the current frame + "00:01 / 00:04" under it.
    public string WallClockLabel => (TimelineStart + Position).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string ProgressLabel => $"{Format(Position)} / {Format(Duration)}";

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
                HasAudio = info.HasAudio;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pre-probe failed for {Path}", _recording.FilePath);
            }

            await LoadMarkersAsync(ct).ConfigureAwait(true);
            await LoadNeighboursAsync(ct).ConfigureAwait(true);
            if (_disposed) return;

            var session = _engine.OpenFile(PlaybackOptions.Default(_recording.FilePath));
            session.Rate = Rate;
            _stateSub = session.StateChanged.Subscribe(s => Dispatcher.UIThread.Post(() =>
            {
                State = s;
                if (s == SessionState.Failed)
                    ErrorMessage = session.LastError;
                if (s == SessionState.Paused)
                    TryAutoContinue();
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
            if (CanListen)
            {
                // Same persisted mute/volume as the live page; the player takes
                // over as the one audio source while it's open.
                _audio.Muted = _userSettings.Current.AudioMuted;
                _audio.Volume = (float)_userSettings.Current.AudioVolume;
                _audio.Changed += OnAudioChanged;
                session.SetAudioEnabled(true);
                _audio.Detach();
                _audio.Attach(session, _recording.CameraId);
                _audioAttached = true;
            }
            await session.StartAsync(ct).ConfigureAwait(true);
            if (StartAt is { } start && start > TimeSpan.FromSeconds(1))
                await SeekToAsync(start).ConfigureAwait(true);
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

    // ── Transport ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void PlayPause()
    {
        if (_playback is null) return;
        if (_playback.IsPaused)
        {
            _userPaused = false;
            // Play at the end of the file starts over, like any player.
            if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromSeconds(0.5))
                _ = SeekToAsync(TimeSpan.Zero);
            _playback.Play();
        }
        else
        {
            _userPaused = true;
            _playback.Pause();
        }
    }

    // Single-frame step (pauses first). Backward costs a GOP decode.
    [RelayCommand]
    private void StepBack() => StepFrame(false);

    [RelayCommand]
    private void StepForward() => StepFrame(true);

    public void StepFrame(bool forward)
    {
        if (_playback is null) return;
        _userPaused = true;
        _playback.StepFrame(forward);
    }

    [RelayCommand]
    private void ToggleFullscreen() =>
        WeakReferenceMessenger.Default.Send(new SetPlayerFullscreenMessage(!IsFullscreen));

    // ── Sound ─────────────────────────────────────────────────────────────

    public bool IsMuted
    {
        get => _audio.Muted;
        set
        {
            if (_audio.Muted == value) return;
            _audio.Muted = value; // raises Changed → OnAudioChanged re-raises + persists
        }
    }

    public double Volume
    {
        get => _audio.Volume;
        set
        {
            if (Math.Abs(_audio.Volume - value) < 0.0001) return;
            _audio.Volume = (float)value;
        }
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (CanListen) IsMuted = !IsMuted;
    }

    private void OnAudioChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(Volume));
        });
        var cur = _userSettings.Current;
        if (cur.AudioMuted == _audio.Muted && Math.Abs(cur.AudioVolume - _audio.Volume) < 0.0001)
            return;
        _ = _userSettings.UpdateAsync(cur with { AudioMuted = _audio.Muted, AudioVolume = _audio.Volume });
    }

    [RelayCommand]
    private Task SkipBack() => SeekRelativeAsync(-SkipStep);

    [RelayCommand]
    private Task SkipForward() => SeekRelativeAsync(SkipStep);

    public Task SeekRelativeAsync(TimeSpan delta)
    {
        var target = Position + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && target > Duration) target = Duration;
        return SeekToAsync(target);
    }

    public Task SeekToStartAsync() => SeekToAsync(TimeSpan.Zero);

    public Task SeekToEndAsync() =>
        Duration > TimeSpan.Zero ? SeekToAsync(Duration) : Task.CompletedTask;

    // Invoked by the timeline and the event list with an absolute UTC time.
    [RelayCommand]
    private Task SeekToTime(DateTime target) => SeekToAsync(target - TimelineStart);

    [RelayCommand]
    private void SetRate(string? value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            Rate = r;
    }

    partial void OnRateChanged(double value)
    {
        foreach (var o in RateOptions) o.IsActive = o.Value == value;
        if (_playback is not null) _playback.Rate = value;
    }

    private async Task SeekToAsync(TimeSpan position)
    {
        if (_playback is null) return;
        Position = position; // optimistic — keeps the playhead responsive mid-drag
        try { await _playback.SeekAsync(position, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Seek to {Pos} failed", position); }
    }

    partial void OnPositionChanged(TimeSpan value) => PlayheadTime = TimelineStart + value;

    partial void OnDurationChanged(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return;
        TimelineEnd = TimelineStart + value;
        Segments = new[] { new TimelineSegment(TimelineStart, TimelineEnd) };
    }

    // ── Neighbours ────────────────────────────────────────────────────────

    private async Task LoadNeighboursAsync(CancellationToken ct)
    {
        try
        {
            var all = (await _recordings.ListAsync(_recording.CameraId, ct).ConfigureAwait(true))
                .OrderBy(r => r.StartedAt)
                .ToList();
            var i = all.FindIndex(r => r.Id == _recording.Id);
            if (i < 0) return;
            _previous = i > 0 ? all[i - 1] : null;
            _next = i < all.Count - 1 ? all[i + 1] : null;
            PreviousTip = _previous is null ? null : NeighbourTip("Player.PreviousTip", _previous);
            NextTip = _next is null ? null : NeighbourTip("Player.NextTip", _next);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load neighbour recordings for {Path}", _recording.FilePath);
        }
    }

    private static string NeighbourTip(string key, Recording r)
    {
        var start = r.StartedAt.ToLocalTime();
        var when = start.Date == DateTime.Now.Date
            ? start.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : start.ToString("d MMM, HH:mm:ss", UiCulture);
        return $"{Localizer.Instance[key]} · {when}";
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous() => OpenNeighbour(_previous);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next() => OpenNeighbour(_next);

    private bool HasPrevious() => PreviousTip is not null;
    private bool HasNext() => NextTip is not null;

    private void OpenNeighbour(Recording? r)
    {
        if (r is null) return;
        WeakReferenceMessenger.Default.Send(new OpenRecordingMessage(r, CameraName, Rate: Rate));
    }

    // The file ended by itself (not a user pause) and the next recording picks
    // up right where this one stops — continuous recording split into files.
    private void TryAutoContinue()
    {
        if (_continued || _userPaused || _next is null || Duration <= TimeSpan.Zero) return;
        if (Position < Duration - TimeSpan.FromSeconds(0.5)) return;
        if (_next.StartedAt - (TimelineStart + Duration) > AutoContinueGap) return;
        _continued = true;
        OpenNeighbour(_next);
    }

    // ── Frame, file actions ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSaveFrame))]
    private async Task SaveFrameAsync()
    {
        if (_playback is null) return;
        try
        {
            var jpeg = await _playback.SnapshotAsync(SnapshotFormat.Jpeg, CancellationToken.None).ConfigureAwait(true);
            if (jpeg.Length == 0) return;
            await _snapshots.SaveFrameAsync(_recording.CameraId, jpeg, TimelineStart + Position, CancellationToken.None)
                .ConfigureAwait(true);
            ShowNotice(Localizer.Instance["Player.FrameSaved"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving a frame from {Path} failed", _recording.FilePath);
            ShowNotice(ex.Message);
        }
    }

    private bool CanSaveFrame() => VideoSession is not null;

    [RelayCommand]
    private async Task ShowInFolderAsync()
    {
        var dir = Path.GetDirectoryName(_recording.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (!await _dialogs.OpenUrlAsync(new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri).ConfigureAwait(true))
            _logger.LogWarning("Could not open folder {Dir}", dir);
    }

    [RelayCommand]
    private async Task CopyFileAsync()
    {
        try { await _dialogs.CopyFileToClipboardAsync(_recording.FilePath).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Copy to clipboard failed for {Path}", _recording.FilePath); }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Recordings.Dialog.DeleteTitle"],
            message: string.Format(Localizer.Instance["Recordings.Dialog.DeleteMessageFormat"], FileName),
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;

        // The decoder holds the file open; release it before deleting.
        await DisposeAsync().ConfigureAwait(true);
        try
        {
            try { if (File.Exists(_recording.FilePath)) File.Delete(_recording.FilePath); }
            catch (IOException ex) { _logger.LogWarning(ex, "File still locked (recording in progress?)"); }
            await _recordings.RemoveAsync(_recording.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete recording {Id}", _recording.Id);
        }
        WeakReferenceMessenger.Default.Send(new GoBackToRecordingsMessage(Reload: true));
    }

    [RelayCommand]
    private void Back() => WeakReferenceMessenger.Default.Send(new GoBackToRecordingsMessage());

    private async void ShowNotice(string text)
    {
        _noticeCts?.Cancel();
        var cts = _noticeCts = new CancellationTokenSource();
        Notice = text;
        try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        Notice = null;
    }

    // ── Clip mode ─────────────────────────────────────────────────────────

    // Entering clip mode pre-selects the next 10 s so there's something to
    // adjust; leaving it drops the selection.
    partial void OnIsClipModeChanged(bool value)
    {
        if (!value)
        {
            SelectionStart = null;
            SelectionEnd = null;
            return;
        }
        if (HasSelection || PlayheadTime is not { } t) return;
        var start = t;
        var end = start + DefaultClipLength;
        if (end > TimelineEnd)
        {
            end = TimelineEnd;
            start = end - DefaultClipLength < TimelineStart ? TimelineStart : end - DefaultClipLength;
        }
        SelectionStart = start;
        SelectionEnd = end;
    }

    [RelayCommand]
    private void CancelClip() => IsClipMode = false;

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

    // A drag on the timeline selects a range — that means clip mode.
    partial void OnSelectionStartChanged(DateTime? value) => EnterClipModeOnSelection();
    partial void OnSelectionEndChanged(DateTime? value) => EnterClipModeOnSelection();

    private void EnterClipModeOnSelection()
    {
        if (HasSelection && !IsClipMode) IsClipMode = true;
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
        try
        {
            var request = new ClipExportRequest(_recording.FilePath, dest!, startOff, endOff, PreciseExport);
            var progress = new Progress<double>(p => ExportFraction = p);
            await _exporter.ExportAsync(request, progress, CancellationToken.None).ConfigureAwait(true);
            CancelClip();
            ShowNotice(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Player.Clip.SavedFormat"], Path.GetFileName(dest)));

            // On mobile the picked file is invisible to the user, so hand the
            // clip to the native share sheet (Phase 16.5 "в галерею/share").
            if (_share.SupportsSystemShare)
                await _share.ShareFileAsync(dest!, "video/mp4", CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip export failed for {Path}", _recording.FilePath);
            ShowNotice(ex.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanExport() => HasSelection && !IsExporting;

    // ── Events ────────────────────────────────────────────────────────────

    private async Task LoadMarkersAsync(CancellationToken ct)
    {
        try
        {
            var list = await _events
                .ListAsync(_recording.CameraId, kind: null, since: TimelineStart.AddSeconds(-1), limit: 2000, ct)
                .ConfigureAwait(true);

            var markers = new List<TimelineMarker>();
            _allEvents.Clear();
            foreach (var ev in list.OrderBy(e => e.OccurredAt))
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
                var row = new PlayerEventRow(ev.OccurredAt, ev.OccurredAt - TimelineStart, kind == TimelineMarkerKind.Motion, ev.Summary);
                _allEvents.Add(row);
                markers.Add(new TimelineMarker(ev.OccurredAt, kind, $"{row.TimeLabel} · {row.Title}"));
            }
            Markers = markers;
            ApplyEventFilter();
            OnPropertyChanged(nameof(HasAnyEvents));
            OnPropertyChanged(nameof(ShowEventSidebar));
            OnPropertyChanged(nameof(FilterAllLabel));
            OnPropertyChanged(nameof(FilterMotionLabel));
            OnPropertyChanged(nameof(FilterDetectionLabel));
            OnPropertyChanged(nameof(EventsTitle));
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
        foreach (var e in _allEvents)
        {
            var include = EventFilter switch
            {
                PlayerEventFilter.Motion => e.IsMotion,
                PlayerEventFilter.Detection => !e.IsMotion,
                _ => true,
            };
            if (include) EventList.Add(e);
        }
        OnPropertyChanged(nameof(HasEvents));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _noticeCts?.Cancel();
        _audio.Changed -= OnAudioChanged;
        if (_audioAttached) _audio.Detach(_recording.CameraId);
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

    private static CultureInfo UiCulture => Localizer.Instance.Active == LangCode.Russian
        ? CultureInfo.GetCultureInfo("ru-RU")
        : CultureInfo.GetCultureInfo("en-US");

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    internal static string FormatRate(double r) =>
        r.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    // "0:04", "1:02:10".
    private static string FormatShort(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{t.Minutes}:{t.Seconds:D2}";

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", t.Minutes, t.Seconds);
}

// One speed step in the transport's segment.
public sealed partial class PlaybackRateOption : ObservableObject
{
    public PlaybackRateOption(double value, bool isActive)
    {
        Value = value;
        _isActive = isActive;
    }

    public double Value { get; }
    public string Label => RecordingPlayerPageViewModel.FormatRate(Value);
    public string Parameter => Value.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty] private bool _isActive;
}

// A motion/detection event inside the playing recording.
public sealed class PlayerEventRow
{
    public PlayerEventRow(DateTime time, TimeSpan offset, bool isMotion, string? summary)
    {
        Time = time;
        IsMotion = isMotion;
        TimeLabel = time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        OffsetLabel = RecordingPlayerPageViewModel.Format(offset < TimeSpan.Zero ? TimeSpan.Zero : offset);
        Chips = isMotion
            ? Array.Empty<DetectionClassChip>()
            : DetectionClasses.Parse(summary)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new DetectionClassChip(kv.Key, kv.Value > 1 ? kv.Value : 0, DetectionClasses.IconKey(kv.Key)))
                .ToList();
        Title = isMotion ? Localizer.Instance["Events.Kind.Motion"] : Localizer.Instance["Events.Kind.Detection"];
    }

    public DateTime Time { get; }
    public bool IsMotion { get; }
    public string TimeLabel { get; }
    public string OffsetLabel { get; }
    public string Title { get; }
    public IReadOnlyList<DetectionClassChip> Chips { get; }
    public bool HasChips => Chips.Count > 0;
    public string IconKey => IsMotion ? "IconWalk" : "IconScanSearch";
}
