using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.Infrastructure.Net;

/// <summary>
/// Reachability via a plain TCP connect. Any non-success (refused, no route,
/// DNS failure, timeout) is reported as unreachable rather than thrown — the
/// caller only wants a reachable/not flag for a status badge.
/// </summary>
public sealed class TcpReachabilityProbe : IReachabilityProbe
{
    public async Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            return false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            // Connect with a socket of each address's own family. A bare
            // TcpClient() is a dual-mode IPv6 socket, and on hosts with IPv6
            // disabled (or a VPN that drops it) connecting that to an IPv4
            // camera fails with WSAEADDRNOTAVAIL — every camera read offline.
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, linked.Token).ConfigureAwait(false);
            foreach (var address in addresses)
            {
                using var client = new TcpClient(address.AddressFamily);
                try
                {
                    await client.ConnectAsync(address, port, linked.Token).ConfigureAwait(false);
                    if (client.Connected) return true;
                }
                catch (SocketException)
                {
                    // Try the next address (e.g. AAAA then A).
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own timeout, not a caller cancel
        }
        catch (Exception)
        {
            return false;
        }
    }
}
