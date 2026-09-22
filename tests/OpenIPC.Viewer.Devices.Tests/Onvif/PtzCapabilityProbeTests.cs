using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Devices.Onvif;

namespace OpenIPC.Viewer.Devices.Tests.Onvif;

// The capability probe against a stub camera: the SOAP chain is GetProfiles →
// GetConfigurationOptions → GetServiceCapabilities → GetConfiguration →
// GetNode, and every flag the UI gates a button on comes out of it. Wrong
// parsing does not throw — it shows buttons that fail or hides ones that work —
// so the answers are pinned against known response bodies.
public sealed class PtzCapabilityProbeTests
{
    private static SoapOnvifClient NewClient() => new(NullLogger<SoapOnvifClient>.Instance);

    // A camera with relative + continuous pan/tilt (FOV-relative), no zoom
    // spaces at all, MoveStatus maintained, and a node that supports home and
    // allows overwriting it.
    [Fact]
    public async Task TheFlagsComeFromWhatTheCameraDeclared()
    {
        using var camera = StubCamera.Start(Respond);

        var caps = await NewClient().GetPtzCapabilitiesAsync(
            camera.Endpoint(null), "Profile_1", CancellationToken.None);

        Assert.True(caps.SupportsRelativePanTilt);
        Assert.True(caps.SupportsContinuousPanTilt);
        Assert.False(caps.SupportsRelativeZoom);
        Assert.False(caps.SupportsContinuousZoom);
        Assert.True(caps.RelativeIsFieldOfView);
        Assert.True(caps.SupportsMoveStatus);
        Assert.True(caps.SupportsHome);
        Assert.True(caps.SupportsSetHome);
        Assert.Equal(-1f, caps.RelativePan.Min, 4);
        Assert.Equal(1f, caps.RelativePan.Max, 4);
    }

    // A hardware-fixed home position can be visited but not overwritten.
    [Fact]
    public async Task AFixedHomePosition_AllowsGotoButNotSetHome()
    {
        using var camera = StubCamera.Start(req => Respond(req, fixedHome: true));

        var caps = await NewClient().GetPtzCapabilitiesAsync(
            camera.Endpoint(null), "Profile_1", CancellationToken.None);

        Assert.True(caps.SupportsHome);
        Assert.False(caps.SupportsSetHome);
    }

    // A camera that answers the node questions with nothing gets no home
    // controls — never a fabricated yes.
    [Fact]
    public async Task ACameraThatWillNotDescribeItsNode_GetsNoHome()
    {
        using var camera = StubCamera.Start(req =>
            req.Is("GetConfiguration") && !req.Is("GetConfigurationOptions")
                ? (string.Empty, 200)
                : Respond(req));

        var caps = await NewClient().GetPtzCapabilitiesAsync(
            camera.Endpoint(null), "Profile_1", CancellationToken.None);

        Assert.True(caps.SupportsRelativePanTilt);   // the spaces still parsed
        Assert.False(caps.SupportsHome);
        Assert.False(caps.SupportsSetHome);
    }

    private static (string Body, int Status) Respond(StubRequest req) => Respond(req, fixedHome: false);

    private static (string Body, int Status) Respond(StubRequest req, bool fixedHome)
    {
        const string tt = "http://www.onvif.org/ver10/schema";
        const string tptz = "http://www.onvif.org/ver20/ptz/wsdl";

        if (req.Is("GetProfiles"))
        {
            return (Envelope(
                $"<trt:GetProfilesResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"{tt}\">" +
                "<trt:Profiles token=\"Profile_1\"><tt:Name>main</tt:Name>" +
                "<tt:PTZConfiguration token=\"cfg1\"><tt:NodeToken>node0</tt:NodeToken></tt:PTZConfiguration>" +
                "</trt:Profiles></trt:GetProfilesResponse>"), 200);
        }
        if (req.Is("GetConfigurationOptions"))
        {
            return (Envelope(
                $"<tptz:GetConfigurationOptionsResponse xmlns:tptz=\"{tptz}\" xmlns:tt=\"{tt}\">" +
                "<tptz:PTZConfigurationOptions><tt:Spaces>" +
                "<tt:RelativePanTiltTranslationSpace>" +
                "<tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/TranslationSpaceFov</tt:URI>" +
                "<tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>" +
                "<tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>" +
                "</tt:RelativePanTiltTranslationSpace>" +
                "<tt:ContinuousPanTiltVelocitySpace>" +
                "<tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>" +
                "<tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>" +
                "<tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>" +
                "</tt:ContinuousPanTiltVelocitySpace>" +
                "</tt:Spaces></tptz:PTZConfigurationOptions></tptz:GetConfigurationOptionsResponse>"), 200);
        }
        if (req.Is("GetServiceCapabilities"))
        {
            return (Envelope(
                $"<tptz:GetServiceCapabilitiesResponse xmlns:tptz=\"{tptz}\">" +
                "<tptz:Capabilities MoveStatus=\"true\"/></tptz:GetServiceCapabilitiesResponse>"), 200);
        }
        if (req.Is("GetNode"))
        {
            return (Envelope(
                $"<tptz:GetNodeResponse xmlns:tptz=\"{tptz}\" xmlns:tt=\"{tt}\">" +
                $"<tptz:PTZNode token=\"node0\" FixedHomePosition=\"{(fixedHome ? "true" : "false")}\">" +
                "<tt:Name>node0</tt:Name><tt:HomeSupported>true</tt:HomeSupported>" +
                "</tptz:PTZNode></tptz:GetNodeResponse>"), 200);
        }
        if (req.Is("GetConfiguration"))
        {
            return (Envelope(
                $"<tptz:GetConfigurationResponse xmlns:tptz=\"{tptz}\" xmlns:tt=\"{tt}\">" +
                "<tptz:PTZConfiguration token=\"cfg1\"><tt:NodeToken>node0</tt:NodeToken></tptz:PTZConfiguration>" +
                "</tptz:GetConfigurationResponse>"), 200);
        }

        // Clock probe, device capabilities (no PTZ XAddr → the client falls
        // back to the device endpoint, which is this stub), anything else.
        return (Envelope("<a/>"), 200);
    }

    private static string Envelope(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
        $"<s:Body>{body}</s:Body></s:Envelope>";
}
