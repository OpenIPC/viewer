using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Onvif.Core.Discovery.Interfaces;

namespace OpenIPC.Viewer.Devices.Onvif.Discovery;

// Drop-in IUdpClient for Onvif.Core's WSDiscovery. The stock UdpClientWrapper
// calls IPGlobalProperties.GetActiveTcpListeners() in its constructor (a
// port-collision pre-check) — that API is unimplemented on Android/iOS and
// throws PlatformNotSupportedException ("Arg_PlatformNotSupported"), so device
// discovery died before sending a single probe on mobile.
//
// This implementation skips that check: it binds an ephemeral UDP socket and
// sends the multicast probe to 239.255.255.250:3702. WS-Discovery ProbeMatch
// replies come back as *unicast* to our source port, so we never join a
// multicast group — which also means no Android WifiManager.MulticastLock is
// required. Works identically on desktop, so no per-platform branching.
internal sealed class PlatformSafeUdpClient : IUdpClient
{
    private readonly UdpClient _client;

    // bindAddress pins the probe to a specific local IPv4 (Phase 12.6) so the
    // multicast doesn't leave via a VPN/virtual adapter on multi-NIC machines.
    // Null / unparseable → IPAddress.Any (let the OS route, prior behavior).
    public PlatformSafeUdpClient(string? bindAddress = null)
    {
        var local = !string.IsNullOrWhiteSpace(bindAddress) && IPAddress.TryParse(bindAddress, out var parsed)
            ? parsed
            : IPAddress.Any;
        // Port 0 = let the OS pick a free ephemeral port. The stock wrapper
        // hard-bound port 80, which is both privileged and collision-prone.
        _client = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
    }

    public short Ttl
    {
        get => _client.Ttl;
        set => _client.Ttl = value;
    }

    public Task<int> SendAsync(byte[] datagram, int bytes, IPEndPoint endPoint) =>
        _client.SendAsync(datagram, bytes, endPoint);

    public Task<UdpReceiveResult> ReceiveAsync() => _client.ReceiveAsync();

    public void Close() => _client.Close();

    public void Dispose() => _client.Dispose();
}
