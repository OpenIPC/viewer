using System;
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

        using var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own timeout, not a caller cancel
        }
        catch (Exception)
        {
            // SocketException (refused / unreachable), DNS resolution failure, etc.
            return false;
        }
    }
}
