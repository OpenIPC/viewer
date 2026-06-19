using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class SingleCameraPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IOnvifClient _onvif;
    private readonly IMajesticClient _majestic;
    private readonly IMajesticSshConfigClient _majesticSsh;
    private readonly RecordingService _recordings;
    private readonly UserSettingsService _userSettings;
    private readonly IDialogService _dialogs;
    private readonly ISnapshotService _snapshots;
    private readonly ILogger<SingleCameraPageViewModel> _logger;
    private Camera _camera;
    private DispatcherTimer? _recTimer;

    private readonly StreamQuality _quality = StreamQuality.Main;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;

    // Re-entrancy/lifecycle gates. MainView hosts CurrentPage in BOTH the wide
    // and narrow layouts, so two views bind this single VM and each fires
    // Loaded -> ActivateAsync / Unloaded -> DisposeAsync. Without these gates
    // the two Activate calls both pass the `Session is null` check before the
    // first await assigns it, double-Acquire (leaking a coordinator ref so the
    // session is never released) and double-Start (the 2nd throws "Already
    // started"). The leaked, stale session is then reused on the next open —
    // which is why an edited RTSP URL didn't take effect until an app restart.
    private bool _activating;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    private IVideoSession? _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private SessionState _state = SessionState.Idle;

    [ObservableProperty] private SessionTelemetry? _telemetry;
    [ObservableProperty] private string? _errorMessage;

    // Visible while the session is mid-connect (or backing off a reconnect). Gated
    // on Session != null so the empty pre-activate window doesn't show a spinner
    // out of nowhere. State changes flip both flags via NotifyPropertyChangedFor.
    public bool IsConnecting =>
        Session is not null && State is SessionState.Connecting or SessionState.Reconnecting;
    public bool IsFailed => State == SessionState.Failed;
    // Set by MainWindowViewModel while the device is in mobile landscape —
    // hides the header / Majestic panel / bottom bar so the video gets the
    // whole screen. Overlays (LIVE badge, telemetry, PTZ) stay on the video.
    [ObservableProperty] private bool _isFullscreen;

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
    [ObservableProperty] private bool? _draftRtmpEnabled;
    [ObservableProperty] private string? _draftRtmpUrl;
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
        IMajesticSshConfigClient majesticSsh,
        RecordingService recordings,
        UserSettingsService userSettings,
        IDialogService dialogs,
        ISnapshotService snapshots,
        ILogger<SingleCameraPageViewModel> logger)
    {
        _camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _onvif = onvif;
        _majestic = majestic;
        _majesticSsh = majesticSsh;
        _recordings = recordings;
        _userSettings = userSettings;
        _dialogs = dialogs;
        _snapshots = snapshots;
        _logger = logger;

        IsRecording = _recordings.IsRecording(_camera.Id);
        _recordings.StateChanged += OnRecordingsStateChanged;
        if (IsRecording) StartRecTimer();

        // Telemetry overlay visibility is a user pref — re-raise PropertyChanged
        // when the Settings page toggles it so the badges hide/show live.
        _userSettings.Changed += OnUserSettingsChanged;

        // RtspTransport switch from Settings drops every cached session via
        // LiveStreamSettingsBridge; we re-pull our own stream so the camera
        // page keeps showing video after the swap.
        _coordinator.Invalidated += OnCoordinatorInvalidated;
    }

    private void OnCoordinatorInvalidated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try { await ReloadStreamAsync().ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "SingleCamera reload after invalidation failed"); }
        });
    }

    // Combined visibility — both the user setting is on AND the session has
    // emitted at least one telemetry tick. Re-raised by both the Telemetry
    // property change (CommunityToolkit auto) and OnUserSettingsChanged.
    public bool ShowTelemetryBadges =>
        Telemetry is not null && _userSettings.Current.ShowTelemetryOverlay;

    partial void OnTelemetryChanged(SessionTelemetry? value)
        => OnPropertyChanged(nameof(ShowTelemetryBadges));

    private void OnUserSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowTelemetryBadges));
            OnPropertyChanged(nameof(IsRawConfigEditorEnabled));
        });
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
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.RecordingFailedFormat"], ex.Message);
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
        // Synchronous gate (no await between check and set) so a second caller
        // can't slip past while the first is mid-activation. Cleared in finally
        // so legitimate re-activation (ReloadStreamAsync nulls Session first)
        // still works.
        if (Session is not null || _activating || _disposed)
            return;
        _activating = true;

        try
        {
            var creds = await _directory.GetCredentialsAsync(_camera.Id, ct).ConfigureAwait(true);
            var options = VideoSessionOptions.Default(_camera.RtspMainUri, creds)
                with { Transport = ParseTransport(_userSettings.Current.RtspTransport) };

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
        finally
        {
            _activating = false;
        }
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
        DraftRtmpEnabled = MajesticConfig.RtmpEnabled;
        DraftRtmpUrl = MajesticConfig.RtmpUrl;
    }

    [RelayCommand]
    private async Task ApplyConfigAsync()
    {
        if (!IsMajestic || MajesticConfig is null) return;
        ApplyInProgress = true;
        ApplyStatus = Localizer.Instance["CameraPage.ApplyingStatus"];

        try
        {
            var patch = new MajesticConfigPatch(
                Codec: DraftCodec != MajesticConfig.Codec ? DraftCodec : null,
                Fps: DraftFps != MajesticConfig.Fps ? DraftFps : null,
                Resolution: DraftResolution != MajesticConfig.Resolution ? DraftResolution : null,
                Bitrate: DraftBitrate != MajesticConfig.Bitrate ? DraftBitrate : null,
                Profile: DraftProfile != MajesticConfig.Profile ? DraftProfile : null,
                RtmpEnabled: DraftRtmpEnabled != MajesticConfig.RtmpEnabled ? DraftRtmpEnabled : null,
                RtmpUrl: DraftRtmpUrl != MajesticConfig.RtmpUrl ? DraftRtmpUrl : null);

            var creds = await _directory.GetCredentialsAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
            var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _majestic.UpdateConfigAsync(endpoint, patch, cts.Token).ConfigureAwait(true);

            ApplyStatus = Localizer.Instance["CameraPage.AppliedRestarting"];
            // ReloadStreamAsync -> ActivateAsync -> InitMajesticAsync refreshes
            // config + drafts in one pass, so no extra fetch needed here.
            await ReloadStreamAsync().ConfigureAwait(true);
            ApplyStatus = Localizer.Instance["CameraPage.ApplyDone"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply Majestic config failed");
            ApplyStatus = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.ApplyFailedFormat"], ex.Message);
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

    // Bound to the Retry button on the Disconnected overlay. Clears the error
    // banner and re-runs the full Activate path (which re-acquires the session
    // and re-subscribes to State / Telemetry).
    [RelayCommand]
    private async Task RetryAsync()
    {
        ErrorMessage = null;
        await ReloadStreamAsync().ConfigureAwait(true);
    }

    // Visible only when the user opts in via Settings → Advanced. The flag is
    // a property (not [ObservableProperty]) — re-raised from OnUserSettingsChanged
    // so toggling the checkbox while on this page flips the button live.
    public bool IsRawConfigEditorEnabled => _userSettings.Current.RawConfigEditorEnabled;

    [RelayCommand]
    private async Task EditRawConfigAsync()
    {
        if (!IsMajestic || MajesticConfig is null) return;
        var initial = MajesticConfig.RawJson;
        var edited = await _dialogs.ShowRawConfigEditorAsync(initial).ConfigureAwait(true);
        if (edited is null) return;
        if (edited == initial) return;

        ApplyInProgress = true;
        ApplyStatus = Localizer.Instance["CameraPage.ApplyingRawStatus"];
        try
        {
            var creds = await _directory.GetCredentialsAsync(_camera.Id, CancellationToken.None).ConfigureAwait(true);
            var endpoint = new MajesticEndpoint(_camera.Host, _camera.HttpPort, creds);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _majestic.UpdateRawConfigAsync(endpoint, edited, cts.Token).ConfigureAwait(true);

            ApplyStatus = Localizer.Instance["CameraPage.AppliedRestarting"];
            await ReloadStreamAsync().ConfigureAwait(true);
            ApplyStatus = Localizer.Instance["CameraPage.ApplyDone"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply raw Majestic config failed");
            ApplyStatus = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.ApplyFailedFormat"], ex.Message);
        }
        finally
        {
            ApplyInProgress = false;
        }
    }

    // SSH fallback transport for majestic.yaml (Phase 13.5) — used when the
    // HTTP API is disabled/unreachable. Edits the raw YAML through the same
    // raw-config editor dialog, then reloads the stream like Apply does.
    [RelayCommand]
    private async Task EditMajesticOverSshAsync()
    {
        var endpoint = await _directory.GetSshEndpointAsync(_camera, CancellationToken.None).ConfigureAwait(true);
        if (endpoint is null)
        {
            ApplyStatus = Localizer.Instance["CameraPage.SshNoCreds"];
            return;
        }

        string initial;
        try
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            initial = await _majesticSsh.ReadRawAsync(endpoint, readCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read majestic.yaml over SSH failed");
            ApplyStatus = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.ApplyFailedFormat"], ex.Message);
            return;
        }

        var edited = await _dialogs.ShowRawConfigEditorAsync(initial).ConfigureAwait(true);
        if (edited is null || edited == initial) return;

        ApplyInProgress = true;
        ApplyStatus = Localizer.Instance["CameraPage.ApplyingRawStatus"];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _majesticSsh.WriteRawAsync(endpoint, edited, restart: true, cts.Token).ConfigureAwait(true);

            ApplyStatus = Localizer.Instance["CameraPage.AppliedRestarting"];
            await ReloadStreamAsync().ConfigureAwait(true);
            ApplyStatus = Localizer.Instance["CameraPage.ApplyDone"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write majestic.yaml over SSH failed");
            ApplyStatus = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.ApplyFailedFormat"], ex.Message);
        }
        finally
        {
            ApplyInProgress = false;
        }
    }

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
            // The shared service picks the HD source (live mainstream → Majestic
            // HTTP → brief mainstream open) and indexes the result; we just hand
            // it our live session so an already-decoded frame is grabbed for free.
            var snapshot = await _snapshots
                .CaptureAsync(_camera, Session, _quality, CancellationToken.None)
                .ConfigureAwait(true);
            SnapshotPath = snapshot.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot failed");
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.SnapshotFailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private void OpenSnapshot()
    {
        var path = SnapshotPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open snapshot failed");
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.OpenSnapshotFailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopySnapshotAsync()
    {
        var path = SnapshotPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await _dialogs.CopyFileToClipboardAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copy snapshot failed");
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.CopySnapshotFailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveSnapshotAsAsync()
    {
        var path = SnapshotPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var target = await _dialogs.PickSaveFileAsync(
                Path.GetFileName(path),
                Localizer.Instance["Snapshot.SaveAsTitle"],
                "jpg").ConfigureAwait(true);
            if (string.IsNullOrEmpty(target)) return;
            File.Copy(path, target, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Save snapshot as failed");
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Localizer.Instance["CameraPage.SaveSnapshotFailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private void Back() =>
        WeakReferenceMessenger.Default.Send(new GoBackToLibraryMessage());

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the two hosting views (wide + narrow layout) each fire
        // Unloaded -> DisposeAsync, and MainWindowViewModel disposes us on
        // navigation too. Only the first call releases the session, so the
        // coordinator ref-count lands back at zero instead of leaking.
        if (_disposed)
            return;
        _disposed = true;

        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        _recordings.StateChanged -= OnRecordingsStateChanged;
        _userSettings.Changed -= OnUserSettingsChanged;
        _coordinator.Invalidated -= OnCoordinatorInvalidated;
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

    private static RtspTransport ParseTransport(string? s) => s?.ToLowerInvariant() switch
    {
        "udp" => RtspTransport.Udp,
        _ => RtspTransport.Tcp,
    };
}
