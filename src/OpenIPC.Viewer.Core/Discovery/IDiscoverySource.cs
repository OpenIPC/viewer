using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenIPC.Viewer.Core.Discovery;

// One way of finding cameras (ONVIF WS-Discovery, mDNS, subnet sweep, …). The
// aggregator runs all applicable sources concurrently and merges their output.
// A source emits whatever partial signal it learns about a host; it may emit the
// same host more than once. Sources with a deterministic extent (the sweep)
// report 0..1 progress; passive ones may leave it null.
public interface IDiscoverySource
{
    // Human-facing label for logging / UI ("ONVIF", "Subnet sweep", "mDNS").
    string Name { get; }

    // Whether this source runs under the given options (e.g. the sweep only runs
    // when DeepScan is on). Lets the aggregator skip it without instantiating IO.
    bool IsEnabled(DiscoveryOptions options);

    IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, CancellationToken ct);
}
