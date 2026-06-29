using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenIPC.Viewer.Core.Discovery;

// A device found on the LAN, possibly by several sources. Each source emits a
// partial signal (whatever it knows); the aggregator merges signals for the same
// Host into one of these via MergeWith. Confidence is derived, not stored.
public sealed record DiscoveredDevice(
    string Host,
    DiscoveryProtocol Protocols,
    IReadOnlyCollection<int> Ports,
    string? Name = null,
    string? Model = null,
    // Present only when ONVIF was found — the existing probe path needs it.
    Uri? OnvifServiceUri = null)
{
    public DiscoveryConfidence Confidence
    {
        get
        {
            // A positive app-layer fingerprint means we actually identified it.
            if (Protocols.HasFlag(DiscoveryProtocol.Majestic) || Protocols.HasFlag(DiscoveryProtocol.Onvif))
                return DiscoveryConfidence.High;
            return ProtocolCount(Protocols) >= 2 ? DiscoveryConfidence.Medium : DiscoveryConfidence.Low;
        }
    }

    // Fold another signal for the SAME host into this one: union protocols/ports,
    // keep the first non-null label, prefer an ONVIF URI once any source has it.
    public DiscoveredDevice MergeWith(DiscoveredDevice other) => this with
    {
        Protocols = Protocols | other.Protocols,
        Ports = Ports.Concat(other.Ports).Distinct().OrderBy(p => p).ToArray(),
        Name = Name ?? other.Name,
        Model = Model ?? other.Model,
        OnvifServiceUri = OnvifServiceUri ?? other.OnvifServiceUri,
    };

    private static int ProtocolCount(DiscoveryProtocol p)
    {
        var count = 0;
        var bits = (int)p;
        while (bits != 0) { bits &= bits - 1; count++; }
        return count;
    }
}
