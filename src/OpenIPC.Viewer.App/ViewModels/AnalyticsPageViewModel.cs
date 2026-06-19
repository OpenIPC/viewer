using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

// AI control center (Phase 15.7): engine status + diagnostics, the cameras with
// analytics on, and recent detection events. The diagnostics counters poll on a
// 1 Hz timer that the view starts/stops with its visual lifetime.
public sealed partial class AnalyticsPageViewModel : ViewModelBase
{
    private readonly IAnalyticsEngine _engine;
    private readonly CameraDirectoryService _directory;
    private readonly IEventRepository _events;
    private readonly ILogger<AnalyticsPageViewModel> _logger;
    private readonly DispatcherTimer _timer;

    public string Title => Localizer.Instance["Nav.Analytics"];

    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private string _engineStatus = "";
    [ObservableProperty] private string _activeProvider = "—";
    [ObservableProperty] private long _framesProcessed;
    [ObservableProperty] private long _framesDropped;
    [ObservableProperty] private int _queueDepth;
    [ObservableProperty] private double _averageLatencyMs;
    [ObservableProperty] private int _activeCameras;

    public ObservableCollection<AnalyticsCameraRow> Cameras { get; } = new();
    public ObservableCollection<DetectionEventRow> RecentDetections { get; } = new();

    public AnalyticsPageViewModel(
        IAnalyticsEngine engine,
        CameraDirectoryService directory,
        IEventRepository events,
        ILogger<AnalyticsPageViewModel> logger)
    {
        _engine = engine;
        _directory = directory;
        _events = events;
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
            var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
            var names = cameras.ToDictionary(c => c.Id, c => c.Name);

            Cameras.Clear();
            foreach (var c in cameras.Where(c => c.AnalyticsOrDefault.Enabled))
            {
                var s = c.AnalyticsOrDefault;
                Cameras.Add(new AnalyticsCameraRow(
                    c.Name, ClassesSummary(s.ClassIds), s.AnalyticsFps, s.ConfidenceThreshold, s.AutoRecord));
            }

            var events = await _events.ListAsync(null, EventKind.Detection, null, 50, ct).ConfigureAwait(true);
            RecentDetections.Clear();
            foreach (var ev in events)
            {
                var name = names.TryGetValue(ev.CameraId, out var n) ? n : ev.CameraId.ToString();
                RecentDetections.Add(new DetectionEventRow(
                    name, ev.OccurredAt.ToLocalTime(), ev.Summary ?? ""));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load analytics control center data.");
        }
    }

    private void RefreshDiagnostics()
    {
        var d = _engine.Diagnostics;
        EngineReady = _engine.IsReady;
        EngineStatus = StatusLabel(_engine.Status);
        ActiveProvider = _engine.ActiveProvider.ToString();
        FramesProcessed = d.FramesProcessed;
        FramesDropped = d.FramesDropped;
        QueueDepth = d.QueueDepth;
        AverageLatencyMs = d.AverageLatencyMs;
        ActiveCameras = d.ActiveCameras;
    }

    private static string StatusLabel(AnalyticsEngineStatus status) => status switch
    {
        AnalyticsEngineStatus.Preparing => Localizer.Instance["Analytics.Status.Preparing"],
        AnalyticsEngineStatus.Loading => Localizer.Instance["Analytics.Status.Loading"],
        AnalyticsEngineStatus.Ready => Localizer.Instance["Analytics.Status.Ready"],
        AnalyticsEngineStatus.Failed => Localizer.Instance["Analytics.Status.Failed"],
        _ => Localizer.Instance["Analytics.Status.NotStarted"],
    };

    private static string ClassesSummary(IReadOnlyCollection<int>? ids)
    {
        if (ids is not { Count: > 0 }) return Localizer.Instance["Analytics.AllClasses"];
        return string.Join(", ", ids.Select(id =>
            id >= 0 && id < CocoClasses.Names.Count ? CocoClasses.Names[id] : $"class{id}"));
    }
}

public sealed record AnalyticsCameraRow(
    string Name, string Classes, int Fps, float Threshold, bool AutoRecord);

public sealed record DetectionEventRow(string CameraName, DateTime OccurredAt, string Summary);
