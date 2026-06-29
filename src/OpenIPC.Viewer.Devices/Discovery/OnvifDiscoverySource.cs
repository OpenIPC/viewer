using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Onvif.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery;

// Wraps the existing ONVIF WS-Discovery as a v2 source. WS-Discovery is passive
// and batches at the end of the timeout, so progress is coarse: it reports 1.0
// once the batch arrives.
public sealed class OnvifDiscoverySource : IDiscoverySource
{
    private readonly IDiscoveryService _ws;

    public OnvifDiscoverySource(IDiscoveryService ws) => _ws = ws;

    public string Name => "ONVIF";

    public bool IsEnabled(DiscoveryOptions options) => true;

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var cam in _ws.ScanAsync(options.Timeout, ct).ConfigureAwait(false))
        {
            yield return new DiscoveredDevice(
                Host: cam.Host,
                Protocols: DiscoveryProtocol.Onvif,
                Ports: new[] { cam.OnvifPort },
                Name: cam.Name,
                Model: cam.Model,
                OnvifServiceUri: cam.DeviceServiceUri);
        }

        progress?.Report(1.0);
    }
}
