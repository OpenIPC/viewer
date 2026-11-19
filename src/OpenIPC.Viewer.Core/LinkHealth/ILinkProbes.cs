using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>ICMP echo. Implementations return null where ICMP is not available (e.g. sandboxed mobile).</summary>
public interface IPingProbe
{
    /// <summary>A paced series of <paramref name="count"/> echoes.</summary>
    Task<PingStats?> PingAsync(string host, int count, int payloadBytes, TimeSpan interval, CancellationToken ct);

    /// <summary>
    /// Back-to-back echoes from <paramref name="parallel"/> loops for
    /// <paramref name="duration"/> — real load on the link in both directions.
    /// </summary>
    Task<PingStats?> FloodAsync(string host, int payloadBytes, TimeSpan duration, int parallel, CancellationToken ct);
}

/// <summary>This computer's network adapter towards a camera.</summary>
public interface IHostLinkProbe
{
    /// <param name="localAddress">
    /// This computer's address on the camera's network when known (the routing
    /// check found it); otherwise the adapter the OS routes the camera to is used.
    /// </param>
    Task<HostLinkSnapshot?> GetAsync(string cameraHost, string? localAddress, CancellationToken ct);
}

/// <summary>Camera-side link state and fixes, over SSH.</summary>
public interface ICameraLinkClient
{
    Task<CameraLinkSnapshot> ReadAsync(SshEndpoint endpoint, CancellationToken ct);

    /// <summary>Restarts the streamer — drops every RTSP client, stale ones included.</summary>
    Task RestartStreamerAsync(SshEndpoint endpoint, CancellationToken ct);

    /// <summary>Changes the advertised link mode; false when the camera has no ethtool.</summary>
    Task<bool> SetLinkModeAsync(SshEndpoint endpoint, CameraLinkMode mode, CancellationToken ct);
}
