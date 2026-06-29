using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Status;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

// Health Center (Slice D): one overview of every camera's reachability and the
// reason behind any problem, derived from the shared CameraStatusPolicy. It is
// probe-based — it holds no live video sessions — so it reports Online/Offline
// with reasons; live "Attention" (a wedged stream) surfaces in the grid/single
// views where a session actually exists.
public sealed partial class HealthCenterViewModel : ViewModelBase
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly CameraDirectoryService _directory;
    private readonly IReachabilityProbe _reachability;
    private readonly ILogger<HealthCenterViewModel> _logger;

    public ObservableCollection<HealthRowViewModel> Rows { get; } = new();

    [ObservableProperty] private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _onlineCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _attentionCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _offlineCount;

    public string SummaryText
    {
        get
        {
            var loc = Localizer.Instance;
            return $"{OnlineCount} {loc["Health.Online"]} · {AttentionCount} {loc["Health.Attention"]} · {OfflineCount} {loc["Health.Offline"]}";
        }
    }

    public HealthCenterViewModel(
        CameraDirectoryService directory,
        IReachabilityProbe reachability,
        ILogger<HealthCenterViewModel> logger)
    {
        _directory = directory;
        _reachability = reachability;
        _logger = logger;
    }

    public Task LoadAsync(CancellationToken ct) => RefreshCoreAsync(ct);

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(CancellationToken.None);

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
            var rows = cameras.Select(c => new HealthRowViewModel(c)).ToList();

            Rows.Clear();
            foreach (var r in rows)
                Rows.Add(r);

            // Probe every camera in parallel — worst-case wait is a single
            // timeout, not the sum across the whole list.
            await Task.WhenAll(rows.Select(r => r.ProbeAsync(_reachability, ProbeTimeout, _logger, ct)))
                .ConfigureAwait(true);

            // Worst first so anything needing attention sits at the top.
            var sorted = Rows
                .OrderBy(r => SortRank(r.Status))
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Rows.Clear();
            foreach (var r in sorted)
                Rows.Add(r);

            OnlineCount = sorted.Count(r => r.Status == CameraStatus.Online);
            AttentionCount = sorted.Count(r => r.Status == CameraStatus.Attention);
            OfflineCount = sorted.Count(r => r.Status is CameraStatus.Offline or CameraStatus.Unknown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health Center refresh failed");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static int SortRank(CameraStatus s) => s switch
    {
        CameraStatus.Offline => 0,
        CameraStatus.Attention => 1,
        CameraStatus.Connecting => 2,
        CameraStatus.Unknown => 3,
        _ => 4, // Online
    };
}

public sealed partial class HealthRowViewModel : ViewModelBase
{
    public Camera Camera { get; }
    public string Name => Camera.Name;
    public string HostAndPort => Camera.HttpPort == 80
        ? Camera.Host
        : $"{Camera.Host}:{Camera.HttpPort}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private CameraStatus _status = CameraStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private CameraStatusReason _reason = CameraStatusReason.None;

    public string ReasonText => Localizer.Instance[Reason switch
    {
        CameraStatusReason.Unreachable => "Health.Reason.Unreachable",
        CameraStatusReason.Probing => "Health.Reason.Probing",
        CameraStatusReason.StreamError => "Health.Reason.StreamError",
        CameraStatusReason.StreamErrorButReachable => "Health.Reason.StreamErrorReachable",
        CameraStatusReason.Connecting => "Health.Reason.Connecting",
        _ => "Health.Reason.Ok",
    }];

    public HealthRowViewModel(Camera camera) => Camera = camera;

    // TCP-probes the dialed RTSP endpoint and collapses the result through the
    // shared policy. Reachability failures are swallowed into Offline — a probe
    // exception is just "not reachable" for the overview.
    public async Task ProbeAsync(IReachabilityProbe probe, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        bool reachable;
        try
        {
            var host = Camera.RtspMainUri.Host;
            if (string.IsNullOrEmpty(host)) host = Camera.Host;
            var port = Camera.RtspMainUri.Port;
            if (port <= 0) port = 554;
            reachable = await probe.IsReachableAsync(host, port, timeout, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Health probe failed for {CameraId}", Camera.Id);
            reachable = false;
        }

        var result = CameraStatusPolicy.Resolve(new CameraStatusInputs(Reachable: reachable));
        Status = result.Status;
        Reason = result.Reason;
    }
}
