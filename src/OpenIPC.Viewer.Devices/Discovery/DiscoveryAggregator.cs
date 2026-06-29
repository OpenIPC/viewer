using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery;

// Runs every enabled discovery source concurrently, fans their signals into one
// channel, and merges by host so a camera found by several sources is one device
// that grows as more signals arrive. Yields the current merged device each time
// its host's signal set changes (consumers upsert by Host). Aggregate progress
// is the mean of the per-source progress.
//
// Lives in Devices (not Core) because Core is netstandard2.1 with no package
// deps — System.Threading.Channels needs the net9.0 BCL.
public sealed class DiscoveryAggregator
{
    private readonly IReadOnlyList<IDiscoverySource> _sources;
    private readonly ILogger<DiscoveryAggregator> _logger;

    public DiscoveryAggregator(IEnumerable<IDiscoverySource> sources, ILogger<DiscoveryAggregator> logger)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var active = _sources.Where(s => s.IsEnabled(options)).ToList();
        if (active.Count == 0)
        {
            progress?.Report(1.0);
            yield break;
        }

        var channel = Channel.CreateUnbounded<DiscoveredDevice>();
        var perSource = new double[active.Count];

        void ReportAggregate()
        {
            if (progress is null) return;
            double mean;
            lock (perSource) mean = perSource.Sum() / active.Count;
            progress.Report(Math.Min(1.0, mean));
        }

        var tasks = new List<Task>(active.Count);
        for (var i = 0; i < active.Count; i++)
        {
            var idx = i;
            var source = active[i];
            var sourceProgress = new Progress<double>(v =>
            {
                lock (perSource) perSource[idx] = Math.Clamp(v, 0.0, 1.0);
                ReportAggregate();
            });

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var device in source.ScanAsync(options, sourceProgress, ct).ConfigureAwait(false))
                        await channel.Writer.WriteAsync(device, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* expected on cancel */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Discovery source {Source} failed", source.Name);
                }
                finally
                {
                    lock (perSource) perSource[idx] = 1.0;
                    ReportAggregate();
                }
            }, ct));
        }

        // Complete the channel once every source has finished. Detached from the
        // reader so a slow consumer never deadlocks the producers.
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        var merged = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        await foreach (var signal in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var current = merged.TryGetValue(signal.Host, out var existing)
                ? existing.MergeWith(signal)
                : signal;
            merged[signal.Host] = current;
            yield return current;
        }

        progress?.Report(1.0);
    }
}
