using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

// Automatic side of the Health page. After ConnectionJournal.FailureThreshold
// failed connect attempts in a row it runs a one-off check (what is reachable
// while the camera is down), and once the camera streams again a second one
// (the camera's uptime and link-flap counters, which only answer once it is
// back, tell a power loss from a broken data link). With AutoFix on, a check
// that finds leftover RTSP sessions from this computer, or a streamer that no
// longer answers on its port while the camera itself does, restarts the
// streamer. Both are rate-limited per camera so a flapping link can't turn
// into a storm of SSH logins or restarts.
public sealed class LinkHealthMonitor : IDisposable
{
    private static readonly TimeSpan CheckCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FixCooldown = TimeSpan.FromMinutes(30);
    // Give a camera that just came back a moment to finish booting its services.
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(15);

    private readonly ConnectionJournal _journal;
    private readonly LinkHealthService _service;
    private readonly CameraDirectoryService _directory;
    private readonly UserSettingsService _settings;
    private readonly ILogger<LinkHealthMonitor> _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _gate = new();
    private readonly Dictionary<(CameraId, LinkCheckTrigger), DateTimeOffset> _lastCheck = new();
    private readonly Dictionary<CameraId, DateTimeOffset> _lastFix = new();

    public LinkHealthMonitor(
        ConnectionJournal journal,
        LinkHealthService service,
        CameraDirectoryService directory,
        UserSettingsService settings,
        LinkHealthStore store,
        ILogger<LinkHealthMonitor> logger)
    {
        _ = store; // resolved for its side effect: the journal is loaded before anything reports
        _journal = journal;
        _service = service;
        _directory = directory;
        _settings = settings;
        _logger = logger;
        _journal.ThresholdReached += OnThresholdReached;
        _journal.Recovered += OnRecovered;
    }

    public void Dispose()
    {
        _journal.ThresholdReached -= OnThresholdReached;
        _journal.Recovered -= OnRecovered;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnThresholdReached(object? sender, ConnectionThresholdEventArgs e) =>
        _ = RunAsync(e.CameraId, LinkCheckTrigger.AfterFailures, TimeSpan.Zero);

    private void OnRecovered(object? sender, ConnectionRecoveredEventArgs e) =>
        _ = RunAsync(e.CameraId, LinkCheckTrigger.AfterRecovery, RecoveryDelay);

    private async Task RunAsync(CameraId id, LinkCheckTrigger trigger, TimeSpan delay)
    {
        if (!_settings.Current.LinkHealthAutoCheck) return;
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_lastCheck.TryGetValue((id, trigger), out var last) && now - last < CheckCooldown)
                return;
            _lastCheck[(id, trigger)] = now;
        }

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
            var camera = await _directory.GetAsync(id, _cts.Token).ConfigureAwait(false);
            if (camera is null) return;

            LinkCheckReport report;
            try
            {
                report = await _service.RunCheckAsync(camera, trigger, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                // Joined a check the Health page started and then cancelled by
                // leaving; that run is gone, so start our own.
                report = await _service.RunCheckAsync(camera, trigger, _cts.Token).ConfigureAwait(false);
            }
            var worst = report.Findings.FirstOrDefault(f => f.Severity >= FindingSeverity.Warning);
            _journal.Record(id, ConnectionEventKind.AutoCheck,
                $"{trigger}:{worst?.Code ?? "Ok"}");
            _logger.LogInformation("Link check ({Trigger}) for {Camera}: {Findings}",
                trigger, camera.Name, string.Join(", ", report.Findings.Select(f => f.Code)));

            if (_settings.Current.LinkHealthAutoFix && NeedsStreamerRestart(report))
                await TryAutoFixAsync(camera, report).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // app shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic link check failed for {Camera}", id);
        }
    }

    /// <summary>
    /// The two cases a streamer restart actually fixes: leftover RTSP sessions
    /// from this computer, or the camera answers (ping/SSH) while its stream
    /// port does not.
    /// </summary>
    public static bool NeedsStreamerRestart(LinkCheckReport report) =>
        report.Findings.Any(f => f.Code is "ExcessOwnSessions" or "StreamPortClosed");

    private async Task TryAutoFixAsync(Camera camera, LinkCheckReport report)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_lastFix.TryGetValue(camera.Id, out var last) && now - last < FixCooldown)
                return;
            _lastFix[camera.Id] = now;
        }
        var reason = report.Findings.First(f => f.Code is "ExcessOwnSessions" or "StreamPortClosed").Code;
        try
        {
            await _service.RestartStreamerAsync(camera, _cts.Token).ConfigureAwait(false);
            _journal.Record(camera.Id, ConnectionEventKind.AutoFix, "RestartStreamer:" + reason);
            _logger.LogInformation("Auto-fix: restarted the streamer on {Camera} ({Reason})", camera.Name, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _journal.Record(camera.Id, ConnectionEventKind.AutoFix, "Failed:" + ex.Message);
            _logger.LogWarning(ex, "Auto-fix (streamer restart) failed on {Camera}", camera.Name);
        }
    }
}
