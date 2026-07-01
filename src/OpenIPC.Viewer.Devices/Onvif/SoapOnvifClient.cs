using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// Auth mirrors the old builder: preemptive HTTP Basic (OpenIPC's
/// onvif_simple_server enforces it at the transport) plus a WS-Security
/// UsernameToken password digest. GetSystemDateAndTime (unauthenticated) yields
/// the camera clock offset the digest's Created stamp needs; it's cached per host.
/// </summary>
public sealed class SoapOnvifClient : IOnvifClient
{
    private const string Soap = "http://www.w3.org/2003/05/soap-envelope";
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

    public SoapOnvifClient(ILogger<SoapOnvifClient> logger)
    {
        _logger = logger;
        // onvif_simple_server is CGI-style: one request per connection, then it
        // closes the socket. Disable pooling so we never reuse a dead socket.
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = CallTimeout,
        })
        {
            Timeout = CallTimeout,
        };
    }

    // --- Device service -----------------------------------------------------

    public async Task<OnvifCapabilities> GetCapabilitiesAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var body = await CallAuthedAsync(endpoint.DeviceServiceUri, endpoint,
            $"{Tds}/GetCapabilities",
            $"<tds:GetCapabilities xmlns:tds=\"{Tds}\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
            ct).ConfigureAwait(false);

        var caps = Child(body, "Capabilities");
        return new OnvifCapabilities(
            MediaServiceUri: TryUri(XAddrOf(caps, "Media")),
            PtzServiceUri: TryUri(XAddrOf(caps, "PTZ")));
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
        var uri = Value(body, "Uri");
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
            .Select(p => new PtzPreset(Token: Attr(p, "token"), Name: Value(p, "Name") ?? Attr(p, "token")))
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
        var body = await CallAuthedAsync(ptz, endpoint, $"{Tptz}/SetPreset", reqBody, ct).ConfigureAwait(false);
        return Value(body, "PresetToken") ?? string.Empty;
    }

    public async Task RemovePresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct)
    {
        var ptz = await ResolveServiceAsync(endpoint, ServiceKind.Ptz, ct).ConfigureAwait(false);
        var reqBody =
            $"<tptz:RemovePreset xmlns:tptz=\"{Tptz}\">" +
            $"<tptz:ProfileToken>{Escape(profileToken)}</tptz:ProfileToken>" +
            $"<tptz:PresetToken>{Escape(presetToken)}</tptz:PresetToken></tptz:RemovePreset>";
        await CallAuthedAsync(ptz, endpoint, $"{Tptz}/RemovePreset", reqBody, ct).ConfigureAwait(false);
    }

    // --- Transport ----------------------------------------------------------

    private enum ServiceKind { Media, Ptz }

    // Media/PTZ calls go to the XAddr from GetCapabilities; fall back to the
    // device service endpoint (onvif_simple_server often serves all at one URI).
    private async Task<Uri> ResolveServiceAsync(OnvifEndpoint endpoint, ServiceKind kind, CancellationToken ct)
    {
        try
        {
            var caps = await GetCapabilitiesAsync(endpoint, ct).ConfigureAwait(false);
            var uri = kind == ServiceKind.Media ? caps.MediaServiceUri : caps.PtzServiceUri;
            if (uri is not null)
                return uri;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ONVIF capability lookup failed; using device endpoint for {Kind}", kind);
        }
        return endpoint.DeviceServiceUri;
    }

    // Authenticated call with a per-host clock shift; on a fault, refresh the
    // shift once and retry (covers a stale/absent offset causing digest rejection).
    private async Task<XElement> CallAuthedAsync(Uri service, OnvifEndpoint endpoint, string action, string body, CancellationToken ct)
    {
        var host = endpoint.DeviceServiceUri.Host;
        if (!_shiftByHost.TryGetValue(host, out var shift))
        {
            shift = await GetTimeShiftAsync(endpoint.DeviceServiceUri, ct).ConfigureAwait(false);
            _shiftByHost[host] = shift;
        }

        try
        {
            return await CallAsync(service, action, body, endpoint.Credentials, shift, ct).ConfigureAwait(false);
        }
        catch (OnvifFaultException)
        {
            // Maybe the clock drifted / the first shift was wrong — recompute and retry once.
            var fresh = await GetTimeShiftAsync(endpoint.DeviceServiceUri, ct).ConfigureAwait(false);
            _shiftByHost[host] = fresh;
            return await CallAsync(service, action, body, endpoint.Credentials, fresh, ct).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> GetTimeShiftAsync(Uri deviceService, CancellationToken ct)
    {
        try
        {
            var body = await CallAsync(deviceService, $"{Tds}/GetSystemDateAndTime",
                $"<tds:GetSystemDateAndTime xmlns:tds=\"{Tds}\"/>",
                credentials: null, shift: TimeSpan.Zero, ct).ConfigureAwait(false);

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

    private async Task<XElement> CallAsync(Uri service, string action, string body, CameraCredentials? credentials, TimeSpan shift, CancellationToken ct)
    {
        var header = SecurityHeader(credentials, shift);
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            $"<s:Envelope xmlns:s=\"{Soap}\">{header}<s:Body>{body}</s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, service);
        req.Headers.ConnectionClose = true;
        if (credentials is { } c && !string.IsNullOrEmpty(c.Username))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{action}\""));
        req.Content = content;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"ONVIF {action}: empty response (HTTP {(int)resp.StatusCode})");

        XElement root;
        try { root = XDocument.Parse(text).Root!; }
        catch (Exception ex) { throw new InvalidOperationException($"ONVIF {action}: malformed response", ex); }

        var bodyEl = Child(Child(root, "Body"), null);
        if (bodyEl is null)
            throw new InvalidOperationException($"ONVIF {action}: empty SOAP body");
        if (bodyEl.Name.LocalName == "Fault")
        {
            var reason = Descendant(bodyEl, "Text")?.Value
                ?? Descendant(bodyEl, "faultstring")?.Value
                ?? "unknown fault";
            throw new OnvifFaultException($"ONVIF fault for {action}: {reason}");
        }
        return bodyEl;
    }

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
