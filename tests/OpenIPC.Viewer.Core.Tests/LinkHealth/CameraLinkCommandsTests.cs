using OpenIPC.Viewer.Core.LinkHealth;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

public sealed class CameraLinkCommandsTests
{
    private const string Sample = """
        if=eth0
        operstate=up
        carrier=1
        speed=100
        duplex=full
        carrier_changes=5
        carrier_up_count=3
        carrier_down_count=2
        st_rx_packets=1000000
        st_tx_packets=2000000
        st_rx_errors=12
        st_tx_errors=0
        st_rx_crc_errors=10
        st_rx_frame_errors=2
        st_rx_dropped=4
        st_collisions=0
        uptime=3725.42
        ethtool=1
        eth:Settings for eth0:
        eth:	Supported link modes:   10baseT/Half 10baseT/Full
        eth:	                        100baseT/Half 100baseT/Full
        eth:	Link partner advertised link modes:  10baseT/Half 10baseT/Full
        eth:	                                     100baseT/Half 100baseT/Full
        eth:	Auto-negotiation: on
        tcp=0A00A8C0:022A 0B00A8C0:D431
        tcp=0A00A8C0:0016 0B00A8C0:C350
        """;

    [Fact]
    public void Parse_ReadsSysfsCounters()
    {
        var s = CameraLinkCommands.Parse(Sample);

        Assert.Equal("eth0", s.Interface);
        Assert.Equal("up", s.OperState);
        Assert.True(s.Carrier);
        Assert.Equal(100, s.SpeedMbps);
        Assert.Equal("full", s.Duplex);
        Assert.Equal(12, s.PhysicalErrors);
        Assert.Equal(1_000_000, s.RxPackets);
        Assert.Equal(3725.42, s.UptimeSeconds);
        Assert.Equal(2, s.LinkFlapsSinceBoot); // up_count 3 = boot + 2 drops
    }

    [Fact]
    public void Parse_ReadsEthtoolContinuationLines()
    {
        var s = CameraLinkCommands.Parse(Sample);
        Assert.True(s.HasEthtool);
        Assert.True(s.AutoNegotiation);
        Assert.Equal("10baseT/Half 10baseT/Full 100baseT/Half 100baseT/Full", s.LinkPartnerModes);
    }

    [Fact]
    public void Parse_DecodesProcNetTcp()
    {
        var s = CameraLinkCommands.Parse(Sample);
        Assert.Equal(2, s.Connections.Count);
        var rtsp = s.Connections[0];
        Assert.Equal("192.168.0.10", rtsp.LocalAddress);
        Assert.Equal(554, rtsp.LocalPort);
        Assert.Equal("192.168.0.11", rtsp.RemoteAddress);
        Assert.Equal(54321, rtsp.RemotePort);
    }

    [Fact]
    public void Parse_LinkDownAndMissingFiles_AreNull()
    {
        var s = CameraLinkCommands.Parse("if=eth0\noperstate=down\ncarrier=\nspeed=-1\nduplex=\nethtool=0\n");
        Assert.Null(s.Carrier);
        Assert.Null(s.SpeedMbps);
        Assert.Null(s.Duplex);
        Assert.False(s.HasEthtool);
        Assert.Null(s.LinkFlapsSinceBoot);
        Assert.Empty(s.Connections);
    }

    [Fact]
    public void FlapsFallBackToCarrierChanges()
    {
        // No carrier_up_count: carrier_changes counts both edges incl. the boot-time up.
        var s = CameraLinkCommands.Parse("carrier_changes=5\n");
        Assert.Equal(2, s.LinkFlapsSinceBoot);
    }

    [Fact]
    public void Parse_ToleratesCrlfAndGarbage()
    {
        var s = CameraLinkCommands.Parse("speed=100\r\nnot a pair\r\ntcp=bad\r\n=x\r\n");
        Assert.Equal(100, s.SpeedMbps);
        Assert.Empty(s.Connections);
    }

    [Fact]
    public void SetLinkMode_KeepsAutonegAndPicksTheMask()
    {
        Assert.Contains("autoneg on advertise 0x002", CameraLinkCommands.SetLinkMode(CameraLinkMode.Force10Full));
        Assert.Contains("autoneg on advertise 0x00f", CameraLinkCommands.SetLinkMode(CameraLinkMode.Auto));
        Assert.Contains("noethtool=1", CameraLinkCommands.SetLinkMode(CameraLinkMode.Auto));
    }

    [Fact]
    public void ReadLinkState_IsReadOnly()
    {
        var cmd = CameraLinkCommands.ReadLinkState();
        Assert.DoesNotContain("ethtool -s", cmd);
        Assert.DoesNotContain("reboot", cmd);
        Assert.DoesNotContain("killall", cmd);
        Assert.Contains("/proc/net/tcp", cmd);
    }
}
