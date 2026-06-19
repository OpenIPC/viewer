using System;
using System.Reactive.Linq;
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
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraTileViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly UserSettingsService _userSettings;
    private readonly ISnapshotService _snapshots;
    private readonly ILogger<CameraTileViewModel> _logger;

    // Auto SD/HD (Phase 12.2): substream in the grid, mainstream when a single
    // tile fills the view. Mutated by SetQualityAsync (a session swap), set
    // pre-activation by SetInitialQuality so a tile opening straight into 1×1
    // doesn't briefly start on the substream.
    private StreamQuality _quality = StreamQuality.Sub;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;
    private bool _started;
    private bool _disposed;

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
    private SessionState _state = SessionState.Idle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsLabel))]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private SessionTelemetry? _telemetry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorDetail))]
    private string? _errorMessage;

    public string Name => Camera.Name;
    public string StateLabel => State switch
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

    public CameraTileViewModel(
        Camera camera,
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        UserSettingsService userSettings,
        ISnapshotService snapshots,
        ILogger<CameraTileViewModel> logger)
    {
        Camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _userSettings = userSettings;
        _snapshots = snapshots;
        _logger = logger;

        _coordinator.Invalidated += OnCoordinatorInvalidated;
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
                    ErrorMessage = session.LastError;
            });
            _telemetrySub = session.Telemetry.Subscribe(t => Telemetry = t);
            Session = session;

            if (session.State == SessionState.Idle)
                await session.StartAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tile session for camera {Id}", Camera.Id);
            State = SessionState.Failed;
        }
    }

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
    // session, so the last frame stays frozen for an instant resume.
    public void Pause() => Session?.PauseDecode();
    public void Resume() => Session?.Resume();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _coordinator.Invalidated -= OnCoordinatorInvalidated;
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(Camera.Id, _quality).ConfigureAwait(false);
        }
    }
}
