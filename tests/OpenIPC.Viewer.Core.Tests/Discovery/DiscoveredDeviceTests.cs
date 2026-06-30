using System;
using System.Linq;
using OpenIPC.Viewer.Core.Discovery;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Discovery;

// The merge-by-IP + confidence logic the aggregator leans on. Pure, so it tests
// without any sockets.
public sealed class DiscoveredDeviceTests
{
    [Fact]
    public void MergeWith_UnionsProtocolsAndPorts()
    {
        var a = new DiscoveredDevice("10.0.0.5", DiscoveryProtocol.Rtsp, new[] { 554 });
        var b = new DiscoveredDevice("10.0.0.5", DiscoveryProtocol.Http, new[] { 80, 554 });

        var merged = a.MergeWith(b);

        Assert.Equal(DiscoveryProtocol.Rtsp | DiscoveryProtocol.Http, merged.Protocols);
        Assert.Equal(new[] { 80, 554 }, merged.Ports.ToArray()); // deduped + sorted
    }

    [Fact]
    public void MergeWith_KeepsFirstNonNullLabels_AndFillsOnvifUri()
    {
        var rtsp = new DiscoveredDevice("10.0.0.5", DiscoveryProtocol.Rtsp, new[] { 554 });
        var onvif = new DiscoveredDevice(
            "10.0.0.5", DiscoveryProtocol.Onvif, new[] { 80 },
            Name: "Front door", Model: "IPC-X",
            OnvifServiceUri: new Uri("http://10.0.0.5/onvif/device_service"));

        var merged = rtsp.MergeWith(onvif);

        Assert.Equal("Front door", merged.Name);
        Assert.Equal("IPC-X", merged.Model);
        Assert.NotNull(merged.OnvifServiceUri);
    }

    [Theory]
    [InlineData(DiscoveryProtocol.Rtsp, DiscoveryConfidence.Low)]                              // 1 weak signal
    [InlineData(DiscoveryProtocol.Rtsp | DiscoveryProtocol.Http, DiscoveryConfidence.Medium)] // 2 signals
    [InlineData(DiscoveryProtocol.Majestic, DiscoveryConfidence.High)]                         // fingerprint
    [InlineData(DiscoveryProtocol.Onvif, DiscoveryConfidence.High)]                            // fingerprint
    [InlineData(DiscoveryProtocol.Rtsp | DiscoveryProtocol.Majestic, DiscoveryConfidence.High)]
    public void Confidence_DerivesFromProtocols(DiscoveryProtocol protocols, DiscoveryConfidence expected)
    {
        var device = new DiscoveredDevice("10.0.0.5", protocols, Array.Empty<int>());
        Assert.Equal(expected, device.Confidence);
    }
}
