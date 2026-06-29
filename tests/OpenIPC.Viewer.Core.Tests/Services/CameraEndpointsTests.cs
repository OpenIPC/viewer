using System;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Services;

// The dialed-endpoint resolution that the whole probe path shares. Encodes the
// "RTSP URI wins over Host, default port 554" rule that previously caused a
// camera to read OFFLINE while it was actually streaming.
public sealed class CameraEndpointsTests
{
    private static Camera Cam(string rtsp, string host) => new(
        new CameraId(Guid.NewGuid()), null, "cam", host, null, 80,
        new Uri(rtsp), null, null, null, false, null, null, null,
        false, false, false, 0, DateTime.UnixEpoch, DateTime.UnixEpoch);

    [Fact]
    public void UsesRtspUriHostAndPort()
    {
        var (host, port) = Cam("rtsp://10.0.0.5:8554/main", "example.local").StreamProbeTarget();
        Assert.Equal("10.0.0.5", host);
        Assert.Equal(8554, port);
    }

    [Fact]
    public void DefaultsPortTo554_WhenUriOmitsIt()
    {
        var (_, port) = Cam("rtsp://10.0.0.5/main", "10.0.0.5").StreamProbeTarget();
        Assert.Equal(CameraEndpoints.DefaultRtspPort, port);
    }

    [Fact]
    public void PrefersRtspUriHostOverCameraHostField()
    {
        // RTSP host (NAT / mDNS name) is what the player dials — it must win.
        var (host, _) = Cam("rtsp://10.0.0.9:554/main", "camera.lan").StreamProbeTarget();
        Assert.Equal("10.0.0.9", host);
    }
}
