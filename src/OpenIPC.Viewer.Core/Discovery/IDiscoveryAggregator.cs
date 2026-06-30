using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenIPC.Viewer.Core.Discovery;

// The aggregated discovery pipeline as seen by the UI. Runs every enabled source
// and yields merged-by-host devices as their signal sets grow (consumers upsert
// by Host). Reports 0..1 progress. The implementation lives in Devices (it does
// socket IO and uses Channels); App depends only on this Core contract.
public interface IDiscoveryAggregator
{
    IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, CancellationToken ct);
}
