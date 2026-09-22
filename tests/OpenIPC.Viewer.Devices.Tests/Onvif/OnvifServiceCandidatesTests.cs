using System;
using System.Linq;
using OpenIPC.Viewer.Devices.Onvif;
using Xunit;

namespace OpenIPC.Viewer.Devices.Tests.Onvif;

// The candidate order is what decides which URI the client tries first, so it
// is pinned against the GetCapabilities table the A'Gold CAM-10 (YooSee) really
// sends — every XAddr shifted one section down, DeviceIO on a stale address.
public sealed class OnvifServiceCandidatesTests
{
    private static readonly Uri Device = new("http://192.168.1.50:5000/onvif/device_service");

    private static readonly Uri[] ShiftedTable =
    {
        new("http://192.168.1.50:5000/onvif/device_service"),   // Device
        new("http://192.168.1.50:5000/onvif/media_service"),    // Events
        new("http://192.168.1.50:5000/onvif/ptz_service"),      // Media
        new("http://10.0.0.7:5000/onvif/deviceio_service"),     // PTZ (and a stale IP)
    };

    [Fact]
    public void Media_ReachesTheRealMediaServiceAfterTheAdvertisedOne()
    {
        var list = OnvifServiceCandidates.Media(Device, ShiftedTable[2], ShiftedTable);

        Assert.Equal(ShiftedTable[2], list[0]);
        Assert.Equal(new Uri("http://192.168.1.50:5000/onvif/media_service"), list[1]);
    }

    [Fact]
    public void Ptz_ReachesTheRealPtzServiceAfterTheAdvertisedOne()
    {
        var list = OnvifServiceCandidates.Ptz(Device, ShiftedTable[3], ShiftedTable);

        Assert.Equal(ShiftedTable[3], list[0]);
        Assert.Equal(new Uri("http://192.168.1.50:5000/onvif/deviceio_service"), list[1]);
        Assert.Equal(new Uri("http://192.168.1.50:5000/onvif/ptz_service"), list[2]);
    }

    [Fact]
    public void AdvertisedAddressIsRetriedOnTheDeviceAuthority()
    {
        var natted = new Uri("http://10.0.0.7/onvif/Media?x=1");

        var list = OnvifServiceCandidates.Media(Device, natted, new[] { natted });

        Assert.Equal(natted, list[0]);
        Assert.Equal(new Uri("http://192.168.1.50:5000/onvif/Media?x=1"), list[1]);
    }

    [Fact]
    public void NothingAdvertised_FallsBackToConventionalPathsThenDeviceService()
    {
        var list = OnvifServiceCandidates.Ptz(Device, advertised: null, Array.Empty<Uri>());

        Assert.Equal(new[]
        {
            new Uri("http://192.168.1.50:5000/onvif/ptz_service"),
            new Uri("http://192.168.1.50:5000/onvif/PTZ"),
            Device,
        }, list);
    }

    [Fact]
    public void Candidates_AreDistinct()
    {
        var list = OnvifServiceCandidates.Media(Device, ShiftedTable[1], ShiftedTable);

        Assert.Equal(list.Count, list.Distinct().Count());
        Assert.Equal(Device, list[^1]);
    }
}
