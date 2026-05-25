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
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraTileViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly UserSettingsService _userSettings;
    private readonly ILogger<CameraTileViewModel> _logger;

    private readonly StreamQuality _quality = StreamQuality.Sub;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;
    private bool _started;
    private bool _disposed;

    public Camera Camera { get; }

    [ObservableProperty] private IVideoSession? _session;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private SessionState _state = SessionState.Idle;
    [ObservableProperty] private SessionTelemetry? _telemetry;

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

    public CameraTileViewModel(
        Camera camera,
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        UserSettingsService userSettings,
        ILogger<CameraTileViewModel> logger)
    {
        Camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _userSettings = userSettings;
        _logger = logger;

        _coordinator.Invalidated += OnCoordinatorInvalidated;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;

        var streamUri = Camera.RtspSubUri ?? Camera.RtspMainUri;
        if (Camera.RtspSubUri is null)
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
            _stateSub = session.StateChanged.Subscribe(s => State = s);
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
