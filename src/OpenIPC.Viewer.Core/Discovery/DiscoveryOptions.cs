using System;

namespace OpenIPC.Viewer.Core.Discovery;

// Knobs for a discovery run. DeepScan gates the active subnet sweep (Slice C),
// which is opt-in because it looks like a port scan on the network.
public sealed record DiscoveryOptions(
    TimeSpan Timeout,
    bool DeepScan = false);
