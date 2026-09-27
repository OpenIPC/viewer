using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

// AI control center (Phase 15.7): engine status + diagnostics, per-camera
// detection (on/off, settings summary, 24 h activity) and a 24 h class summary.
// Diagnostics poll on a 1 Hz timer the view starts/stops with its lifetime.
public sealed partial class AnalyticsPageViewModel : ViewModelBase
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromHours(24);

    private readonly IAnalyticsEngine _engine;
    private readonly CameraDirectoryService _directory;
    private readonly IEventRepository _events;
    private readonly CameraEditService _editService;
    private readonly ILogger<AnalyticsPageViewModel> _logger;
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<Camera> _allCameras = Array.Empty<Camera>();

    public string Title => Localizer.Instance["Nav.Analytics"];

    // --- Engine -----------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsNotStarted))]
    [NotifyPropertyChangedFor(nameof(ShowTiles))]
    private AnalyticsEngineStatus _status;

    public bool IsReady => Status == AnalyticsEngineStatus.Ready;
    public bool IsBusy => Status is AnalyticsEngineStatus.Preparing or AnalyticsEngineStatus.Loading;
    public bool IsFailed => Status == AnalyticsEngineStatus.Failed;
    public bool IsNotStarted => Status == AnalyticsEngineStatus.NotStarted;
    public bool ShowTiles => Status is AnalyticsEngineStatus.Ready or AnalyticsEngineStatus.Loading;

    // "Ready · CPU"
    [ObservableProperty] private string _statusBadge = "";

    [ObservableProperty] private string _activeCameras = "0";
    [ObservableProperty] private string _framesProcessed = "0";
    [ObservableProperty] private string _droppedShare = "0 %";
    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _queueDepth = "0";

    // --- Cameras ----------------------------------------------------------------
    public ObservableCollection<AiCameraRow> Cameras { get; } = new();
    public bool HasCameras => Cameras.Count > 0;

    // "Enable on a camera…" picker: cameras with detection off.
    public ObservableCollection<AiEnableCandidate> EnableCandidates { get; } = new();
    [ObservableProperty] private string _enableSearch = "";
    partial void OnEnableSearchChanged(string value) => RebuildCandidates();

    // --- Last 24 h ----------------------------------------------------------------
    public ObservableCollection<DetectionClassChip> ClassSummary { get; } = new();
    public bool HasClassSummary => ClassSummary.Count > 0;

    public AnalyticsPageViewModel(
        IAnalyticsEngine engine,
        CameraDirectoryService directory,
        IEventRepository events,
        CameraEditService editService,
        ILogger<AnalyticsPageViewModel> logger)
    {
        _engine = engine;
        _directory = directory;
        _events = events;
        _editService = editService;
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshDiagnostics();
    }

    // Called by the view when shown.
    public async Task StartAsync()
    {
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        _timer.Start();
    }

    // Called by the view when hidden — stop polling.
    public void Stop() => _timer.Stop();

    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) => ReloadAsync(ct);

    private async Task ReloadAsync(CancellationToken ct)
    {
        RefreshDiagnostics();
        try
        {
            _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);

            var since = DateTime.UtcNow - ActivityWindow;
            var detections = await _events.ListAsync(null, EventKind.Detection, since, 10000, ct).ConfigureAwait(true);
            var perCamera = detections.GroupBy(e => e.CameraId).ToDictionary(g => g.Key, g => g.Count());

            Cameras.Clear();
            foreach (var c in _allCameras.Where(c => c.AnalyticsOrDefault.Enabled)
                         .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Cameras.Add(new AiCameraRow(c, perCamera.TryGetValue(c.Id, out var n) ? n : 0, OnRowToggled));
            }
            OnPropertyChanged(nameof(HasCameras));
            RebuildCandidates();

            // Per class: how many detections it appeared in over the window.
            var perClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in detections)
                foreach (var label in DetectionClasses.Parse(ev.Summary).Keys)
                    perClass[label] = perClass.TryGetValue(label, out var cur) ? cur + 1 : 1;
            ClassSummary.Clear();
            foreach (var (label, count) in perClass.OrderByDescending(kv => kv.Value).Take(8))
                ClassSummary.Add(new DetectionClassChip(label, count, DetectionClasses.IconKey(label)));
            OnPropertyChanged(nameof(HasClassSummary));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load analytics control center data.");
        }
    }

    private void RebuildCandidates()
    {
        var q = EnableSearch?.Trim() ?? "";
        var enabled = Cameras.Where(r => r.IsEnabled).Select(r => r.Camera.Id).ToHashSet();
        EnableCandidates.Clear();
        foreach (var c in _allCameras
                     .Where(c => !enabled.Contains(c.Id))
                     .Where(c => q.Length == 0
                                 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || c.Host.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(50))
        {
            EnableCandidates.Add(new AiEnableCandidate(c.Id, c.Name, c.Host));
        }
    }

    // Row switch: flip Enabled but keep the rest of the camera's settings, so
    // turning detection back on restores classes / fps / threshold.
    private async void OnRowToggled(AiCameraRow row)
    {
        try
        {
            var settings = row.Camera.AnalyticsOrDefault with { Enabled = row.IsEnabled };
            await _directory.SetAnalyticsAsync(row.Camera.Id, settings, CancellationToken.None).ConfigureAwait(true);
            RebuildCandidates();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toggling detection for {Camera} failed", row.Camera.Name);
        }
    }

    [RelayCommand]
    private async Task EnableCameraAsync(AiEnableCandidate? candidate)
    {
        if (candidate is null) return;
        var camera = _allCameras.FirstOrDefault(c => c.Id == candidate.Id);
        if (camera is null) return;
        try
        {
            var settings = camera.AnalyticsOrDefault with { Enabled = true };
            await _directory.SetAnalyticsAsync(camera.Id, settings, CancellationToken.None).ConfigureAwait(true);
            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enabling detection for {Camera} failed", camera.Name);
        }
    }

    // Detection classes, fps, threshold and auto-record live in the camera
    // editor; open it on that camera.
    [RelayCommand]
    private async Task ConfigureCameraAsync(AiCameraRow? row)
    {
        if (row is null) return;
        if (await _editService.EditAsync(row.Camera).ConfigureAwait(true))
            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowAllDetections() => WeakReferenceMessenger.Default.Send(new ShowDetectionEventsMessage());

    private void RefreshDiagnostics()
    {
        var d = _engine.Diagnostics;
        Status = _engine.Status;
        var provider = ProviderLabel(_engine.ActiveProvider);
        StatusBadge = IsReady ? $"{StatusLabel(Status)} · {provider}" : StatusLabel(Status);
        ActiveCameras = d.ActiveCameras.ToString(CultureInfo.CurrentCulture);
        FramesProcessed = d.FramesProcessed.ToString("N0", CultureInfo.CurrentCulture);
        var total = d.FramesProcessed + d.FramesDropped;
        DroppedShare = total == 0 ? "0 %" : $"{100.0 * d.FramesDropped / total:0.#} %";
        Latency = d.AverageLatencyMs > 0 ? $"{d.AverageLatencyMs:F0} ms" : "—";
        QueueDepth = d.QueueDepth.ToString(CultureInfo.CurrentCulture);
    }

    private static string ProviderLabel(ExecutionProvider p) => p switch
    {
        ExecutionProvider.Cpu => "CPU",
        ExecutionProvider.DirectMl => "DirectML",
        ExecutionProvider.Cuda => "CUDA",
        ExecutionProvider.OpenVino => "OpenVINO",
        ExecutionProvider.CoreMl => "Core ML",
        ExecutionProvider.NnApi => "NNAPI",
        ExecutionProvider.Xnnpack => "XNNPACK",
        _ => p.ToString(),
    };

    private static string StatusLabel(AnalyticsEngineStatus status) => status switch
    {
        AnalyticsEngineStatus.Preparing => Localizer.Instance["Analytics.Status.Preparing"],
        AnalyticsEngineStatus.Loading => Localizer.Instance["Analytics.Status.Loading"],
        AnalyticsEngineStatus.Ready => Localizer.Instance["Analytics.Status.Ready"],
        AnalyticsEngineStatus.Failed => Localizer.Instance["Analytics.Status.Failed"],
        _ => Localizer.Instance["Analytics.Status.NotStarted"],
    };
}

// A camera with detection configured: switch, classes, settings, 24 h count.
public sealed partial class AiCameraRow : ObservableObject
{
    private readonly Action<AiCameraRow> _onToggled;

    public AiCameraRow(Camera camera, int detections24h, Action<AiCameraRow> onToggled)
    {
        Camera = camera;
        _onToggled = onToggled;
        _isEnabled = camera.AnalyticsOrDefault.Enabled;
        var s = camera.AnalyticsOrDefault;
        Classes = s.ClassIds is { Count: > 0 } ids
            ? ids.Where(id => id >= 0 && id < CocoClasses.Names.Count)
                 .Select(id => CocoClasses.Names[id])
                 .Select(label => new DetectionClassChip(label, 0, DetectionClasses.IconKey(label)))
                 .ToList()
            : Array.Empty<DetectionClassChip>();
        SettingsLabel = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Analytics.SettingsFormat"],
            s.AnalyticsFps, s.ConfidenceThreshold);
        AutoRecord = s.AutoRecord;
        Detections24h = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Analytics.Detections24hFormat"], detections24h);
    }

    public Camera Camera { get; }
    public string Name => Camera.Name;
    public IReadOnlyList<DetectionClassChip> Classes { get; }
    public bool AllClasses => Classes.Count == 0;
    public string SettingsLabel { get; }
    public bool AutoRecord { get; }
    public string Detections24h { get; }

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _onToggled(this);
}

public sealed record AiEnableCandidate(CameraId Id, string Name, string Host);
