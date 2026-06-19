using System;
using System.Collections.Generic;
using System.Linq;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Video;

// Pure Phase 12 policy helpers — backoff ramp (12.3), SD/HD selection (12.2),
// and WS-Discovery interface filtering (12.6).
public sealed class Phase12PolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]   // 32 capped to 30
    [InlineData(7, 30)]
    [InlineData(20, 30)]
    public void Backoff_FollowsExponentialRampCappedAt30(int attempt, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, ReconnectBackoff.Delay(attempt).TotalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Backoff_ClampsNonPositiveAttemptToFirstStep(int attempt)
    {
        Assert.Equal(1, ReconnectBackoff.Delay(attempt).TotalSeconds);
    }

    [Theory]
    [InlineData(true, 1, StreamQuality.Main)]   // single tile fills the view → HD
    [InlineData(true, 2, StreamQuality.Sub)]
    [InlineData(true, 3, StreamQuality.Sub)]
    [InlineData(false, 1, StreamQuality.Sub)]   // auto off → always SD
    [InlineData(false, 2, StreamQuality.Sub)]
    public void GridQuality_HdOnlyForSingleTileWithAutoOn(bool autoSdHd, int layout, StreamQuality expected)
    {
        Assert.Equal(expected, StreamQualityPolicy.ForGrid(autoSdHd, layout));
    }

    [Theory]
    // Per-camera override wins regardless of layout / global toggle…
    [InlineData(StreamQualityOverride.AlwaysHd, false, 3, StreamQuality.Main)]
    [InlineData(StreamQualityOverride.AlwaysSd, true, 1, StreamQuality.Sub)]
    // …Auto defers to the global grid policy.
    [InlineData(StreamQualityOverride.Auto, true, 1, StreamQuality.Main)]
    [InlineData(StreamQualityOverride.Auto, true, 2, StreamQuality.Sub)]
    [InlineData(StreamQualityOverride.Auto, false, 1, StreamQuality.Sub)]
    public void Resolve_OverrideBeatsGlobalPolicy(
        StreamQualityOverride cameraOverride, bool autoSdHd, int layout, StreamQuality expected)
    {
        Assert.Equal(expected, StreamQualityPolicy.Resolve(cameraOverride, autoSdHd, layout));
    }

    [Fact]
    public void Nics_DropsDownLoopbackAndTunnelAdapters()
    {
        var candidates = NetworkInterfaceSelector.SelectCandidates(new[]
        {
            Nic("eth0", up: true, loopback: false, tunnel: false, gateway: true, "192.168.1.5"),
            Nic("vpn", up: true, loopback: false, tunnel: true, gateway: false, "10.8.0.2"),
            Nic("lo", up: true, loopback: true, tunnel: false, gateway: false, "127.0.0.1"),
            Nic("down", up: false, loopback: false, tunnel: false, gateway: true, "192.168.1.9"),
        });

        Assert.Equal(new[] { "192.168.1.5" }, candidates.Select(c => c.Address).ToArray());
    }

    [Fact]
    public void Nics_OrdersGatewayPrivateLanFirst()
    {
        var candidates = NetworkInterfaceSelector.SelectCandidates(new[]
        {
            Nic("virt", up: true, loopback: false, tunnel: false, gateway: false, "172.20.0.1"), // private, no gw
            Nic("eth0", up: true, loopback: false, tunnel: false, gateway: true, "192.168.1.5"),  // private + gw
        });

        Assert.Equal("192.168.1.5", candidates[0].Address);
        Assert.Equal("172.20.0.1", candidates[1].Address);
    }

    [Fact]
    public void ResolveBindAddress_PrefersExplicitChoiceWhenPresent()
    {
        var candidates = NetworkInterfaceSelector.SelectCandidates(new[]
        {
            Nic("eth0", up: true, loopback: false, tunnel: false, gateway: true, "192.168.1.5"),
            Nic("eth1", up: true, loopback: false, tunnel: false, gateway: false, "192.168.1.6"),
        });

        Assert.Equal("192.168.1.6", NetworkInterfaceSelector.ResolveBindAddress(candidates, "192.168.1.6"));
    }

    [Fact]
    public void ResolveBindAddress_FallsBackToBestCandidateForAutoOrStale()
    {
        var candidates = NetworkInterfaceSelector.SelectCandidates(new[]
        {
            Nic("eth0", up: true, loopback: false, tunnel: false, gateway: true, "192.168.1.5"),
        });

        Assert.Equal("192.168.1.5", NetworkInterfaceSelector.ResolveBindAddress(candidates, ""));        // auto
        Assert.Equal("192.168.1.5", NetworkInterfaceSelector.ResolveBindAddress(candidates, "9.9.9.9")); // stale
    }

    [Fact]
    public void ResolveBindAddress_NullWhenNoCandidates()
    {
        Assert.Null(NetworkInterfaceSelector.ResolveBindAddress(Array.Empty<NetworkInterfaceInfo>(), "192.168.1.5"));
    }

    private static NicDescriptor Nic(string name, bool up, bool loopback, bool tunnel, bool gateway, params string[] ipv4) =>
        new(name, up, loopback, tunnel, gateway, ipv4);
}
