using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

// Health page: per-camera connection counters, on-demand link checks (quick and
// under load), the fixes a check can justify, and the shared-segment analysis
// of the drop history. The automatic checks run in LinkHealthMonitor whether
// or not this page is open; the page only shows their reports.
public sealed partial class NetworkHealthPageViewModel : ViewModelBase
{
    private readonly CameraDirectoryService _directory;
    private readonly ConnectionJournal _journal;
    private readonly LinkHealthService _service;
    private readonly UserSettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IFileSystem _fs;
    private readonly ILogger<NetworkHealthPageViewModel> _logger;
    private readonly Dictionary<CameraId, string> _names = new();
    private readonly Dictionary<string, string> _namesByHost = new(StringComparer.OrdinalIgnoreCase);
    private bool _active;
    // Cancelled when the page leaves the screen: page-started checks stop and
    // their completions no longer touch the detached page. A new one per visit.
    private CancellationTokenSource? _lifetime;
    private static readonly CancellationToken Cancelled = new(true);

    internal CancellationToken Lifetime => _lifetime?.Token ?? Cancelled;
    internal bool IsActive => _active;
    private bool _refreshQueued;

    public ObservableCollection<LinkHealthRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> TopologyLines { get; } = new();
    public ObservableCollection<RouteLineViewModel> RouteLines { get; } = new();
    public bool CanRunFixElevated => RouteFixScriptFile.CanRunElevated;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _topologyTitle = string.Empty;
    [ObservableProperty] private string _topologyHint = string.Empty;
    [ObservableProperty] private bool _autoCheck;
    [ObservableProperty] private bool _autoFix;

    // This computer's routing (the Routes card).
    [ObservableProperty] private string _routesSummary = string.Empty;
    [ObservableProperty] private string _routesStatus = string.Empty;
    [ObservableProperty] private bool _hasFix;
    [ObservableProperty] private string _fixScriptText = string.Empty;
    [ObservableProperty] private string _fixScriptPath = string.Empty;
    // What the last run of the fix wrote to its log (shown under the card).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixLog))]
    private string _fixLogText = string.Empty;
    public bool HasFixLog => FixLogText.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckRoutesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFixScriptCommand))]
    private bool _isCheckingRoutes;

    public NetworkHealthPageViewModel(
        CameraDirectoryService directory,
        ConnectionJournal journal,
        LinkHealthService service,
        UserSettingsService settings,
        IDialogService dialogs,
        IFileSystem fs,
        ILogger<NetworkHealthPageViewModel> logger)
    {
        _directory = directory;
        _journal = journal;
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        _fs = fs;
        _logger = logger;
        _autoCheck = settings.Current.LinkHealthAutoCheck;
        _autoFix = settings.Current.LinkHealthAutoFix;
    }

    public async Task StartAsync()
    {
        if (_active) return;
        _active = true;
        _lifetime = new CancellationTokenSource();
        _journal.Changed += OnJournalChanged;
        _service.ReportReady += OnReportReady;
        _service.RoutingReady += OnRoutingReady;
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
        await LoadAsync().ConfigureAwait(true);
        if (!_active) return;
        // The routing check is cheap (a few seconds) and answers the most common
        // "camera is plugged in but unreachable" question, so it runs on open.
        if (_service.LastRouting is null || DateTimeOffset.Now - _service.LastRouting.At > TimeSpan.FromMinutes(1))
            _ = CheckRoutesAsync();
        else
            UpdateRoutes();
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        // Cancel, don't dispose: tasks still holding the token may register on it.
        _lifetime?.Cancel();
        _lifetime = null;
        _journal.Changed -= OnJournalChanged;
        _service.ReportReady -= OnReportReady;
        _service.RoutingReady -= OnRoutingReady;
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;
    }

    private bool CanCheckRoutes() => !IsCheckingRoutes;

    [RelayCommand(CanExecute = nameof(CanCheckRoutes))]
    private async Task CheckRoutesAsync()
    {
        var ct = Lifetime;
        if (ct.IsCancellationRequested) return;
        IsCheckingRoutes = true;
        RoutesStatus = Localizer.Instance["LinkHealth.Routes.Checking"];
        try
        {
            await _service.CheckRoutingAsync(ct).ConfigureAwait(true);
            RoutesStatus = string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // the page left
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routing check failed");
            RoutesStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["LinkHealth.ActionFailed"], ex.Message);
        }
        finally
        {
            IsCheckingRoutes = false;
        }
        if (_active) UpdateRoutes();
    }

    [RelayCommand]
    private async Task SaveFixScriptAsync()
    {
        if (_service.LastRouting is not { } report || report.Fix.IsEmpty) return;
        var target = RouteFixScript.CurrentTarget();
        var dest = await _dialogs.PickSaveFileAsync(
            RouteFixScript.FileName(target), Localizer.Instance["LinkHealth.Routes.SaveTitle"],
            target == RouteScriptTarget.Windows ? "ps1" : "sh").ConfigureAwait(true);
        if (string.IsNullOrEmpty(dest)) return;
        try
        {
            System.IO.File.WriteAllText(dest, RouteFixScriptFile.Text(report.Fix, IsRussian),
                new System.Text.UTF8Encoding(RouteFixScript.NeedsBom(target)));
            RoutesStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["LinkHealth.Routes.Saved"], dest);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            RoutesStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["LinkHealth.ActionFailed"], ex.Message);
        }
    }

    private bool CanRunFix() => !IsCheckingRoutes && RouteFixScriptFile.CanRunElevated;

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunFixScriptAsync()
    {
        if (_service.LastRouting is not { } report || report.Fix.IsEmpty) return;
        var loc = Localizer.Instance;
        var ok = await _dialogs.ConfirmAsync(loc["LinkHealth.Routes.RunTitle"], loc["LinkHealth.Routes.RunMessage"],
            loc["LinkHealth.Routes.RunGo"], loc["Common.Cancel"]).ConfigureAwait(true);
        if (!ok) return;
        try
        {
            // Rewrite first so the file matches what the page shows.
            // The hash is of the bytes written here; the elevated stub refuses
            // to run anything else.
            var written = RouteFixScriptFile.Write(_fs.AppDataDir.FullName, report.Fix, IsRussian);
            FixScriptPath = written.Path;
            FixLogText = string.Empty;
            RoutesStatus = loc["LinkHealth.Routes.Running"];
            var exitCode = await RouteFixScriptFile.RunElevatedAsync(written, IsRussian).ConfigureAwait(true);
            if (exitCode is null)
            {
                RoutesStatus = loc["LinkHealth.Routes.Declined"];
                return;
            }
            if (!_active) return; // left the page while the script ran
            FixLogText = RouteFixScriptFile.ReadLog(FixScriptPath);
            await CheckRoutesAsync().ConfigureAwait(true);
            // After the re-check (which clears the status line) say how the script ended.
            RoutesStatus = loc[exitCode switch
            {
                0 => "LinkHealth.Routes.Result.Done",
                2 => "LinkHealth.Routes.Result.Partial",
                4 => "LinkHealth.Routes.Result.Tampered",
                _ => "LinkHealth.Routes.Result.Failed",
            }];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Running the route fix failed");
            RoutesStatus = string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.ActionFailed"], ex.Message);
        }
    }

    private static bool IsRussian => Localizer.Instance.Active == LangCode.Russian;

    private void UpdateRoutes()
    {
        var loc = Localizer.Instance;
        RouteLines.Clear();
        if (_service.LastRouting is not { } report)
        {
            RoutesSummary = string.Empty;
            HasFix = false;
            return;
        }

        var problems = 0;
        foreach (var v in report.Verdicts.OrderBy(v => v.Ip ?? v.Host, StringComparer.Ordinal))
        {
            var line = RouteLineViewModel.For(v, CameraLabel(v.Host));
            if (line.IsProblem) problems++;
            RouteLines.Add(line);
        }
        foreach (var c in report.Snapshot.Conflicts)
            RouteLines.Add(RouteLineViewModel.Conflict(c));

        RoutesSummary = problems == 0
            ? loc["LinkHealth.Routes.AllGood"]
            : string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.Problems"], problems);
        if (report.Snapshot.Conflicts.Count > 0)
            RoutesSummary += " " + string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.Notes"], report.Snapshot.Conflicts.Count);

        HasFix = report.Fix.Routes.Count > 0;
        if (HasFix)
        {
            FixScriptText = RouteFixScriptFile.Text(report.Fix, IsRussian);
            try
            {
                FixScriptPath = RouteFixScriptFile.Write(_fs.AppDataDir.FullName, report.Fix, IsRussian).Path;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write the route fix script");
                FixScriptPath = string.Empty;
            }
        }
        else
        {
            FixScriptText = string.Empty;
        }
    }

    private string CameraLabel(string host) =>
        _namesByHost.TryGetValue(host, out var name) && !string.Equals(name, host, StringComparison.OrdinalIgnoreCase)
            ? $"{name} ({host})"
            : host;

    private void OnRoutingReady(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_active) UpdateRoutes();
    });

    partial void OnAutoCheckChanged(bool value) =>
        _ = _settings.UpdateAsync(_settings.Current with { LinkHealthAutoCheck = value });

    partial void OnAutoFixChanged(bool value) =>
        _ = _settings.UpdateAsync(_settings.Current with { LinkHealthAutoFix = value });

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        // Sequential: parallel checks would load the shared segment and blur
        // whose counters moved.
        foreach (var row in Rows.ToList())
        {
            if (!_active) break;
            await row.CheckCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResetCountersAsync()
    {
        var loc = Localizer.Instance;
        var ok = await _dialogs.ConfirmAsync(
            loc["LinkHealth.ResetTitle"], loc["LinkHealth.ResetMessage"],
            loc["LinkHealth.Reset"], loc["Common.Cancel"]).ConfigureAwait(true);
        if (!ok) return;
        _journal.Reset();
        UpdateAll();
    }

    internal Task<bool> ConfirmAsync(string titleKey, string messageKey, string confirmKey, string name)
    {
        var loc = Localizer.Instance;
        return _dialogs.ConfirmAsync(
            loc[titleKey],
            string.Format(CultureInfo.CurrentCulture, loc[messageKey], name),
            loc[confirmKey], loc["Common.Cancel"]);
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        var ct = Lifetime;
        IsLoading = true;
        try
        {
            var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
            if (!_active) return;
            _names.Clear();
            _namesByHost.Clear();
            foreach (var c in cameras)
            {
                _names[c.Id] = c.Name;
                _namesByHost[c.StreamProbeTarget().Host] = c.Name;
            }

            Rows.Clear();
            foreach (var c in cameras.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                Rows.Add(new LinkHealthRowViewModel(c, this, _service, _journal, _logger));
            IsEmpty = Rows.Count == 0;
            UpdateAll();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the page left before the list arrived
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health page load failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateAll()
    {
        foreach (var r in Rows) r.Update();
        UpdateTopology();
        UpdateRoutes();
    }

    private void UpdateTopology()
    {
        var loc = Localizer.Instance;
        var report = FailureDomainAnalyzer.Analyze(_journal.Events());
        TopologyTitle = loc["LinkHealth.Topo." + report.Verdict];
        TopologyHint = loc["LinkHealth.Topo." + report.Verdict + ".Hint"];

        TopologyLines.Clear();
        if (report.Incidents > 0)
            TopologyLines.Add(string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Topo.Counts"],
                report.Incidents, report.SharedIncidents));
        foreach (var p in report.Cameras)
            TopologyLines.Add(string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Topo.Camera"],
                Name(p.CameraId), p.Drops, p.SharedDrops, p.SoloDrops));
        foreach (var inc in report.RecentShared.Take(5))
            TopologyLines.Add(string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Topo.Incident"],
                inc.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                string.Join(", ", inc.Cameras.Select(Name))));
    }

    private string Name(CameraId id) => _names.TryGetValue(id, out var n) ? n : id.Value.ToString()[..8];

    // Journal changes arrive in bursts from session threads; coalesce them into
    // one UI refresh.
    private void OnJournalChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (_active) UpdateAll();
        }, DispatcherPriority.Background);
    }

    private void OnReportReady(object? sender, LinkReportEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            Rows.FirstOrDefault(r => r.Camera.Id == e.Report.CameraId)?.Update();
            UpdateTopology();
        });

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateAll);
}

public sealed partial class LinkHealthRowViewModel : ViewModelBase
{
    private static readonly IBrush CriticalBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#60a5fa"));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#6b7280"));

    private readonly NetworkHealthPageViewModel _page;
    private readonly LinkHealthService _service;
    private readonly ConnectionJournal _journal;
    private readonly ILogger _logger;

    public Camera Camera { get; }
    public string Name => Camera.Name;
    public string Host => Camera.Host;

    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<MetricViewModel> Metrics { get; } = new();

    [ObservableProperty] private string _countersText = string.Empty;
    [ObservableProperty] private string _lastDropText = string.Empty;
    [ObservableProperty] private string _lastCheckText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private IBrush _statusBrush = UnknownBrush;
    [ObservableProperty] private string _actionText = string.Empty;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _isInOutage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(StressCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartStreamerCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSlowLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetAutoLinkCommand))]
    private bool _isBusy;

    public LinkHealthRowViewModel(
        Camera camera, NetworkHealthPageViewModel page, LinkHealthService service, ConnectionJournal journal, ILogger logger)
    {
        Camera = camera;
        _page = page;
        _service = service;
        _journal = journal;
        _logger = logger;
        _isBusy = service.IsRunning(camera.Id);
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CheckAsync() => RunAsync(LinkCheckTrigger.Manual, "LinkHealth.Checking");

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task StressAsync() => RunAsync(LinkCheckTrigger.Stress, "LinkHealth.Stressing");

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RestartStreamerAsync()
    {
        if (!await _page.ConfirmAsync("LinkHealth.Fix.Restart", "LinkHealth.Fix.RestartConfirm", "LinkHealth.Fix.RestartGo", Name)
                .ConfigureAwait(true))
            return;
        await ActAsync(async () =>
        {
            await _service.RestartStreamerAsync(Camera, _page.Lifetime).ConfigureAwait(true);
            _journal.Record(Camera.Id, ConnectionEventKind.AutoFix, "RestartStreamer:manual");
            return Localizer.Instance["LinkHealth.Fix.RestartDone"];
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SetSlowLinkAsync()
    {
        if (!await _page.ConfirmAsync("LinkHealth.Fix.Slow", "LinkHealth.Fix.SlowConfirm", "LinkHealth.Fix.SlowGo", Name)
                .ConfigureAwait(true))
            return;
        await ActAsync(async () =>
        {
            var ok = await _service.SetLinkModeAsync(Camera, CameraLinkMode.Force10Full, _page.Lifetime).ConfigureAwait(true);
            if (ok) _journal.Record(Camera.Id, ConnectionEventKind.AutoFix, "LinkMode:10Full");
            return Localizer.Instance[ok ? "LinkHealth.Fix.SlowDone" : "LinkHealth.Fix.NoEthtool"];
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SetAutoLinkAsync()
    {
        await ActAsync(async () =>
        {
            var ok = await _service.SetLinkModeAsync(Camera, CameraLinkMode.Auto, _page.Lifetime).ConfigureAwait(true);
            if (ok) _journal.Record(Camera.Id, ConnectionEventKind.AutoFix, "LinkMode:Auto");
            return Localizer.Instance[ok ? "LinkHealth.Fix.AutoDone" : "LinkHealth.Fix.NoEthtool"];
        }).ConfigureAwait(true);
    }

    private async Task RunAsync(LinkCheckTrigger trigger, string busyKey)
    {
        await ActAsync(async () =>
        {
            await _service.RunCheckAsync(Camera, trigger, _page.Lifetime).ConfigureAwait(true);
            return string.Empty;
        }, busyKey).ConfigureAwait(true);
        if (_page.IsActive) Update();
    }

    private async Task ActAsync(Func<Task<string>> action, string busyKey = "LinkHealth.Working")
    {
        if (!_page.IsActive) return;
        IsBusy = true;
        ActionText = Localizer.Instance[busyKey];
        try
        {
            ActionText = await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_page.IsActive)
        {
            ActionText = string.Empty; // the page left; nothing to report
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health action failed for {Camera}", Name);
            ActionText = ex.Message == LinkProbeErrors.NoCredentials
                ? Localizer.Instance["LinkHealth.NoCredentials"]
                : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["LinkHealth.ActionFailed"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Update()
    {
        var loc = Localizer.Instance;
        var c = _journal.Get(Camera.Id);
        IsInOutage = _journal.IsInOutage(Camera.Id);
        CountersText = string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Counters"],
            c.Connects, c.Drops, c.FailedAttempts, c.ConsecutiveFailures);
        LastDropText = c.LastDropAt is { } drop
            ? string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.LastDrop"],
                drop.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                c.LastOutage is { } o ? FormatDuration(o) : "—")
            : loc["LinkHealth.NoDrops"];

        var report = _service.LastReport(Camera.Id);
        HasReport = report is not null;
        Findings.Clear();
        Metrics.Clear();
        if (report is null)
        {
            StatusText = IsInOutage ? loc["LinkHealth.Status.Down"] : loc["LinkHealth.Status.NotChecked"];
            StatusBrush = IsInOutage ? CriticalBrush : UnknownBrush;
            LastCheckText = string.Empty;
            return;
        }

        LastCheckText = string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.LastCheck"],
            report.At.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), loc["LinkHealth.Trigger." + report.Trigger]);
        (StatusText, StatusBrush) = report.Worst switch
        {
            FindingSeverity.Critical => (loc["LinkHealth.Status.Critical"], CriticalBrush),
            FindingSeverity.Warning => (loc["LinkHealth.Status.Warning"], WarningBrush),
            _ => (loc["LinkHealth.Status.Ok"], OkBrush),
        };

        foreach (var f in report.Findings)
            Findings.Add(new FindingViewModel(f, f.Severity switch
            {
                FindingSeverity.Critical => CriticalBrush,
                FindingSeverity.Warning => WarningBrush,
                FindingSeverity.Ok => OkBrush,
                _ => InfoBrush,
            }));
        BuildMetrics(report);
    }

    private void BuildMetrics(LinkCheckReport r)
    {
        var loc = Localizer.Instance;
        void Add(string key, string value) => Metrics.Add(new MetricViewModel(loc[key], value));

        Add("LinkHealth.M.Port", r.StreamPortReachable ? loc["LinkHealth.Yes"] : loc["LinkHealth.No"]);
        if (r.SmallPing is { } sp)
            Add("LinkHealth.M.Ping", PingText(sp));
        else
            Add("LinkHealth.M.Ping", loc["LinkHealth.NoIcmp"]);
        if (r.LargePing is { } lp)
            Add("LinkHealth.M.PingLarge", PingText(lp));

        if (r.Camera is { } cam)
        {
            Add("LinkHealth.M.Link", cam.Carrier == false
                ? loc["LinkHealth.LinkDown"]
                : $"{cam.SpeedMbps?.ToString(CultureInfo.CurrentCulture) ?? "?"} {loc["LinkHealth.Mbps"]} · {cam.Duplex ?? "?"}");
            if (cam.UptimeSeconds is { } up)
                Add("LinkHealth.M.Uptime", FormatDuration(TimeSpan.FromSeconds(up)));
            if (cam.LinkFlapsSinceBoot is { } flaps)
                Add("LinkHealth.M.Flaps", flaps.ToString(CultureInfo.CurrentCulture));
            Add("LinkHealth.M.Crc", cam.PhysicalErrors.ToString(CultureInfo.CurrentCulture));
            var port = StreamPort();
            Add("LinkHealth.M.Sessions", string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.SessionsValue"],
                LinkHealthAnalyzer.TotalRtspSessions(cam, port),
                LinkHealthAnalyzer.OwnRtspSessions(cam, port, r.Host?.LocalAddress)));
        }
        else if (r.CameraProbeError is { } err)
        {
            Add("LinkHealth.M.CameraSide", err == LinkProbeErrors.NoCredentials ? loc["LinkHealth.NoCredentials"] : err);
        }

        if (r.Path is { } path)
        {
            Add("LinkHealth.M.Route", RouteLineViewModel.RouteText(path));
            Add("LinkHealth.M.Arp", RouteLineViewModel.ArpText(path));
        }

        if (r.Host is { } h)
            Add("LinkHealth.M.Host", $"{h.Name} · {h.SpeedMbps?.ToString(CultureInfo.CurrentCulture) ?? "?"} {loc["LinkHealth.Mbps"]}"
                                     + $" · {loc["LinkHealth.Errors"]} {(h.ReceivedErrors ?? 0) + (h.SentErrors ?? 0)}");

        if (r.Stress is { } st)
        {
            Add("LinkHealth.M.StressLoad", PingText(st.Load) + $" · {st.Load.Sent} {loc["LinkHealth.Packets"]}");
            if (st.CameraPhysicalErrorsDelta is { } d)
                Add("LinkHealth.M.StressCrc", "+" + d.ToString(CultureInfo.CurrentCulture));
        }
    }

    private int StreamPort() => Camera.StreamProbeTarget().Port;

    private static string PingText(PingStats p)
    {
        var loss = p.LossPercent.ToString("0.#", CultureInfo.CurrentCulture);
        return p.AvgMs is { } avg
            ? $"{loss}% · {avg.ToString("0.#", CultureInfo.CurrentCulture)} / {p.MaxMs?.ToString("0", CultureInfo.CurrentCulture)} ms"
            : $"{loss}%";
    }

    internal static string FormatDuration(TimeSpan t)
    {
        var loc = Localizer.Instance;
        if (t.TotalDays >= 1)
            return string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Dur.Days"], (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1)
            return string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Dur.Hours"], (int)t.TotalHours, t.Minutes);
        if (t.TotalMinutes >= 1)
            return string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Dur.Minutes"], (int)t.TotalMinutes, t.Seconds);
        return string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Dur.Seconds"], (int)t.TotalSeconds);
    }
}

public sealed class FindingViewModel
{
    public FindingViewModel(LinkFinding finding, IBrush brush)
    {
        var loc = Localizer.Instance;
        var args = finding.Args
            .Select(a => a == LinkProbeErrors.NoCredentials ? loc["LinkHealth.NoCredentials"] : a)
            .Cast<object>()
            .ToArray();
        Title = Format(loc["LinkHealth.F." + finding.Code], args);
        Hint = Format(loc["LinkHealth.F." + finding.Code + ".Hint"], args);
        Brush = brush;
        HasHint = !Hint.StartsWith("LinkHealth.", StringComparison.Ordinal) && Hint.Length > 0;
    }

    public string Title { get; }
    public string Hint { get; }
    public bool HasHint { get; }
    public IBrush Brush { get; }

    private static string Format(string template, object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}

public sealed record MetricViewModel(string Label, string Value);

public sealed class RouteLineViewModel
{
    private static readonly IBrush CriticalBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#60a5fa"));

    private RouteLineViewModel(string title, string detail, IBrush brush, bool isProblem)
    {
        Title = title;
        Detail = detail;
        Brush = brush;
        IsProblem = isProblem;
    }

    public string Title { get; }
    public string Detail { get; }
    public IBrush Brush { get; }
    public bool IsProblem { get; }
    public bool HasDetail => Detail.Length > 0;

    public static RouteLineViewModel For(CameraPathVerdict v, string label)
    {
        var loc = Localizer.Instance;
        string key;
        IBrush brush;
        var problem = true;
        if (v.Ip is null)
        {
            key = "LinkHealth.Routes.Unresolved";
            brush = WarningBrush;
        }
        else if (v.SubnetInterfaces.Count == 0)
        {
            key = "LinkHealth.Routes.ViaGateway";
            brush = InfoBrush;
            problem = false;
        }
        else if (v.WrongInterface)
        {
            key = v.Evidence == PathEvidence.Neighbors ? "LinkHealth.Routes.WrongInferred" : "LinkHealth.Routes.Wrong";
            brush = CriticalBrush;
        }
        else if (v.LivesOn is null)
        {
            key = "LinkHealth.Routes.NotFound";
            brush = WarningBrush;
        }
        else if (v.Pinned)
        {
            key = "LinkHealth.Routes.Pinned";
            brush = OkBrush;
            problem = false;
        }
        else if (v.SharedSubnet)
        {
            key = "LinkHealth.Routes.Fragile";
            brush = WarningBrush;
        }
        else
        {
            key = "LinkHealth.Routes.Ok";
            brush = OkBrush;
            problem = false;
        }

        var title = string.Format(CultureInfo.CurrentCulture, loc[key], label, v.RoutedVia ?? "?", v.LivesOn ?? "?");
        var detail = v.PerInterface.Count == 0 ? string.Empty : ArpText(v) + " · " + StreamText(v);
        if (v.IpOnSeveralNetworks)
        {
            detail += " · " + loc["LinkHealth.Routes.SeveralNetworks"];
            if (ReferenceEquals(brush, OkBrush)) brush = WarningBrush;
            problem = true;
        }
        return new RouteLineViewModel(title, detail, brush, problem);
    }

    // A note, not a problem: the two networks don't touch, so nothing collides
    // until they are joined (see LinkHealthAnalyzer.AnalyzePath).
    public static RouteLineViewModel Conflict(AddressConflict c)
    {
        var loc = Localizer.Instance;
        return new RouteLineViewModel(
            string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.Conflict"], c.Address, c.OwnInterface, c.ForeignMac, c.SeenOnInterface),
            loc["LinkHealth.Routes.ConflictHint"],
            InfoBrush,
            false);
    }

    public static string RouteText(CameraPathVerdict v)
    {
        var loc = Localizer.Instance;
        if (v.RoutedVia is null) return "?";
        if (v.Pinned && v.RoutedVia == v.LivesOn)
            return string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.ViaPinned"], v.RoutedVia);
        return v.LivesOn is null || v.LivesOn == v.RoutedVia
            ? string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.Via"], v.RoutedVia)
            : string.Format(CultureInfo.CurrentCulture, loc["LinkHealth.Routes.ViaButOn"], v.RoutedVia, v.LivesOn);
    }

    public static string ArpText(CameraPathVerdict v)
    {
        var loc = Localizer.Instance;
        if (v.PerInterface.Count == 0) return "—";
        return "ARP: " + string.Join(", ", v.PerInterface.Select(p =>
            $"{p.InterfaceName} {p.ArpMac ?? loc["LinkHealth.Routes.NoAnswer"]}"));
    }

    private static string StreamText(CameraPathVerdict v)
    {
        var loc = Localizer.Instance;
        return loc["LinkHealth.Routes.Port"] + ": " + string.Join(", ", v.PerInterface.Select(p =>
            $"{p.InterfaceName} {Answer(p.StreamReachable, loc)}"));
    }

    private static string Answer(bool? value, Localizer loc) => value switch
    {
        true => loc["LinkHealth.Yes"],
        false => loc["LinkHealth.No"],
        _ => "?",
    };
}
