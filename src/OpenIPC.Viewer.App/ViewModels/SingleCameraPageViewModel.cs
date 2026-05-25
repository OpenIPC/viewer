using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class SingleCameraPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IOnvifClient _onvif;
    private readonly IMajesticClient _majestic;
    private readonly RecordingService _recordings;
    private readonly IFileSystem _fs;
    private readonly ILogger<SingleCameraPageViewModel> _logger;
    private Camera _camera;
    private DispatcherTimer? _recTimer;

    private readonly StreamQuality _quality = StreamQuality.Main;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;

    [ObservableProperty] private IVideoSession? _session;
    [ObservableProperty] private SessionState _state = SessionState.Idle;
    [ObservableProperty] private SessionTelemetry? _telemetry;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _snapshotPath;
    [ObservableProperty] private PtzController? _ptz;
    [ObservableProperty] private string _newPresetName = "";

    // Majestic state. IsMajestic gates the whole config panel; MajesticConfig
    // is null until first GetConfigAsync completes.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMajestic))]
    private bool _majesticReady;
    [ObservableProperty] private MajesticConfig? _majesticConfig;
    [ObservableProperty] private MajesticInfo? _majesticInfo;
    [ObservableProperty] private NightMode _currentNightMode = NightMode.Unknown;
    [ObservableProperty] private string? _majesticError;

    // Editable drafts for Apply (Phase 5c). Hydrated from MajesticConfig on load
    // and after each successful Apply.
    [ObservableProperty] private string? _draftCodec;
    [ObservableProperty] private string? _draftResolution;
    [ObservableProperty] private int? _draftFps;
    [ObservableProperty] private int? _draftBitrate;
    [ObservableProperty] private string? _draftProfile;
    [ObservableProperty] private bool _applyInProgress;
    [ObservableProperty] private string? _applyStatus;
    [ObservableProperty] private bool _showRawJson;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingElapsed = "REC 00:00:00";

    // 9e — touch gestures. ZoomLevel drives the ScaleTransform on the video
    // surface (digital zoom only, 1.0..MaxZoom). IsPtzOverlayVisible is a
    // toggle hidden behind long-press on narrow viewports — the joystick
    // takes a chunk of screen real estate that's fine on desktop but should
    // stay out of the way on a phone until the user explicitly asks.
    public const double MinZoom = 1.0;
    public const double MaxZoom = 4.0;
    public const double ZoomStep = 0.25;

    [ObservableProperty] private double _zoomLevel = MinZoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPtzPanelVisible))]
    private bool _isPtzOverlayVisible = true;

    public bool IsPtzPanelVisible => HasPtz && IsPtzOverlayVisible;

    // Hardcoded option lists. Phase-05 risk §"Кривой conf": free-input on res/codec
    // can brick the camera, so dropdowns only.
    public System.Collections.Generic.IReadOnlyList<string> CodecOptions { get; } = new[] { "h264", "h265" };
    public System.Collections.Generic.IReadOnlyList<string> ResolutionOptions { get; } =
        new[] { "640x480", "1280x720", "1920x1080", "2560x1440", "3840x2160" };
    public System.Collections.Generic.IReadOnlyList<int> FpsOptions { get; } = new[] { 10, 15, 20, 25, 30 };
    public System.Collections.Generic.IReadOnlyList<string> ProfileOptions { get; } = new[] { "baseline", "main", "high" };

    public bool HasPtz => _camera.HasPtz && !string.IsNullOrEmpty(_camera.OnvifProfileToken);
    public bool IsMajestic => MajesticReady;
    public ObservableCollection<PtzPreset> Presets { get; } = new();

    public string CameraName => _camera.Name;
    public string HostLabel => _camera.Host;

    public SingleCameraPageViewModel(
        Camera camera,
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        IOnvifClient onvif,
        IMajesticClient majestic,
        RecordingService recordings,
        IFileSystem fs,
        ILogger<SingleCameraPageViewModel> logger)
    {
        _camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _onvif = onvif;
        _majestic = majestic;
        _recordings = recordings;
        _fs = fs;
        _logger = logger;

        IsRecording = _recordings.IsRecording(_camera.Id);
        _recordings.StateChanged += OnRecordingsStateChanged;
        if (IsRecording) StartRecTimer();
    }

    private void OnRecordingsStateChanged(object? sender, CameraId cam)
    {
        if (cam != _camera.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            IsRecording = _recordings.IsRecording(_camera.Id);
            if (IsRecording) StartRecTimer();
            else StopRecTimer();
        });
    }

    private void StartRecTimer()
    {
        _recTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRecElapsed());
        UpdateRecElapsed();
        _recTimer.Start();
    }

    private void StopRecTimer()
    {
        _recTimer?.Stop();
        RecordingElapsed = "REC 00:00:00";
    }

    private void UpdateRecElapsed()
    {
        var start = _recordings.StartedAt(_camera.Id);
        if (start is null) return;
        var elapsed = DateTime.UtcNow - start.Value;
        RecordingElapsed = $"REC {(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        try
        {
            await _recordings.ToggleAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle recording failed for {CameraId}", _camera.Id);
            ErrorMessage = $"Recording failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePtzOverlay() => IsPtzOverlayVisible = !IsPtzOverlayVisible;

    [RelayCommand]
    private void ResetZoom() => ZoomLevel = MinZoom;

    public void ApplyZoomDelta(double factor)
    {
        var next = Math.Clamp(ZoomLevel * factor, MinZoom, MaxZoom);
        ZoomLevel = next;
    }

    public void StepZoom(int steps)
    {
        var next = Math.Clamp(ZoomLevel + steps * ZoomStep, MinZoom, MaxZoom);
        ZoomLevel = next;
    }

    public async Task NavigateRelativeAsync(int offset, CancellationToken ct)
    {
        if (offset == 0) return;
        var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        if (cameras.Count <= 1) return;

        var index = -1;
        for (var i = 0; i < cameras.Count; i++)
        {
            if (cameras[i].Id == _camera.Id) { index = i; break; }
        }
        if (index < 0) return;

        // Wrap around — swiping past the last camera takes you back to the
        // first. Matches phone-gallery muscle memory.
        var n = cameras.Count;
        var next = ((index + offset) % n + n) % n;
        if (next == index) return;

        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(cameras[next].Id));
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        if (Session is not null)
            return;

        var creds = await _directory.GetCredentialsAsync(_camera.Id, ct).ConfigureAwait(true);
        var options = VideoSessionOptions.Default(_camera.RtspMainUri, creds);

        try
        {
            var session = _coordinator.Acquire(_camera.Id, _quality, options);
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
            _logger.LogError(ex, "Failed to start session for camera {CameraId}", _camera.Id);
            ErrorMessage = ex.Message;
            State = SessionState.Failed;
        }

        if (HasPtz)
            await InitPtzAsync(creds, ct).ConfigureAwait(true);

        await InitMajesticAsync(creds, ct).ConfigureAwait(true);
    }

    private async Task InitMajesticAsync(CameraCredentials? creds, CancellationToken ct)
    {
        var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);

        // Trust the persisted flag if set; otherwise probe and persist on success.
        bool reachable = _camera.IsMajestic;
        if (!reachable)
        {
            try
            {
                reachable = await _majestic.PingAsync(endpoint, ct).ConfigureAwait(true);
                if (reachable)
                {
                    await _directory.SetIsMajesticAsync(_camera.Id, true, CancellationToken.None).ConfigureAwait(true);
                    _camera = _camera with { IsMajestic = true };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Majestic ping threw for {CameraId}", _camera.Id);
                reachable = false;
            }
        }

        if (!reachable) return;
        MajesticReady = true;

        try
        {
            var (cfg, info) = await GetMajesticStateAsync(endpoint, ct).ConfigureAwait(true);
            MajesticConfig = cfg;
            MajesticInfo = info;
            CurrentNightMode = cfg.NightMode;
            HydrateDrafts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Majestic config for {CameraId}", _camera.Id);
            MajesticError = ex.Message;
        }
    }

    private void HydrateDrafts()
    {
        if (MajesticConfig is null) return;
        DraftCodec = MajesticConfig.Codec;
        DraftResolution = MajesticConfig.Resolution;
        DraftFps = MajesticConfig.Fps;
        DraftBitrate = MajesticConfig.Bitrate;
        DraftProfile = MajesticConfig.Profile;
    }

    [RelayCommand]
    private async Task ApplyConfigAsync()
    {
        if (!IsMajestic || MajesticConfig is null) return;
        ApplyInProgress = true;
        ApplyStatus = "Applying…";

        try
        {
            var patch = new MajesticConfigPatch(
                Codec: DraftCodec != MajesticConfig.Codec ? DraftCodec : null,
                Fps: DraftFps != MajesticConfig.Fps ? DraftFps : null,
                Resolution: DraftResolution != MajesticConfig.Resolution ? DraftResolution : null,
                Bitrate: DraftBitrate != MajesticConfig.Bitrate ? DraftBitrate : null,
                Profile: DraftProfile != MajesticConfig.Profile ? DraftProfile : null);

            var creds = await _directory.GetCredentialsAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
            var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _majestic.UpdateConfigAsync(endpoint, patch, cts.Token).ConfigureAwait(true);

            ApplyStatus = "Applied. Restarting stream…";
            // ReloadStreamAsync -> ActivateAsync -> InitMajesticAsync refreshes
            // config + drafts in one pass, so no extra fetch needed here.
            await ReloadStreamAsync().ConfigureAwait(true);
            ApplyStatus = "Done.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply Majestic config failed");
            ApplyStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            ApplyInProgress = false;
        }
    }

    // After Apply the camera restarts its streamer; our existing session sees a
    // disconnect anyway, so we release proactively and re-acquire to skip the
    // reconnect-backoff wait inside AutoReconnectingVideoSession.
    private async Task ReloadStreamAsync()
    {
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(_camera.Id, _quality).ConfigureAwait(true);
        }
        // Empirically camera takes 2–5s to come back; phase-05 risks §"Apply ломает поток".
        try { await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        await ActivateAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleRawJson() => ShowRawJson = !ShowRawJson;

    private async Task<(MajesticConfig config, MajesticInfo info)> GetMajesticStateAsync(MajesticEndpoint ep, CancellationToken ct)
    {
        var cfgTask = _majestic.GetConfigAsync(ep, ct);
        var infoTask = _majestic.GetInfoAsync(ep, ct);
        await Task.WhenAll(cfgTask, infoTask).ConfigureAwait(false);
        return (await cfgTask.ConfigureAwait(false), await infoTask.ConfigureAwait(false));
    }

    [RelayCommand]
    private async Task SetNightModeAsync(string? modeName)
    {
        if (!IsMajestic || modeName is null) return;
        var mode = modeName switch
        {
            "day" => NightMode.Day,
            "night" => NightMode.Night,
            "auto" => NightMode.Auto,
            _ => NightMode.Unknown,
        };
        if (mode == NightMode.Unknown) return;

        try
        {
            var creds = await _directory.GetCredentialsAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
            var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _majestic.SetNightModeAsync(endpoint, mode, cts.Token).ConfigureAwait(true);
            CurrentNightMode = mode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set night mode {Mode}", mode);
            MajesticError = ex.Message;
        }
    }

    private async Task InitPtzAsync(CameraCredentials? creds, CancellationToken ct)
    {
        var port = _camera.OnvifPort ?? 80;
        var endpoint = OnvifEndpoint.FromHost(_camera.Host, port, creds);
        Ptz = new PtzController(_onvif, endpoint, _camera.OnvifProfileToken!);
        await ReloadPresetsAsync(ct).ConfigureAwait(true);
    }

    private async Task ReloadPresetsAsync(CancellationToken ct)
    {
        if (Ptz is null) return;
        try
        {
            var list = await Ptz.GetPresetsAsync(ct).ConfigureAwait(true);
            Presets.Clear();
            foreach (var p in list) Presets.Add(p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PTZ presets for {CameraId}", _camera.Id);
        }
    }

    [RelayCommand]
    private async Task GotoPresetAsync(PtzPreset? preset)
    {
        if (preset is null || Ptz is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Ptz.GotoPresetAsync(preset.Token, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to goto preset {Preset}", preset.Token);
        }
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (Ptz is null || string.IsNullOrWhiteSpace(NewPresetName)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Ptz.SetPresetAsync(NewPresetName.Trim(), cts.Token).ConfigureAwait(true);
            NewPresetName = "";
            await ReloadPresetsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save preset");
        }
    }

    [RelayCommand]
    private async Task RemovePresetAsync(PtzPreset? preset)
    {
        if (preset is null || Ptz is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Ptz.RemovePresetAsync(preset.Token, cts.Token).ConfigureAwait(true);
            await ReloadPresetsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove preset {Preset}", preset.Token);
        }
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        try
        {
            byte[] bytes;
            if (IsMajestic)
            {
                // /image.jpg is ~50–100ms vs ~hundreds of ms decoding a video frame.
                var creds = await _directory.GetCredentialsAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
                var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                bytes = await _majestic.SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token).ConfigureAwait(true);
            }
            else if (Session is not null)
            {
                bytes = await Session.SnapshotAsync(SnapshotFormat.Jpeg, CancellationToken.None).ConfigureAwait(true);
            }
            else return;

            var dir = Path.Combine(_fs.SnapshotsDir.FullName, SafeFileName(_camera.Name));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd-HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            SnapshotPath = path;
            _logger.LogInformation("Snapshot saved {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot failed");
            ErrorMessage = $"Snapshot failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Back() =>
        WeakReferenceMessenger.Default.Send(new GoBackToLibraryMessage());

    public async ValueTask DisposeAsync()
    {
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        _recordings.StateChanged -= OnRecordingsStateChanged;
        StopRecTimer();
        if (Ptz is not null)
        {
            await Ptz.DisposeAsync().ConfigureAwait(false);
            Ptz = null;
        }
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(_camera.Id, _quality).ConfigureAwait(false);
        }
        // NOTE: we deliberately do NOT stop the recording on page close —
        // the camera keeps recording in the background until the user
        // explicitly stops it (or app exits, see RecordingService.DisposeAsync).
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }
}
