using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Devices.Onvif;

/// <summary>
/// Trim-safe ONVIF client: builds SOAP 1.2 envelopes by hand and parses the
/// responses with <see cref="XDocument"/> over <see cref="HttpClient"/>. It
/// replaces the WCF/System.ServiceModel path (OnvifCoreClient), whose
/// XmlSerializer can't build its runtime serializers under the Android linker —
/// the "XmlType reflection error" on <c>Onvif.Core.Client.Common.DeviceEntity</c>.
/// Same <see cref="IOnvifClient"/> contract, so the swap is one DI registration.
///
/// Auth is three things at once, because cameras disagree about which they
/// want: preemptive HTTP Basic (OpenIPC's onvif_simple_server enforces it at
/// the transport and never challenges), HTTP Digest answered on a 401 by an
/// <see cref="HttpClient"/> whose handler carries the credentials, and a
/// WS-Security UsernameToken password digest in the envelope.
/// GetSystemDateAndTime (unauthenticated) yields the camera clock offset the
/// token's Created stamp needs; it's cached per host.
/// </summary>
public sealed class SoapOnvifClient : IOnvifClient
{
    private const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Tds = "http://www.onvif.org/ver10/device/wsdl";
    private const string Trt = "http://www.onvif.org/ver10/media/wsdl";
    private const string Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string Tt = "http://www.onvif.org/ver10/schema";
    private const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PwDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64Type = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly ILogger<SoapOnvifClient> _logger;

    // Camera clock offset (cameraUtc - hostUtc) per host — the digest's Created
    // stamp must be near the camera's clock or it rejects the token. Computed on
    // first authed call, refreshed on an auth fault.
    private readonly ConcurrentDictionary<string, TimeSpan> _shiftByHost = new(StringComparer.OrdinalIgnoreCase);

    // Media/PTZ service URI per device endpoint, cached only once it has
    // answered a read-only call of its own service (see ResolveServiceAsync).
    private readonly ConcurrentDictionary<string, Uri> _serviceByDevice = new(StringComparer.OrdinalIgnoreCase);

    // Addresses (host:port) that turned out to speak SOAP 1.1 only. Learned from the retry the
    // first time a host answers 1.2 with nothing usable, then used as the first
    // choice — so the discovery costs one extra request per host, ever, and a
    // state-changing call is never the one doing the discovering.
    private readonly ConcurrentDictionary<string, byte> _soap11Hosts = new(StringComparer.OrdinalIgnoreCase);

    // One client per camera address. A handler that carries credentials is what
    // lets HttpClient answer a 401 challenge on its own, which is the only way
    // to satisfy a camera that asks for Digest rather than Basic.
    //
    // Keyed by host:port alone — a camera has one credential at a time — with
    // the credential kept beside the client so a password change swaps the
    // entry and disposes the superseded one, instead of caching every password
    // this process has ever seen. Growth is bounded by the number of camera
    // addresses. A plain lock rather than GetOrAdd: it also stops a concurrent
    // miss from constructing a second client that nothing would ever dispose.
    private readonly object _clientsGate = new();
    private readonly Dictionary<string, (string Credential, HttpClient Client)> _authedClients = new(StringComparer.Ordinal);

    public SoapOnvifClient(ILogger<SoapOnvifClient> logger)
    {
        _logger = logger;
        _http = NewClient(credentials: null);
    }

    private static HttpClient NewClient(NetworkCredential? credentials)
    {
        // onvif_simple_server is CGI-style: one request per connection, then it
        // closes the socket. Disable pooling so we never reuse a dead socket.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = CallTimeout,
        };
        if (credentials is not null)
        {
            handler.Credentials = credentials;
            // Let the camera state its terms first: preemptive auth would send
            // Basic to a device that only accepts Digest.
            handler.PreAuthenticate = false;
        }
        return new HttpClient(handler) { Timeout = CallTimeout };
    }

    private HttpClient ClientFor(Uri service, CameraCredentials? credentials)
    {
        if (credentials is not { } c || string.IsNullOrEmpty(c.Username)) return _http;

        var key = $"{service.Host}:{service.Port}";
        var credential = $"{c.Username}\u0000{c.Password}";
        lock (_clientsGate)
        {
            if (_authedClients.TryGetValue(key, out var entry))
            {
                if (entry.Credential == credential) return entry.Client;
                // The password changed. A request in flight on the old client
                // was sent with the old password and is failing anyway, so
                // disposing under it loses nothing.
                entry.Client.Dispose();
            }

            var client = NewClient(new NetworkCredential(c.Username, c.Password ?? string.Empty));
            _authedClients[key] = (credential, client);
            return client;
        }
    }

    // --- Device service -----------------------------------------------------

    public async Task<OnvifCapabilities> GetCapabilitiesAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var caps = await GetCapabilitiesElementAsync(endpoint, ct).ConfigureAwait(false);
        return new OnvifCapabilities(
            MediaServiceUri: TryUri(XAddrOf(caps, "Media")),
            PtzServiceUri: TryUri(XAddrOf(caps, "PTZ")));
    }

    private async Task<XElement?> GetCapabilitiesElementAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var body = await CallAuthedAsync(endpoint.DeviceServiceUri, endpoint,
            $"{Tds}/GetCapabilities",
            $"<tds:GetCapabilities xmlns:tds=\"{Tds}\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
            ct).ConfigureAwait(false);
        return Child(body, "Capabilities");
    }

    public async Task<OnvifDeviceInfo> GetDeviceInformationAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var body = await CallAuthedAsync(endpoint.DeviceServiceUri, endpoint,
            $"{Tds}/GetDeviceInformation",
            $"<tds:GetDeviceInformation xmlns:tds=\"{Tds}\"/>",
            ct).ConfigureAwait(false);

        return new OnvifDeviceInfo(
            Manufacturer: Value(body, "Manufacturer"),
            Model: Value(body, "Model"),
            FirmwareVersion: Value(body, "FirmwareVersion"),
            SerialNumber: Value(body, "SerialNumber"));
    }

    // --- Media service ------------------------------------------------------

    public async Task<IReadOnlyList<MediaProfile>> GetProfilesAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var media = await ResolveServiceAsync(endpoint, ServiceKind.Media, ct).ConfigureAwait(false);
        var body = await CallAuthedAsync(media, endpoint,
            $"{Trt}/GetProfiles",
            $"<trt:GetProfiles xmlns:trt=\"{Trt}\"/>",
            ct).ConfigureAwait(false);

        return Children(body, "Profiles")
            .Select(p => new MediaProfile(
                Token: Attr(p, "token"),
                Name: Value(p, "Name") ?? Attr(p, "token"),
                PtzConfigurationToken: Attr(Child(p, "PTZConfiguration"), "token"),
                HasAudioIn: Child(p, "AudioEncoderConfiguration") is not null,
                HasAudioOut: Child(Child(p, "Extension"), "AudioOutputConfiguration") is not null))
            .ToList();
    }

    public async Task<Uri> GetStreamUriAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var media = await ResolveServiceAsync(endpoint, ServiceKind.Media, ct).ConfigureAwait(false);
        var reqBody =
            $"<trt:GetStreamUri xmlns:trt=\"{Trt}\" xmlns:tt=\"{Tt}\">" +
            "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream>" +
            "<tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
            $"<trt:ProfileToken>{Escape(profileToken)}</trt:ProfileToken></trt:GetStreamUri>";

        var body = await CallAuthedAsync(media, endpoint, $"{Trt}/GetStreamUri", reqBody, ct).ConfigureAwait(false);
        // The spec nests this as MediaUri/Uri, and Hikvision (among others) sends
        // exactly that. Reading it as a direct child only matched the flatter
        // shape onvif_simple_server returns, so a compliant camera looked like
        // it had answered with no stream at all.
        var uri = Descendant(body, "Uri")?.Value;
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException($"GetStreamUri returned no URI for profile {profileToken}");
        return new Uri(uri, UriKind.Absolute);
    }

    // --- PTZ service --------------------------------------------------------

    public async Task ContinuousMoveAsync(OnvifEndpoint endpoint, string profileToken, PtzVelocity velocity, TimeSpan? timeout, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var timeoutXml = timeout is { } t
            ? $"<tptz:Timeout>{XmlConvertDuration(t)}</tptz:Timeout>"
            : "";
        var reqBody =
            $"<tptz:ContinuousMove xmlns:tptz=\"{Tptz}\" xmlns:tt=\"{Tt}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            "<tptz:Velocity>" +
            $"<tt:PanTilt x=\"{Num(velocity.PanX)}\" y=\"{Num(velocity.TiltY)}\"/>" +
            $"<tt:Zoom x=\"{Num(velocity.Zoom)}\"/>" +
            "</tptz:Velocity>" +
            timeoutXml +
            "</tptz:ContinuousMove>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/ContinuousMove", reqBody, ct).ConfigureAwait(false);
    }

    public async Task StopPtzAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:Stop xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            "<tptz:PanTilt>true</tptz:PanTilt><tptz:Zoom>true</tptz:Zoom></tptz:Stop>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/Stop", reqBody, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PtzPreset>> GetPresetsAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:GetPresets xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken></tptz:GetPresets>";
        var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetPresets", reqBody, ct).ConfigureAwait(false);

        return Children(body, "Preset")
            .Where(p => !string.IsNullOrEmpty(Attr(p, "token")))
            // Names come back mangled from cameras that store UTF-8 and label
            // the response Latin-1; the bytes survive, only the label was wrong.
            .Select(p => new PtzPreset(
                Token: Attr(p, "token"),
                Name: OnvifText.RepairMojibake(Value(p, "Name") ?? Attr(p, "token"))))
            .ToList();
    }

    public async Task GotoPresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:GotoPreset xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            $"<tptz:PresetToken>{Escape(presetToken)}</tptz:PresetToken></tptz:GotoPreset>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GotoPreset", reqBody, ct).ConfigureAwait(false);
    }

    public async Task<string> SetPresetAsync(OnvifEndpoint endpoint, string profileToken, string name, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:SetPreset xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            $"<tptz:PresetName>{Escape(name)}</tptz:PresetName></tptz:SetPreset>";
        // retryable: false — if the camera ran the request and answered
        // garbage, a resend would create a second preset.
        var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/SetPreset", reqBody, ct, retryable: false).ConfigureAwait(false);
        // Nested the same way on some firmwares, for the same reason.
        return Descendant(body, "PresetToken")?.Value ?? string.Empty;
    }

    public async Task RemovePresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:RemovePreset xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            $"<tptz:PresetToken>{Escape(presetToken)}</tptz:PresetToken></tptz:RemovePreset>";
        // retryable: false — a resend after a successful-but-unreadable remove
        // would fault on the now-missing preset and report failure for a
        // removal that worked.
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/RemovePreset", reqBody, ct, retryable: false).ConfigureAwait(false);
    }

    // --- PTZ capability, steps and home ------------------------------------

    public async Task<PtzCapabilities> GetPtzCapabilitiesAsync(
        OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);

        var configToken = await ResolvePtzConfigurationTokenAsync(endpoint, profileToken, ct).ConfigureAwait(false);
        if (configToken is null) return PtzCapabilities.ContinuousOnly;

        XElement options;
        try
        {
            var reqBody =
                $"<tptz:GetConfigurationOptions xmlns:tptz=\"{Tptz}\">" +
                $"<tptz:ConfigurationToken>{Escape(configToken)}</tptz:ConfigurationToken>" +
                "</tptz:GetConfigurationOptions>";
            options = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetConfigurationOptions", reqBody, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Optional operation. A camera that will not describe itself gets
            // the profile this app assumed of every camera before it asked.
            return PtzCapabilities.ContinuousOnly;
        }

        var spaces = Descendant(options, "Spaces");
        // Cameras often declare both the generic translation space and the FOV
        // one; the FOV one is what makes a step feel the same at any zoom.
        var relativeSpaces = spaces?.Descendants()
            .Where(e => e.Name.LocalName == "RelativePanTiltTranslationSpace").ToList();
        var relativePanTilt = relativeSpaces?.FirstOrDefault(IsFovSpace) ?? relativeSpaces?.FirstOrDefault();
        var relativeZoom = spaces is null ? null : Descendant(spaces, "RelativeZoomTranslationSpace");
        var absoluteZoom = spaces is null ? null : Descendant(spaces, "AbsoluteZoomPositionSpace");
        var continuousPanTilt = spaces is null ? null : Descendant(spaces, "ContinuousPanTiltVelocitySpace");
        var continuousZoom = spaces is null ? null : Descendant(spaces, "ContinuousZoomVelocitySpace");

        // A space URI ending in TranslationSpaceFov means a step is a fraction
        // of the current field of view, so one press covers the same part of
        // the picture at any zoom.
        var fov = relativePanTilt is not null && IsFovSpace(relativePanTilt);

        // Home is not advertised among the spaces; the node knows. A camera
        // that cannot answer gets no home button rather than one that fails.
        var (homeSupported, homeFixed) = await ReadHomeSupportAsync(ptz, endpoint, configToken, ct).ConfigureAwait(false);

        return new PtzCapabilities(
            SupportsContinuousPanTilt: continuousPanTilt is not null,
            SupportsContinuousZoom: continuousZoom is not null,
            SupportsRelativePanTilt: relativePanTilt is not null,
            SupportsRelativeZoom: relativeZoom is not null,
            SupportsAbsolute: absoluteZoom is not null,
            SupportsHome: homeSupported,
            // A fixed home position exists but cannot be overwritten.
            SupportsSetHome: homeSupported && !homeFixed,
            SupportsMoveStatus: await SupportsMoveStatusAsync(ptz, endpoint, ct).ConfigureAwait(false),
            RelativeIsFieldOfView: fov,
            RelativePan: RangeOf(relativePanTilt, "XRange"),
            RelativeTilt: RangeOf(relativePanTilt, "YRange"),
            RelativeZoom: RangeOf(relativeZoom, "XRange"),
            AbsoluteZoom: RangeOf(absoluteZoom, "XRange"),
            AuxiliaryCommands: Array.Empty<string>());
    }

    // Whether the node supports home at all, and whether its home position is
    // fixed by hardware: GetConfiguration names the node, GetNode describes it.
    // Both are reads a camera may refuse; refusing means no home controls, the
    // same as before the buttons existed.
    private async Task<(bool Supported, bool Fixed)> ReadHomeSupportAsync(
        Uri ptz, OnvifEndpoint endpoint, string configToken, CancellationToken ct)
    {
        try
        {
            var conf = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetConfiguration",
                $"<tptz:GetConfiguration xmlns:tptz=\"{Tptz}\">" +
                $"<tptz:PTZConfigurationToken>{Escape(configToken)}</tptz:PTZConfigurationToken>" +
                "</tptz:GetConfiguration>", ct).ConfigureAwait(false);
            var nodeToken = Descendant(conf, "NodeToken")?.Value;
            if (string.IsNullOrEmpty(nodeToken)) return (false, false);

            var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetNode",
                $"<tptz:GetNode xmlns:tptz=\"{Tptz}\">" +
                $"<tptz:NodeToken>{Escape(nodeToken)}</tptz:NodeToken></tptz:GetNode>", ct).ConfigureAwait(false);
            var node = Descendant(body, "PTZNode");
            if (node is null) return (false, false);

            return (XmlBool(Descendant(node, "HomeSupported")?.Value),
                    XmlBool(Attr(node, "FixedHomePosition")));
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    private static bool IsFovSpace(XElement space) =>
        (Value(space, "URI") ?? "").Contains("TranslationSpaceFov", StringComparison.OrdinalIgnoreCase);

    // xs:boolean allows both spellings.
    private static bool XmlBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    // Axes the caller left at zero are omitted rather than sent as zero: the
    // spec reads an absent element as "leave this axis alone", while an
    // explicit zero is a command some cameras act on.
    public async Task RelativeMoveAsync(
        OnvifEndpoint endpoint, string profileToken, PtzVelocity step, float speed, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);

        var panTilt = step.PanX != 0f || step.TiltY != 0f
            ? $"<tt:PanTilt x=\"{Num(step.PanX)}\" y=\"{Num(step.TiltY)}\"/>"
            : "";
        var zoom = step.Zoom != 0f ? $"<tt:Zoom x=\"{Num(step.Zoom)}\"/>" : "";
        if (panTilt.Length == 0 && zoom.Length == 0) return;

        var speedXml = speed > 0f
            ? "<tptz:Speed>" +
              (panTilt.Length > 0 ? $"<tt:PanTilt x=\"{Num(speed)}\" y=\"{Num(speed)}\"/>" : "") +
              (zoom.Length > 0 ? $"<tt:Zoom x=\"{Num(speed)}\"/>" : "") +
              "</tptz:Speed>"
            : "";

        var reqBody =
            $"<tptz:RelativeMove xmlns:tptz=\"{Tptz}\" xmlns:tt=\"{Tt}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            $"<tptz:Translation>{panTilt}{zoom}</tptz:Translation>" +
            speedXml +
            "</tptz:RelativeMove>";
        // retryable: false — re-sending a translation the camera may already
        // have executed is a double step.
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/RelativeMove", reqBody, ct, retryable: false).ConfigureAwait(false);
    }

    // Positions come back exactly as the camera reported them, in its own
    // declared units — see PtzStatus for why they are not normalized here.
    public async Task<PtzStatus> GetPtzStatusAsync(
        OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:GetStatus xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken></tptz:GetStatus>";
        var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetStatus", reqBody, ct).ConfigureAwait(false);

        var position = Descendant(body, "Position");
        var panTilt = position is null ? null : Descendant(position, "PanTilt");
        var zoom = position is null ? null : Descendant(position, "Zoom");
        var move = Descendant(body, "MoveStatus");

        return new PtzStatus(
            Pan: ParseFloat(Attr(panTilt, "x")) ?? 0f,
            Tilt: ParseFloat(Attr(panTilt, "y")) ?? 0f,
            Zoom: ParseFloat(Attr(zoom, "x")) ?? 0f,
            PanTilt: MoveStateOf(move, "PanTilt"),
            ZoomState: MoveStateOf(move, "Zoom"),
            UtcTime: DateTime.TryParse(Descendant(body, "UtcTime")?.Value, out var utc) ? utc : null);
    }

    public async Task GotoHomeAsync(
        OnvifEndpoint endpoint, string profileToken, float speed, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var speedXml = speed > 0f
            ? $"<tptz:Speed><tt:PanTilt x=\"{Num(speed)}\" y=\"{Num(speed)}\"/></tptz:Speed>"
            : "";
        var reqBody =
            $"<tptz:GotoHomePosition xmlns:tptz=\"{Tptz}\" xmlns:tt=\"{Tt}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>{speedXml}" +
            "</tptz:GotoHomePosition>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GotoHomePosition", reqBody, ct).ConfigureAwait(false);
    }

    public async Task SetHomeAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:SetHomePosition xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken></tptz:SetHomePosition>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/SetHomePosition", reqBody, ct).ConfigureAwait(false);
    }

    // MoveStatus is optional, and several firmwares 400 on the call that
    // reports whether they keep it. Not knowing is the same as not having it.
    private async Task<bool> SupportsMoveStatusAsync(Uri ptz, OnvifEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/GetServiceCapabilities",
                $"<tptz:GetServiceCapabilities xmlns:tptz=\"{Tptz}\"/>", ct).ConfigureAwait(false);
            var caps = Descendant(body, "Capabilities");
            return caps is not null && XmlBool(Attr(caps, "MoveStatus"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string?> ResolvePtzConfigurationTokenAsync(
        OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var profiles = await GetProfilesAsync(endpoint, ct).ConfigureAwait(false);
        foreach (var profile in profiles)
            if (profile.Token == profileToken)
                return profile.PtzConfigurationToken;
        return null;
    }

    private static PtzRange RangeOf(XElement? space, string axis)
    {
        if (space is null) return PtzRange.Normalized;
        var range = Descendant(space, axis);
        if (range is null) return PtzRange.Normalized;
        var min = ParseFloat(Value(range, "Min"));
        var max = ParseFloat(Value(range, "Max"));
        return min is null || max is null ? PtzRange.Normalized : new PtzRange(min.Value, max.Value);
    }

    private static PtzMoveState MoveStateOf(XElement? moveStatus, string axis) =>
        (moveStatus is null ? null : Value(moveStatus, axis))?.ToUpperInvariant() switch
        {
            "IDLE" => PtzMoveState.Idle,
            "MOVING" => PtzMoveState.Moving,
            _ => PtzMoveState.Unknown,
        };

    private static float? ParseFloat(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // --- Transport ----------------------------------------------------------

    private enum ServiceKind { Media, Ptz }

    // Media/PTZ calls go to the XAddr from GetCapabilities — but only once it has
    // proven itself: some firmwares advertise the wrong service there (issue #67),
    // so a candidate must answer a read-only call of its own service first. The
    // first that does is cached for the endpoint. If none does, fall back to the
    // advertised XAddr, else the device endpoint (onvif_simple_server often
    // serves all at one URI), and let the real call report the real error.
    private async Task<Uri> ResolveServiceAsync(OnvifEndpoint endpoint, ServiceKind kind, CancellationToken ct)
    {
        var key = $"{kind}\u0000{endpoint.DeviceServiceUri}";
        if (_serviceByDevice.TryGetValue(key, out var known))
            return known;

        XElement? caps;
        try
        {
            caps = await GetCapabilitiesElementAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "ONVIF capability lookup failed; using device endpoint for {Kind}", kind);
            return endpoint.DeviceServiceUri;
        }

        var advertised = TryUri(XAddrOf(caps, kind == ServiceKind.Media ? "Media" : "PTZ"));
        var all = caps?.Descendants().Where(e => e.Name.LocalName == "XAddr")
            .Select(e => TryUri(e.Value)).OfType<Uri>().ToList() ?? new List<Uri>();
        var candidates = kind == ServiceKind.Media
            ? OnvifServiceCandidates.Media(endpoint.DeviceServiceUri, advertised, all)
            : OnvifServiceCandidates.Ptz(endpoint.DeviceServiceUri, advertised, all);

        foreach (var candidate in candidates)
        {
            if (!await AnswersAsync(candidate, endpoint, kind, ct).ConfigureAwait(false))
                continue;
            if (candidate != advertised)
                _logger.LogInformation("ONVIF {Kind} service answers at {Uri}, not the advertised {Advertised}",
                    kind, candidate, advertised?.ToString() ?? "(none)");
            _serviceByDevice[key] = candidate;
            return candidate;
        }

        _logger.LogDebug("No ONVIF {Kind} service candidate answered; using {Uri}",
            kind, advertised ?? endpoint.DeviceServiceUri);
        return advertised ?? endpoint.DeviceServiceUri;
    }

    // One cheap read-only call every implementation of the service must support:
    // GetProfiles for Media, GetNodes for PTZ. The response element has to match,
    // so a different service that happens to reply doesn't pass for this one.
    private async Task<bool> AnswersAsync(Uri service, OnvifEndpoint endpoint, ServiceKind kind, CancellationToken ct)
    {
        var (action, body, expected) = kind == ServiceKind.Media
            ? ($"{Trt}/GetProfiles", $"<trt:GetProfiles xmlns:trt=\"{Trt}\"/>", "GetProfilesResponse")
            : ($"{Tptz}/GetNodes", $"<tptz:GetNodes xmlns:tptz=\"{Tptz}\"/>", "GetNodesResponse");

        // CallAuthedAsync ran GetCapabilities just before, so the shift and the
        // SOAP dialect are both known. A plain CallAsync skips the fault retry,
        // and retryable: false skips the dialect retry — either would only
        // double the cost of every wrong candidate, and a wrong path answering
        // garbage must not teach the host the wrong dialect.
        _shiftByHost.TryGetValue(endpoint.DeviceServiceUri.Host, out var shift);
        try
        {
            var response = await CallAsync(service, action, body, endpoint.Credentials, shift, retryable: false, ct).ConfigureAwait(false);
            return response.Name.LocalName == expected;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "ONVIF {Kind} candidate {Uri} did not answer", kind, service);
            return false;
        }
    }

    // Authenticated call with a per-host clock shift; on a fault, refresh the
    // shift once and retry (covers a stale/absent offset causing digest rejection).
    private async Task<XElement> CallAuthedAsync(Uri service, OnvifEndpoint endpoint, string action, string body, CancellationToken ct, bool retryable = true)
    {
        var host = endpoint.DeviceServiceUri.Host;
        if (!_shiftByHost.TryGetValue(host, out var shift))
        {
            // Also where the host's SOAP dialect gets discovered, since this
            // probe runs before the first real call — so by the time a mutation
            // goes out, the dialect is already known.
            shift = await GetTimeShiftAsync(endpoint.DeviceServiceUri, ct).ConfigureAwait(false);
            _shiftByHost[host] = shift;
        }

        try
        {
            return await CallAsync(service, action, body, endpoint.Credentials, shift, retryable, ct).ConfigureAwait(false);
        }
        catch (OnvifFaultException)
        {
            // Maybe the clock drifted / the first shift was wrong — recompute and
            // retry once. Safe for mutations too: a fault means the camera
            // refused the request, not that it ran it.
            var fresh = await GetTimeShiftAsync(endpoint.DeviceServiceUri, ct).ConfigureAwait(false);
            _shiftByHost[host] = fresh;
            return await CallAsync(service, action, body, endpoint.Credentials, fresh, retryable, ct).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> GetTimeShiftAsync(Uri deviceService, CancellationToken ct)
    {
        try
        {
            var body = await CallAsync(deviceService, $"{Tds}/GetSystemDateAndTime",
                $"<tds:GetSystemDateAndTime xmlns:tds=\"{Tds}\"/>",
                credentials: null, shift: TimeSpan.Zero, retryable: true, ct).ConfigureAwait(false);

            var utc = Descendant(body, "UTCDateTime");
            var date = Child(utc, "Date");
            var time = Child(utc, "Time");
            if (date is null || time is null)
                return TimeSpan.Zero;

            var cameraUtc = new DateTime(
                Int(date, "Year"), Int(date, "Month"), Int(date, "Day"),
                Int(time, "Hour"), Int(time, "Minute"), Int(time, "Second"),
                DateTimeKind.Utc);
            return cameraUtc - DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetSystemDateAndTime failed; assuming zero clock shift");
            return TimeSpan.Zero;
        }
    }

    private async Task<XElement> CallAsync(Uri service, string action, string body, CameraCredentials? credentials, TimeSpan shift, bool retryable, CancellationToken ct)
    {
        // SOAP 1.2 first — the version ONVIF specifies — unless this host has
        // already shown it only answers 1.1. A camera that answers the first
        // choice with nothing usable gets one retry in the other dialect, which
        // several firmwares need and which costs one request to find out. The
        // winner is remembered per host, so the discovery happens once.
        //
        // Except for mutations (retryable: false). An unusable response does
        // not prove the request was not executed — a camera that ran SetPreset
        // and then answered garbage would get a duplicate preset from a resend.
        // Mutations rely on the dialect already learned from this host's
        // earlier read calls (the clock probe at minimum) and fail honestly
        // rather than guessing.
        var address = $"{service.Host}:{service.Port}";
        var soap12First = !_soap11Hosts.ContainsKey(address);
        var (status, text) = await SendAsync(service, action, body, credentials, shift, soap12: soap12First, ct)
            .ConfigureAwait(false);

        if (!IsUsable(text) && retryable)
        {
            _logger.LogDebug("ONVIF {Action}: SOAP {First} gave HTTP {Status} and {Length} bytes; retrying as SOAP {Second}",
                action, soap12First ? "1.2" : "1.1", (int)status, text.Length, soap12First ? "1.1" : "1.2");
            (status, text) = await SendAsync(service, action, body, credentials, shift, soap12: !soap12First, ct)
                .ConfigureAwait(false);

            if (IsUsable(text))
            {
                // The other dialect is the one this host speaks; remember it in
                // whichever direction the flip went.
                if (soap12First) _soap11Hosts[address] = 1;
                else _soap11Hosts.TryRemove(address, out _);
            }
        }

        // A refused login usually comes back as an HTML error page or nothing,
        // not a fault — say so rather than blaming an "empty body".
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !IsUsable(text))
            throw new InvalidOperationException(
                $"ONVIF {action}: the camera refused the login (HTTP {(int)status}). Check the username and password.");
        if (string.IsNullOrWhiteSpace(text)) throw EmptyBody(action, status);

        XElement root;
        try { root = XDocument.Parse(text).Root!; }
        catch (Exception ex) { throw new InvalidOperationException($"ONVIF {action}: malformed response", ex); }

        var bodyEl = Child(Child(root, "Body"), null);
        if (bodyEl is null)
        {
            _logger.LogDebug("ONVIF {Action}: HTTP {Status}, body: {Body}",
                action, (int)status, text.Length > 400 ? text[..400] : text);
            throw EmptyBody(action, status);
        }
        if (bodyEl.Name.LocalName == "Fault")
        {
            var reason = Descendant(bodyEl, "Text")?.Value
                ?? Descendant(bodyEl, "faultstring")?.Value
                ?? "unknown fault";
            throw new OnvifFaultException($"ONVIF fault for {action}: {reason}");
        }
        return bodyEl;
    }

    private async Task<(HttpStatusCode Status, string Text)> SendAsync(
        Uri service, string action, string body, CameraCredentials? credentials,
        TimeSpan shift, bool soap12, CancellationToken ct)
    {
        var header = SecurityHeader(credentials, shift);
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            $"<s:Envelope xmlns:s=\"{(soap12 ? Soap : Soap11)}\">{header}<s:Body>{body}</s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, service);
        req.Headers.ConnectionClose = true;
        if (credentials is { } c && !string.IsNullOrEmpty(c.Username))
        {
            // Preemptive Basic for onvif_simple_server, which enforces it at the
            // transport and never challenges. A camera that wants Digest answers
            // 401 instead, and the handler's credentials settle that exchange.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var content = new StringContent(envelope, Encoding.UTF8);
        if (soap12)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{action}\""));
        }
        else
        {
            // SOAP 1.1 has no action parameter on the content type; it travels
            // in a header of its own.
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");
        }
        req.Content = content;

        using var resp = await ClientFor(service, credentials)
            .SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) ?? string.Empty);
    }

    // Worth reading: it parses, and its Body holds something. A firmware built
    // for SOAP 1.1 typically answers a 1.2 request with no bytes at all or with
    // an envelope whose Body is empty, and both mean "ask again differently".
    // A fault is a usable answer — a camera that says why it refused is not
    // asked twice — except VersionMismatch, which is exactly how a gSOAP
    // firmware built for 1.1 rejects a 1.2 envelope when it doesn't stay silent.
    private static bool IsUsable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        XElement? body;
        try { body = Child(Child(XDocument.Parse(text).Root!, "Body"), null); }
        catch (Exception) { return false; }
        if (body is null) return false;
        if (body.Name.LocalName != "Fault") return true;
        var code = Descendant(body, "faultcode")?.Value ?? Descendant(body, "Value")?.Value ?? string.Empty;
        return !code.Contains("VersionMismatch", StringComparison.Ordinal);
    }

    // An empty body is what a camera sends when it will not say why. In
    // practice it means ONVIF is switched off in the camera's own settings or
    // the account has no ONVIF rights — neither of which arrives as a fault, so
    // the message has to name them. The status code is the only other clue.
    private static InvalidOperationException EmptyBody(string action, HttpStatusCode status) =>
        new($"ONVIF {action}: the camera returned an empty SOAP body (HTTP {(int)status}). " +
            "Check that ONVIF is enabled on the camera and that this account may use it.");

    private static string SecurityHeader(CameraCredentials? credentials, TimeSpan shift)
    {
        if (credentials is not { } c || string.IsNullOrEmpty(c.Username))
            return string.Empty;

        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.Add(shift).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var pwdBytes = Encoding.UTF8.GetBytes(c.Password ?? string.Empty);

        var buf = new byte[nonce.Length + createdBytes.Length + pwdBytes.Length];
        Buffer.BlockCopy(nonce, 0, buf, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, buf, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(pwdBytes, 0, buf, nonce.Length + createdBytes.Length, pwdBytes.Length);
        var digest = Convert.ToBase64String(SHA1.HashData(buf));

        return
            $"<s:Header><wsse:Security s:mustUnderstand=\"1\" xmlns:wsse=\"{Wsse}\" xmlns:wsu=\"{Wsu}\">" +
            "<wsse:UsernameToken>" +
            $"<wsse:Username>{Escape(c.Username)}</wsse:Username>" +
            $"<wsse:Password Type=\"{PwDigestType}\">{digest}</wsse:Password>" +
            $"<wsse:Nonce EncodingType=\"{Base64Type}\">{Convert.ToBase64String(nonce)}</wsse:Nonce>" +
            $"<wsu:Created>{created}</wsu:Created>" +
            "</wsse:UsernameToken></wsse:Security></s:Header>";
    }

    // --- XML helpers (namespace-agnostic: match by local name) --------------

    // Child by local name; localName == null returns the first child element.
    private static XElement? Child(XElement? parent, string? localName) =>
        parent?.Elements().FirstOrDefault(e => localName is null || e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    private static XElement? Descendant(XElement? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Value(XElement? parent, string localName) =>
        Child(parent, localName)?.Value;

    private static string Attr(XElement? el, string name) =>
        el?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? string.Empty;

    private static int Int(XElement? parent, string localName) =>
        int.TryParse(Value(parent, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // XAddr under a named capability section (Media / PTZ) — scoped so we don't
    // grab another service's XAddr.
    private static string? XAddrOf(XElement? capabilities, string section) =>
        Value(Child(capabilities, section), "XAddr");

    private static Uri? TryUri(string? value) =>
        string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var u) ? null : u;

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : SecurityElement.Escape(value);

    private static string Num(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

    private static string XmlConvertDuration(TimeSpan t) =>
        System.Xml.XmlConvert.ToString(t);

    private sealed class OnvifFaultException(string message) : Exception(message);
}
