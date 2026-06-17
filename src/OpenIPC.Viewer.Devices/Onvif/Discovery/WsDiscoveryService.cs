using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Onvif.Core.Discovery;
using Onvif.Core.Discovery.Models;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Devices.Onvif.Discovery;

// WS-Discovery (multicast probe to 239.255.255.250:3702) via Onvif.Core's
// one-shot WSDiscovery.Discover — NOT the DiscoveryService wrapper, which
// loops and reuses a UdpClient that its own Discover disposes on each cycle
// (object-disposed crash). One-shot fits our "scan for N seconds" UX anyway;
// results arrive in a batch at the end of the timeout rather than streaming.
public sealed class WsDiscoveryService : IDiscoveryService
{
    private readonly ILogger<WsDiscoveryService> _logger;
    private readonly INetworkInterfaceProvider _nics;
    private readonly IUserSettingsAccessor _settings;

    public WsDiscoveryService(
        ILogger<WsDiscoveryService> logger,
        INetworkInterfaceProvider nics,
        IUserSettingsAccessor settings)
    {
        _logger = logger;
        _nics = nics;
        _settings = settings;
    }

    public async IAsyncEnumerable<DiscoveredCamera> ScanAsync(
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        var ws = new WSDiscovery();
        // Bind to the chosen LAN interface so the probe doesn't leave via a VPN
        // on multi-adapter machines (Phase 12.6). Auto/stale → best candidate.
        var bind = NetworkInterfaceSelector.ResolveBindAddress(
            _nics.GetCandidates(), _settings.PreferredNetworkInterface);
        // Our own IUdpClient — the stock UdpClientWrapper crashes on Android/iOS
        // (GetActiveTcpListeners is PlatformNotSupported). See PlatformSafeUdpClient.
        var client = new PlatformSafeUdpClient(bind);

        _logger.LogDebug("WS-Discovery scan starting (timeout={Seconds}s, bind={Bind})", seconds, bind ?? "any");
        IEnumerable<DiscoveryDevice> devices;
        try
        {
            devices = await ws.Discover(seconds, client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        _logger.LogDebug("WS-Discovery scan finished, {Count} raw responses", devices?.Count() ?? 0);
        if (devices is null) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var camera = TryMap(device);
            if (camera is null) continue;
            if (!seen.Add(camera.DeviceServiceUri.ToString())) continue;
            yield return camera;
        }
    }

    private static DiscoveredCamera? TryMap(DiscoveryDevice device)
    {
        var xaddr = device.XAdresses?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(xaddr)) return null;
        if (!Uri.TryCreate(xaddr, UriKind.Absolute, out var uri)) return null;

        return new DiscoveredCamera(
            Host: uri.Host,
            OnvifPort: uri.Port,
            DeviceServiceUri: uri,
            Name: device.Name,
            Model: device.Model);
    }
}
