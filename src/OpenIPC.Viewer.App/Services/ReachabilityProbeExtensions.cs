using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.Services;

// One home for "TCP-probe a camera's stream port". The grid tile, single-camera
// page, library row, and Health Center all need to resolve the dialed endpoint
// (CameraEndpoints.StreamProbeTarget) then probe it, so they share this instead
// of each re-wrapping the try/catch.
public static class ReachabilityProbeExtensions
{
    // Resolve + TCP-probe in one call. Exceptions (incl. cancellation) collapse
    // to "not reachable" — for a reachability check, "threw" is just false. Pass
    // a logger to record the failure at Debug.
    public static async Task<bool> ProbeAsync(
        this IReachabilityProbe probe,
        Camera camera,
        TimeSpan timeout,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var (host, port) = camera.StreamProbeTarget();
        try
        {
            return await probe.IsReachableAsync(host, port, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Reachability probe failed for {CameraId}", camera.Id);
            return false;
        }
    }
}
