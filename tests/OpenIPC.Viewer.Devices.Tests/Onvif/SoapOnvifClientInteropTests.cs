using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Devices.Onvif;

namespace OpenIPC.Viewer.Devices.Tests.Onvif;

// Interop with firmwares that do not behave like onvif_simple_server: they want
// Digest rather than Basic, or SOAP 1.1 rather than 1.2, or they nest the
// stream URI where the spec says it goes. All three fail the same unhelpful
// way — HTTP 200 with an empty SOAP body — so each is reproduced against a stub
// camera rather than taken on trust.
//
// Every call is preceded by an unauthenticated GetSystemDateAndTime (the clock
// probe the WS-Security digest needs), so assertions count the requests that
// carry the action under test rather than all of them.
public sealed class SoapOnvifClientInteropTests
{
    private static SoapOnvifClient NewClient() => new(NullLogger<SoapOnvifClient>.Instance);

    [Fact]
    public async Task ACameraThatAnswersSoap12WithNothing_IsRetriedAsSoap11()
    {
        using var camera = StubCamera.Start(req =>
            req.IsSoap12 ? (string.Empty, 200) : (Envelope11(Capabilities()), 200));

        var caps = await NewClient().GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None);

        Assert.NotNull(caps);
        // The host's first exchange — the clock probe — is where the flip
        // happens: 1.2, nothing usable, one retry as 1.1. Everything after
        // leads with what that taught, so GetCapabilities is 1.1 on the first
        // try rather than failing 1.2 again.
        Assert.True(camera.Requests[0].IsSoap12);
        Assert.True(camera.Requests[1].IsSoap11);
        var capabilities = camera.Requests.Where(r => r.Is("GetCapabilities")).ToList();
        Assert.Single(capabilities);
        Assert.True(capabilities[0].IsSoap11);
    }

    // SOAP 1.1 carries the action in a header of its own rather than as a
    // parameter on the content type. A camera that reads SOAPAction and finds
    // nothing there rejects the call, so the retry would be pointless without it.
    [Fact]
    public async Task TheSoap11Retry_CarriesTheActionInItsOwnHeader()
    {
        using var camera = StubCamera.Start(req =>
            req.IsSoap12 ? (string.Empty, 200) : (Envelope11(Capabilities()), 200));

        await NewClient().GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None);

        var retry = camera.Requests.Last(r => r.Is("GetCapabilities"));
        Assert.Contains("GetCapabilities", retry.SoapAction ?? "", StringComparison.Ordinal);
    }

    // The Hikvision case. The camera refuses the preemptive Basic header and
    // challenges for Digest; answering that is HttpClient's job, but only when
    // the handler holds the credentials.
    [Fact]
    public async Task ADigestChallenge_IsAnswered()
    {
        using var camera = StubCamera.Start(
            req => (req.Authorization ?? "").StartsWith("Digest", StringComparison.OrdinalIgnoreCase)
                ? (Envelope12(Capabilities()), 200)
                : (string.Empty, 401),
            challenge: "Digest realm=\"IP Camera\", qop=\"auth\", nonce=\"4f3a2b1c\", stale=\"FALSE\"");

        var caps = await NewClient().GetCapabilitiesAsync(
            camera.Endpoint(new CameraCredentials("admin", "secret")), CancellationToken.None);

        Assert.NotNull(caps);
        Assert.Contains(camera.Requests, r =>
            r.Is("GetCapabilities")
            && (r.Authorization ?? "").StartsWith("Digest", StringComparison.OrdinalIgnoreCase));
    }

    // Neither version got anywhere. "Empty SOAP body" is not something a user
    // can act on; the two things worth checking on the camera are.
    [Fact]
    public async Task ACameraThatSaysNothingAtAll_FailsWithSomethingActionable()
    {
        using var camera = StubCamera.Start(_ => (string.Empty, 200));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewClient().GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None));

        Assert.Contains("ONVIF is enabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("account", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A fault is an answer. Asking again in another dialect would waste a round
    // trip and bury what the camera actually said.
    [Fact]
    public async Task AFault_IsReportedAsWorded_AndNeverRetriedAsSoap11()
    {
        using var camera = StubCamera.Start(_ => (Envelope12(
            "<s:Fault xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<s:Reason><s:Text>Sender not authorized</s:Text></s:Reason></s:Fault>"), 400));

        // The fault type is private to the client, so the assertion is on what
        // reaches the caller: the camera's own wording.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            NewClient().GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None));

        Assert.Contains("Sender not authorized", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(camera.Requests, r => r.IsSoap11);
    }

    // GetStreamUriResponse/MediaUri/Uri is what the spec defines and what most
    // cameras send. Reading Uri as a direct child matched only the flatter
    // shape onvif_simple_server returns, so a working camera looked like it had
    // no stream at all.
    [Fact]
    public async Task TheStreamUri_IsReadFromWhereTheSpecPutsIt()
    {
        StubCamera? camera = null;
        camera = StubCamera.Start(req => req.Is("GetCapabilities")
            // The media service has to be advertised somewhere the client can
            // actually follow — this stub.
            ? (Envelope12(Capabilities($"http://127.0.0.1:{camera!.Port}/onvif/media")), 200)
            : (Envelope12(
                "<trt:GetStreamUriResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" " +
                "xmlns:tt=\"http://www.onvif.org/ver10/schema\"><trt:MediaUri>" +
                "<tt:Uri>rtsp://10.16.33.231:554/Streaming/Channels/101</tt:Uri>" +
                "<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>" +
                "</trt:MediaUri></trt:GetStreamUriResponse>"), 200));
        using var _ = camera;

        var uri = await NewClient().GetStreamUriAsync(camera.Endpoint(null), "Profile_1", CancellationToken.None);

        Assert.Equal("rtsp://10.16.33.231:554/Streaming/Channels/101", uri.ToString());
    }

    // The discovery costs one request per host, ever: once a host has answered
    // 1.1 after failing 1.2, later calls lead with 1.1 instead of failing 1.2
    // again first.
    [Fact]
    public async Task TheWorkingDialectIsRemembered()
    {
        using var camera = StubCamera.Start(req =>
            req.IsSoap12 ? (string.Empty, 200) : (Envelope11(Capabilities()), 200));

        var client = NewClient();
        await client.GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None);
        await client.GetCapabilitiesAsync(camera.Endpoint(null), CancellationToken.None);

        // Only the very first request on the host — the clock probe — went out
        // as 1.2; everything after used what that probe learned.
        Assert.Equal(1, camera.Requests.Count(r => r.IsSoap12));
    }

    // An unusable response does not prove the request was not executed. A
    // camera that ran SetPreset and answered garbage must not be asked again —
    // the resend would create a second preset — so mutations fail honestly
    // instead of retrying in the other dialect.
    [Fact]
    public async Task AMutation_IsNeverRetriedInAnotherDialect()
    {
        using var camera = StubCamera.Start(req =>
            req.Is("SetPreset") ? (string.Empty, 200) : (Envelope12(Capabilities()), 200));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewClient().SetPresetAsync(camera.Endpoint(null), "Profile_1", "Gate", CancellationToken.None));

        Assert.Equal(1, camera.Requests.Count(r => r.Is("SetPreset")));
    }

    // --- helpers ------------------------------------------------------------

    private static string Capabilities(string mediaXAddr = "http://127.0.0.1:1/onvif/media") =>
        "<tds:GetCapabilitiesResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\" " +
        "xmlns:tt=\"http://www.onvif.org/ver10/schema\"><tds:Capabilities><tt:Media>" +
        $"<tt:XAddr>{mediaXAddr}</tt:XAddr>" +
        "</tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse>";

    private static string Envelope12(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
        $"<s:Body>{body}</s:Body></s:Envelope>";

    private static string Envelope11(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        $"<s:Body>{body}</s:Body></s:Envelope>";
}
