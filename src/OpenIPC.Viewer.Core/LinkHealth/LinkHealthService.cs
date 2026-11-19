using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>Raised when a check finishes.</summary>
public sealed class LinkReportEventArgs : EventArgs
{
    public LinkReportEventArgs(LinkCheckReport report) => Report = report;
    public LinkCheckReport Report { get; }
}

/// <summary>Error text used when a camera has no SSH login stored.</summary>
public static class LinkProbeErrors
{
    public const string NoCredentials = "no-credentials";
}

// Runs one link-health check against a camera: TCP to the stream port, a small
// and a full-size ICMP series, this computer's adapter, and the camera's own
// interface counters over SSH — optionally under load (stress). One check per
// camera at a time; a second request joins the running one.
public sealed class LinkHealthService
{
    public const int SmallPayload = 32;
    // 1472 bytes of ICMP payload = a full 1500-byte Ethernet frame.
    public const int LargePayload = 1472;
    public static readonly TimeSpan StressDuration = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(12);
    // A camera check reuses a fleet routing report this fresh ("check all" runs
    // one probe of every adapter, not one per camera).
    private static readonly TimeSpan RoutingReuse = TimeSpan.FromSeconds(20);

    private readonly CameraDirectoryService _directory;
    private readonly IReachabilityProbe _reachability;
    private readonly IPingProbe _ping;
    private readonly IHostLinkProbe _host;
    private readonly ICameraLinkClient _camera;
    private readonly IHostRoutingProbe _routing;
    private readonly ConnectionJournal _journal;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private readonly Dictionary<CameraId, Task<LinkCheckReport>> _running = new();
    private readonly Dictionary<CameraId, LinkCheckReport> _last = new();
    private Task<RoutingReport>? _routingRunning;
    private RoutingReport? _lastRouting;

    public LinkHealthService(
        CameraDirectoryService directory,
        IReachabilityProbe reachability,
        IPingProbe ping,
        IHostLinkProbe host,
        ICameraLinkClient camera,
        IHostRoutingProbe routing,
        ConnectionJournal journal,
        Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _reachability = reachability;
        _ping = ping;
        _host = host;
        _camera = camera;
        _routing = routing;
        _journal = journal;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event EventHandler<LinkReportEventArgs>? ReportReady;

    /// <summary>A fleet routing check finished.</summary>
    public event EventHandler? RoutingReady;

    public RoutingReport? LastRouting
    {
        get { lock (_gate) return _lastRouting; }
    }

    public bool IsRoutingRunning
    {
        get { lock (_gate) return _routingRunning is not null; }
    }

    /// <summary>
    /// Locates every camera on this computer's adapters: where the OS sends its
    /// packets, where it answers ARP / its stream port, and which of our own
    /// addresses another device answers for. Joins a check that is running.
    /// </summary>
    public Task<RoutingReport> CheckRoutingAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_routingRunning is { } running)
                return running;
            var task = RoutingCoreAsync(ct);
            _routingRunning = task;
            return task;
        }
    }

    public LinkCheckReport? LastReport(CameraId id)
    {
        lock (_gate)
            return _last.TryGetValue(id, out var r) ? r : null;
    }

    public bool IsRunning(CameraId id)
    {
        lock (_gate)
            return _running.ContainsKey(id);
    }

    public Task<LinkCheckReport> RunCheckAsync(Camera camera, LinkCheckTrigger trigger, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(camera.Id, out var existing))
                return existing;
            var task = RunCoreAsync(camera, trigger, ct);
            _running[camera.Id] = task;
            return task;
        }
    }

    public async Task RestartStreamerAsync(Camera camera, CancellationToken ct)
    {
        var endpoint = await RequireEndpointAsync(camera, ct).ConfigureAwait(false);
        await _camera.RestartStreamerAsync(endpoint, ct).ConfigureAwait(false);
    }

    public async Task<bool> SetLinkModeAsync(Camera camera, CameraLinkMode mode, CancellationToken ct)
    {
        var endpoint = await RequireEndpointAsync(camera, ct).ConfigureAwait(false);
        return await _camera.SetLinkModeAsync(endpoint, mode, ct).ConfigureAwait(false);
    }

    private async Task<LinkCheckReport> RunCoreAsync(Camera camera, LinkCheckTrigger trigger, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            var stress = trigger == LinkCheckTrigger.Stress;
            var (host, port) = camera.StreamProbeTarget();

            var routingTask = SafeAsync<RoutingReport?>(
                async () => await RoutingForCheckAsync(ct).ConfigureAwait(false), null, ct);
            var portTask = SafeAsync(() => _reachability.IsReachableAsync(host, port, PortTimeout, ct), false, ct);
            var smallTask = SafeAsync(() => _ping.PingAsync(host, 20, SmallPayload, TimeSpan.FromMilliseconds(100), ct), null, ct);
            await Task.WhenAll(routingTask, portTask, smallTask).ConfigureAwait(false);

            // The near-end adapter is the one the camera is on — not necessarily
            // the one the OS currently (mis)routes it to.
            var routing = routingTask.Result;
            var path = routing?.Verdicts.FirstOrDefault(v => v.Host == host);
            var localAddress = path?.LivesOnLocalAddress;
            var hostTask = SafeAsync(() => _host.GetAsync(host, localAddress, ct), null, ct);
            await hostTask.ConfigureAwait(false);

            var large = await SafeAsync(() => _ping.PingAsync(host, 20, LargePayload, TimeSpan.FromMilliseconds(50), ct), null, ct)
                .ConfigureAwait(false);

            var (cameraBefore, probeError, endpoint) = await ReadCameraAsync(camera, ct).ConfigureAwait(false);

            StressResult? stressResult = null;
            if (stress && smallTask.Result is { Received: > 0 })
            {
                var load = await SafeAsync(() => _ping.FloodAsync(host, LargePayload, StressDuration, 4, ct), null, ct)
                    .ConfigureAwait(false);
                CameraLinkSnapshot? cameraAfter = null;
                if (endpoint is not null)
                    cameraAfter = await SafeAsync<CameraLinkSnapshot?>(
                        async () => await ReadWithTimeoutAsync(endpoint, ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
                var hostAfter = await SafeAsync(() => _host.GetAsync(host, localAddress, ct), null, ct).ConfigureAwait(false);

                stressResult = new StressResult(
                    StressDuration,
                    load ?? new PingStats(LargePayload, 0, 0, null, null, null, null),
                    Delta(cameraBefore?.PhysicalErrors, cameraAfter?.PhysicalErrors),
                    Delta(cameraBefore?.RxErrors, cameraAfter?.RxErrors),
                    Delta(cameraBefore?.LinkFlapsSinceBoot, cameraAfter?.LinkFlapsSinceBoot),
                    Delta(hostTask.Result?.ReceivedErrors, hostAfter?.ReceivedErrors));
            }

            var now = _clock();
            var findings = LinkHealthAnalyzer.Analyze(new LinkAnalysisInput(
                now, portTask.Result, port, smallTask.Result, large, hostTask.Result,
                cameraBefore, probeError, stressResult, _journal.Get(camera.Id),
                path, routing?.Snapshot.Conflicts));

            var report = new LinkCheckReport(
                camera.Id, now, trigger, portTask.Result, smallTask.Result, large, hostTask.Result,
                cameraBefore, probeError, stressResult, findings, path);

            lock (_gate)
                _last[camera.Id] = report;
            ReportReady?.Invoke(this, new LinkReportEventArgs(report));
            return report;
        }
        finally
        {
            lock (_gate)
                _running.Remove(camera.Id);
        }
    }

    private async Task<RoutingReport> RoutingForCheckAsync(CancellationToken ct)
    {
        var last = LastRouting;
        if (last is not null && _clock() - last.At < RoutingReuse)
            return last;
        return await CheckRoutingAsync(ct).ConfigureAwait(false);
    }

    private async Task<RoutingReport> RoutingCoreAsync(CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            var cameras = await _directory.ListAsync(ct).ConfigureAwait(false);
            var targets = cameras
                .Select(c => c.StreamProbeTarget())
                .Select(t => new RouteTarget(t.Host, t.Port))
                .Distinct()
                .ToList();
            var snapshot = await _routing.ProbeAsync(targets, ct).ConfigureAwait(false);
            var verdicts = HostRoutingAnalyzer.Analyze(snapshot);
            var report = new RoutingReport(_clock(), snapshot, verdicts, HostRoutingAnalyzer.Plan(verdicts, snapshot.Conflicts));
            lock (_gate)
                _lastRouting = report;
            RoutingReady?.Invoke(this, EventArgs.Empty);
            return report;
        }
        finally
        {
            lock (_gate)
                _routingRunning = null;
        }
    }

    private async Task<(CameraLinkSnapshot? Snapshot, string? Error, SshEndpoint? Endpoint)> ReadCameraAsync(
        Camera camera, CancellationToken ct)
    {
        SshEndpoint? endpoint;
        try
        {
            endpoint = await _directory.GetSshEndpointAsync(camera, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex.Message, null);
        }
        if (endpoint is null)
            return (null, LinkProbeErrors.NoCredentials, null);

        try
        {
            return (await ReadWithTimeoutAsync(endpoint, ct).ConfigureAwait(false), null, endpoint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "SSH timeout", endpoint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message, endpoint);
        }
    }

    private async Task<CameraLinkSnapshot> ReadWithTimeoutAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SshTimeout);
        return await _camera.ReadAsync(endpoint, cts.Token).ConfigureAwait(false);
    }

    private async Task<SshEndpoint> RequireEndpointAsync(Camera camera, CancellationToken ct) =>
        await _directory.GetSshEndpointAsync(camera, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException(LinkProbeErrors.NoCredentials);

    private static long? Delta(long? before, long? after) =>
        before is { } b && after is { } a ? Math.Max(0, a - b) : null;

    // A failing probe is "no data" for that probe, never a failed check.
    private static async Task<T> SafeAsync<T>(Func<Task<T>> run, T fallback, CancellationToken ct)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
