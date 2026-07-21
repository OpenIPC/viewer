using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.Web.Api;

// Discovery runs for seconds and dribbles results out; HTTP wants an answer now.
// So a scan is a background job: POST starts one and gets an id, GET polls it.
//
// Polling rather than SSE/WebSocket on purpose — the result set is tiny, a
// reloaded page can pick the run back up by id, and there's no socket lifetime
// to reason about. The store keeps the last few runs so a viewer who navigates
// away and back still sees what was found.
public sealed class DiscoveryScanStore
{
    private const int MaxRetainedScans = 5;
    private static readonly TimeSpan RetainFor = TimeSpan.FromMinutes(10);

    private readonly IDiscoveryAggregator _aggregator;
    private readonly ILogger<DiscoveryScanStore> _logger;
    private readonly ConcurrentDictionary<string, DiscoveryScan> _scans = new(StringComparer.Ordinal);

    public DiscoveryScanStore(IDiscoveryAggregator aggregator, ILogger<DiscoveryScanStore> logger)
    {
        _aggregator = aggregator;
        _logger = logger;
    }

    public bool HasRunningScan => _scans.Values.Any(s => s.IsRunning);

    public DiscoveryScan Start(DiscoveryOptions options)
    {
        Prune();
        var scan = new DiscoveryScan(Guid.NewGuid().ToString("n"));
        _scans[scan.Id] = scan;
        _ = RunAsync(scan, options);
        return scan;
    }

    public DiscoveryScan? Get(string id) => _scans.TryGetValue(id, out var scan) ? scan : null;

    private async Task RunAsync(DiscoveryScan scan, DiscoveryOptions options)
    {
        var progress = new Progress<double>(p => scan.Progress = p);
        try
        {
            await foreach (var device in _aggregator.ScanAsync(options, progress, scan.Token))
                scan.Upsert(device);
            scan.Finish(scan.Token.IsCancellationRequested ? "cancelled" : "done");
        }
        catch (OperationCanceledException)
        {
            scan.Finish("cancelled");
        }
        catch (Exception ex)
        {
            // A broken source (no usable interface, socket denied) must not take
            // the server down with it — the run just ends with an error state.
            _logger.LogWarning(ex, "Discovery scan {Id} failed", scan.Id);
            scan.Finish("failed", ex.Message);
        }
    }

    // Drop finished runs once they age out, and never keep more than a handful.
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, scan) in _scans.ToArray())
        {
            if (!scan.IsRunning && scan.FinishedAt is { } finished && now - finished > RetainFor)
                Remove(id);
        }
        foreach (var (id, _) in _scans
            .Where(kv => !kv.Value.IsRunning)
            .OrderBy(kv => kv.Value.StartedAt)
            .Take(Math.Max(0, _scans.Count - MaxRetainedScans + 1))
            .ToArray())
        {
            Remove(id);
        }
    }

    private void Remove(string id)
    {
        if (_scans.TryRemove(id, out var scan))
            scan.Dispose();
    }
}

// One scan run: its cancellation, its progress, and the devices found so far
// (merged by host, exactly like the desktop dialog's row upsert).
public sealed class DiscoveryScan : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredDevice> _byHost =
        new(StringComparer.OrdinalIgnoreCase);

    public DiscoveryScan(string id)
    {
        Id = id;
        StartedAt = DateTimeOffset.UtcNow;
        Status = "running";
    }

    public string Id { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string Status { get; private set; }
    public string? Error { get; private set; }
    public double Progress { get; set; }
    public bool IsRunning => Status == "running";
    public CancellationToken Token => _cts.Token;

    public IReadOnlyList<DiscoveredDevice> Devices
    {
        get
        {
            lock (_gate)
                return _byHost.Values.OrderBy(d => d.Host, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // A host can be yielded repeatedly as more sources confirm it; fold the new
    // signal into what we already know instead of listing it twice.
    public void Upsert(DiscoveredDevice device)
    {
        lock (_gate)
        {
            _byHost[device.Host] = _byHost.TryGetValue(device.Host, out var existing)
                ? existing.MergeWith(device)
                : device;
        }
    }

    public void Cancel() => _cts.Cancel();

    public void Finish(string status, string? error = null)
    {
        Status = status;
        Error = error;
        Progress = 1;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
