using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Status;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraTileViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly UserSettingsService _userSettings;
    private readonly ISnapshotService _snapshots;
    private readonly IAnalyticsEngine _analytics;
    private readonly AnalyticsBootstrap _analyticsBootstrap;
    private readonly AudioMonitor _audio;
    private readonly IReachabilityProbe _reachability;
    private readonly CameraStatusRegistry _statusRegistry;
    private readonly ISnapshotFrameSource _frameSource;
    private readonly ILogger<CameraTileViewModel> _logger;

    // "Stills" mode: instead of a live RTSP session this tile shows a periodic
    // HTTP snapshot (StillFrame), refreshed every _stillsIntervalSeconds. Set at
    // construction from the grid setting; the tile is rebuilt when it changes.
    private readonly bool _stillsMode;
    private readonly int _stillsIntervalSeconds;
    private CancellationTokenSource? _stillsCts;

    [ObservableProperty] private Bitmap? _stillFrame;

    // Drives the template: Image (stills) vs RtspVideoView (live). A camera with
    // no cheap HTTP still (non-Majestic) stays on live RTSP even when the grid
    // stills toggle is on, rather than blanking to an empty tile.
    public bool StillsMode => _stillsMode && _frameSource.Supports(Camera);

    // On a stream fault we TCP-probe the camera so the status policy can tell a
    // wedged-but-alive camera (Attention) from a truly unreachable one (Offline).
    // Short timeout: a screen of dead tiles must settle quickly.
    private static readonly TimeSpan ReachabilityProbeTimeout = TimeSpan.FromSeconds(2);

    // True while this tile is the live audio source. Tracked locally so we can
    // turn the audio decoder back off when another tile displaces us (one-source
    // policy lives in AudioMonitor; this just mirrors it for our own session).
    private bool _listening;

    // Auto SD/HD (Phase 12.2): substream in the grid, mainstream when a single
    // tile fills the view. Mutated by SetQualityAsync (a session swap), set
    // pre-activation by SetInitialQuality so a tile opening straight into 1×1
    // doesn't briefly start on the substream.
    private StreamQuality _quality = StreamQuality.Sub;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;
    private IDisposable? _resultsSub;
    private bool _started;
    private bool _disposed;
    private bool _suspended;

    public Camera Camera { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    private IVideoSession? _session;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ErrorDetail))]
    [NotifyPropertyChangedFor(nameof(ConnectingLabel))]
    [NotifyPropertyChangedFor(nameof(Status))]
    private SessionState _state = SessionState.Idle;

    // Last TCP probe of the camera's stream port (null = not probed). Only set
    // while Failed, to split Attention from Offline; reset on recovery.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool? _portReachable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsLabel))]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    [NotifyPropertyChangedFor(nameof(SourceAspect))]
    private SessionTelemetry? _telemetry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorDetail))]
    private string? _errorMessage;

    // Latest detections for this tile (Phase 15.5), normalized 0..1 boxes drawn
    // by the DetectionOverlay control. Updated off the inference worker thread.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectionCounter))]
    [NotifyPropertyChangedFor(nameof(HasDetections))]
    private IReadOnlyList<Detection> _detections = Array.Empty<Detection>();

    public bool AnalyticsEnabled => Camera.AnalyticsOrDefault.Enabled;

    // Bottom-center counter badge, e.g. "person ×2, car ×1".
    public string DetectionCounter => AnalyticsMotionEventSource.Summarize(Detections);

    public bool HasDetections => AnalyticsEnabled && Detections.Count > 0;

    public string Name => Camera.Name;

    // Unified health verdict (grid + sidebar share this). A faulted stream that
    // still answers on its port reads Attention, not Offline — see CameraStatusPolicy.
    public CameraStatus Status =>
        CameraStatusPolicy.Resolve(new CameraStatusInputs(State, PortReachable)).Status;

    public string StateLabel => Status == CameraStatus.Attention
        ? "ATTENTION"
        : State switch
        {
            SessionState.Playing => "LIVE",
            SessionState.Connecting => "CONNECTING…",
            SessionState.Reconnecting => "RECONNECTING…",
            SessionState.Paused => "PAUSED",
            SessionState.Failed => "OFFLINE",
            _ => "IDLE",
        };

    // Mid-connect dim overlay (spinner). Gated on Session so the pre-activate
    // window doesn't flash a spinner out of nowhere.
    public bool IsConnecting =>
        Session is not null && State is SessionState.Connecting or SessionState.Reconnecting;

    // Drives the interactive error cell (icon + reason + Retry/Close).
    public bool IsFailed => State == SessionState.Failed;

    // Text under the connecting spinner — distinguishes a fresh connect from a
    // reconnect attempt.
    public string ConnectingLabel => State == SessionState.Reconnecting
        ? Localizer.Instance["Stream.Reconnecting"]
        : Localizer.Instance["Stream.Connecting"];

    // Reason line for the error cell: server-supplied error (or a generic
    // fallback) followed by the camera host, e.g. "Stream timeout · 10.0.0.42".
    public string ErrorDetail
    {
        get
        {
            var reason = string.IsNullOrWhiteSpace(ErrorMessage)
                ? Localizer.Instance["Stream.Unavailable"]
                : ErrorMessage;
            return $"{reason} · {Camera.Host}";
        }
    }

    public bool HasStats => Telemetry is not null;

    // Source frame aspect ratio (width/height), so the DetectionOverlay can map
    // normalized boxes into the letterboxed video rect instead of the full tile
    // bounds. 0 until the first telemetry arrives → overlay falls back to bounds.
    public double SourceAspect =>
        Telemetry is { Width: > 0, Height: > 0 } t ? (double)t.Width / t.Height : 0;

    // Bottom-right stats badge: codec • resolution • fps • bitrate.
    public string? StatsLabel
    {
        get
        {
            var t = Telemetry;
            if (t is null) return null;
            var codec = string.IsNullOrEmpty(t.Codec) ? "—" : t.Codec;
            return $"{codec} • {t.Width}×{t.Height} • {t.Fps:F0} fps • {FormatBitrate(t.BitrateKbps)}";
        }
    }

    private static string FormatBitrate(double kbps) => kbps >= 1000
        ? $"{kbps / 1000:F1} Mb/s"
        : $"{kbps:F0} kb/s";

    // --- Audio listen (Phase 17): per-tile speaker toggle --------------------
    // Shown when a native sink exists and we don't positively know the camera
    // has no mic. Same lenient ONVIF gate as the talk button: only hide when a
    // probed camera reports no audio-in (non-ONVIF/unprobed cameras still show).
    public bool AudioAvailable =>
        _audio.IsAvailable && !(Camera.OnvifEnabled && !Camera.HasAudioIn);
    public bool IsListening => _audio.AttachedCamera == Camera.Id;

    [RelayCommand]
    private void ToggleListen()
    {
        if (!_audio.IsAvailable || Session is null) return;
        if (_audio.AttachedCamera == Camera.Id)
        {
            _audio.Detach(Camera.Id);
            Session.SetAudioEnabled(false);
            _listening = false;
        }
        else
        {
            Session.SetAudioEnabled(true);
            _audio.Attach(Session, Camera.Id); // silences the previously-listening tile
            _audio.Muted = false;              // listening from a tile implies "play it"
            _listening = true;
        }
        OnPropertyChanged(nameof(IsListening));
    }

    private void OnAudioChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Displaced by another tile: stop burning CPU on our audio decoder.
            if (_listening && _audio.AttachedCamera != Camera.Id)
            {
                _listening = false;
                Session?.SetAudioEnabled(false);
            }
            OnPropertyChanged(nameof(IsListening));
        });
    }

    public CameraTileViewModel(
        Camera camera,
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        UserSettingsService userSettings,
        ISnapshotService snapshots,
        IAnalyticsEngine analytics,
        AnalyticsBootstrap analyticsBootstrap,
        AudioMonitor audio,
        IReachabilityProbe reachability,
        CameraStatusRegistry statusRegistry,
        ISnapshotFrameSource frameSource,
        bool stillsMode,
        int stillsIntervalSeconds,
        ILogger<CameraTileViewModel> logger)
    {
        Camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _userSettings = userSettings;
        _snapshots = snapshots;
        _analytics = analytics;
        _analyticsBootstrap = analyticsBootstrap;
        _audio = audio;
        _reachability = reachability;
        _statusRegistry = statusRegistry;
        _frameSource = frameSource;
        _stillsMode = stillsMode;
        _stillsIntervalSeconds = stillsIntervalSeconds;
        _logger = logger;

        _coordinator.Invalidated += OnCoordinatorInvalidated;
        _audio.Changed += OnAudioChanged;

        // Always listen for results for this camera; they only arrive while the
        // tile is attached + the engine is ready, so this is cheap otherwise.
        _resultsSub = _analytics.Results
            .Where(r => r.CameraId == Camera.Id)
            .Subscribe(r => Dispatcher.UIThread.Post(() => Detections = r.Detections));
    }

    // Set the stream quality before the first ActivateAsync. No-op once started
    // — use SetQualityAsync to switch a live tile.
    public void SetInitialQuality(StreamQuality quality)
    {
        if (_started) return;
        _quality = quality;
    }

    // Auto SD/HD swap on a live tile: tear the current-quality session down and
    // re-acquire on the other stream. Mirrors RetryAsync. The coordinator keys
    // sessions by (camera, quality), so we must release with the OLD quality
    // before flipping the field.
    public async Task SetQualityAsync(StreamQuality quality, CancellationToken ct)
    {
        if (_disposed || quality == _quality) return;
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        DetachAnalytics();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(Camera.Id, _quality).ConfigureAwait(true);
        }
        _quality = quality;
        State = SessionState.Idle;
        _started = false;
        await ActivateAsync(ct).ConfigureAwait(true);
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;

        // Stills mode: no decoder, just poll an HTTP snapshot on an interval.
        // Only for cameras that expose one — others fall through to live RTSP.
        if (StillsMode)
        {
            StartStillsLoop();
            return;
        }

        var streamUri = _quality == StreamQuality.Main
            ? Camera.RtspMainUri
            : (Camera.RtspSubUri ?? Camera.RtspMainUri);
        if (_quality == StreamQuality.Sub && Camera.RtspSubUri is null)
            _logger.LogWarning("Camera {Name} has no substream URL, using mainstream in grid", Camera.Name);

        var creds = await _directory.GetCredentialsAsync(Camera.Id, ct).ConfigureAwait(true);
        // Respect the user-selected RtspTransport from Settings — previously
        // hard-coded TCP, so toggling to UDP in Settings was a no-op for grid
        // tiles. Bridged invalidation re-runs activation on change.
        var options = VideoSessionOptions.Default(streamUri, creds)
            with { Transport = ParseTransport(_userSettings.Current.RtspTransport) };

        try
        {
            var session = _coordinator.Acquire(Camera.Id, _quality, options);
            _stateSub = session.StateChanged.Subscribe(s =>
            {
                State = s;
                if (s == SessionState.Failed)
                {
                    ErrorMessage = session.LastError;
                    _ = ProbeReachabilityAsync(); // Attention vs Offline
                }
                else
                {
                    PortReachable = null; // stale once we're no longer failed
                }
            });
            _telemetrySub = session.Telemetry.Subscribe(t => Telemetry = t);
            Session = session;

            if (session.State == SessionState.Idle)
                await session.StartAsync(ct).ConfigureAwait(true);

            // Rebind audio to the fresh session if we were the listener before a
            // quality swap / reconnect (the monitor still points at our camera).
            if (_audio.AttachedCamera == Camera.Id)
            {
                session.SetAudioEnabled(true);
                _audio.Attach(session, Camera.Id);
                _listening = true;
            }

            AttachAnalytics(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tile session for camera {Id}", Camera.Id);
            State = SessionState.Failed;
            _ = ProbeReachabilityAsync(); // Attention vs Offline
        }
    }

    // Tap this session's frames for object detection (Phase 15.3) when the
    // camera has analytics on. Kicks the engine bootstrap (model load) in the
    // background; frames are dropped until it's ready. isActive gates analytics
    // off for a Smart-Paused / non-playing tile.
    private void AttachAnalytics(IVideoSession session)
    {
        if (!Camera.AnalyticsOrDefault.Enabled) return;
        _ = _analyticsBootstrap.EnsureStartedAsync();
        _analytics.Attach(
            Camera.Id,
            session.Frames,
            () => Camera.AnalyticsOrDefault,
            () => !_suspended && State == SessionState.Playing);
    }

    private void DetachAnalytics()
    {
        _analytics.Detach(Camera.Id);
        Detections = Array.Empty<Detection>();
    }

    // Stream faulted — TCP-probe the port the player dials so the status policy
    // can distinguish a wedged-but-alive camera (Attention) from one that's gone
    // (Offline). Fire-and-forget; a late result is dropped if we've recovered.
    private async Task ProbeReachabilityAsync()
    {
        var reachable = await _reachability
            .ProbeAsync(Camera, ReachabilityProbeTimeout, CancellationToken.None, _logger)
            .ConfigureAwait(true);
        _statusRegistry.ReportReachability(Camera.Id, reachable);
        if (State == SessionState.Failed)
            PortReachable = reachable;
    }

    // Mirror every state move into the shared registry so the library + Health
    // Center reflect this tile's live session (incl. Attention), not just their
    // own probe. Covers manual Idle resets (quality swap / retry) too.
    partial void OnStateChanged(SessionState value) =>
        _statusRegistry?.ReportSession(Camera.Id, value);

    // Coordinator dropped every cached session (e.g. RtspTransport flipped in
    // Settings). Our current Session ref is now disposed — drop subscriptions,
    // reset state, and re-Acquire on the UI thread so observable updates land
    // on the bindable thread.
    private void OnCoordinatorInvalidated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                _stateSub?.Dispose();
                _telemetrySub?.Dispose();
                DetachAnalytics();
                Session = null;
                State = SessionState.Idle;
                _started = false;
                await ActivateAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tile reactivation after invalidation failed");
            }
        });
    }

    private static RtspTransport ParseTransport(string? s) => s?.ToLowerInvariant() switch
    {
        "udp" => RtspTransport.Udp,
        _ => RtspTransport.Tcp,
    };

    [RelayCommand]
    private void OpenSingle() =>
        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(Camera.Id));

    // Brief "captured" confirmation flashed over the tile after a snapshot. The
    // tile streams the substream, but SnapshotService still grabs an HD source
    // (Majestic HTTP or a brief mainstream open) — never the SD frame on screen.
    [ObservableProperty] private bool _snapshotFlash;

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        try
        {
            await _snapshots.CaptureAsync(Camera, Session, _quality, CancellationToken.None).ConfigureAwait(true);
            SnapshotFlash = true;
            await Task.Delay(900).ConfigureAwait(true);
            SnapshotFlash = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tile snapshot failed for {Camera}", Camera.Name);
        }
    }

    // Retry button on the error cell. Mirrors OnCoordinatorInvalidated: drop the
    // dead session, reset state, and re-run the full Activate path.
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_disposed) return;
        ErrorMessage = null;
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        DetachAnalytics();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(Camera.Id, _quality).ConfigureAwait(true);
        }
        State = SessionState.Idle;
        _started = false;
        await ActivateAsync(CancellationToken.None).ConfigureAwait(true);
    }

    // Close button on the error cell — drops this tile from the grid for the
    // session (GridPageViewModel handles the message).
    [RelayCommand]
    private void Close() =>
        WeakReferenceMessenger.Default.Send(new CloseTileMessage(Camera.Id));

    // Smart Pause (Phase 12.1): suspend/resume decode without dropping the
    // session, so the last frame stays frozen for an instant resume. Analytics
    // pauses with the tile (12.1 ↔ 15.3) — isActive reads _suspended.
    public void Pause()
    {
        _suspended = true;
        Session?.PauseDecode();
        Detections = Array.Empty<Detection>();
    }

    public void Resume()
    {
        _suspended = false;
        Session?.Resume();
    }

    // --- Stills mode --------------------------------------------------------

    private void StartStillsLoop()
    {
        _stillsCts = new CancellationTokenSource();
        _ = RunStillsAsync(_stillsCts.Token);
    }

    private async Task RunStillsAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _stillsIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytes = await _frameSource.GrabAsync(Camera, ct).ConfigureAwait(false);
                var bmp = bytes is { Length: > 0 } ? DecodeJpeg(bytes) : null;
                if (bmp is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var old = StillFrame;
                        StillFrame = bmp;
                        old?.Dispose();
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stills grab failed for {Name}", Camera.Name);
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static Bitmap? DecodeJpeg(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // a truncated / non-image body — keep the previous frame
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _stillsCts?.Cancel();
        _stillsCts?.Dispose();
        _stillsCts = null;
        StillFrame?.Dispose();
        StillFrame = null;
        // No live session for this camera anymore — clear our signal so the
        // registry falls back to the reachability probe alone.
        _statusRegistry.ReportSession(Camera.Id, null);
        _coordinator.Invalidated -= OnCoordinatorInvalidated;
        _audio.Changed -= OnAudioChanged;
        _audio.Detach(Camera.Id); // no-op unless this tile is the current source
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        _resultsSub?.Dispose();
        DetachAnalytics();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(Camera.Id, _quality).ConfigureAwait(false);
        }
    }
}
