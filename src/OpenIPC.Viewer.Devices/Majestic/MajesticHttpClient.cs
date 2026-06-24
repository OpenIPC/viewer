using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Majestic;

namespace OpenIPC.Viewer.Devices.Majestic;

// Talks to OpenIPC-Majestic over HTTP. Single HttpClient is reused across
// cameras; Basic auth is attached per-request from MajesticEndpoint.Credentials
// (HttpClientHandler.Credentials would lock us into one cred set globally).
//
// 5-second timeout per request (phase-05 §5.3). On 401 we surface a typed
// exception so the UI can ask the user to re-enter credentials instead of
// silently retrying and racking up failed auth attempts.
public sealed class MajesticHttpClient : IMajesticClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger<MajesticHttpClient> _logger;

    public MajesticHttpClient(ILogger<MajesticHttpClient> logger)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };
        _logger = logger;
    }

    public async Task<bool> PingAsync(MajesticEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            // Probe config.json, not info.json: some firmwares don't serve
            // info.json and fall back to the web UI's HTML index (200 OK,
            // "<!DOCTYPE html>"), which fails the JSON sniff and hides an
            // otherwise working Majestic. config.json is the canonical endpoint
            // and always returns the live config object.
            using var resp = await SendAsync(endpoint, HttpMethod.Get, "api/v1/config.json", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return LooksLikeMajesticConfig(body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Majestic ping failed for {Host}", endpoint.Host);
            return false;
        }
    }

    // A Majestic config root always carries these top-level sections; checking
    // for one avoids treating an unrelated JSON endpoint as a Majestic camera.
    private static bool LooksLikeMajesticConfig(string body)
    {
        if (!body.TrimStart().StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("system", out _)
                || doc.RootElement.TryGetProperty("video0", out _)
                || doc.RootElement.TryGetProperty("rtsp", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<MajesticConfig> GetConfigAsync(MajesticEndpoint endpoint, CancellationToken ct)
    {
        using var resp = await SendAsync(endpoint, HttpMethod.Get, "api/v1/config.json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(rawJson);
        return ParseConfig(rawJson, doc.RootElement);
    }

    public async Task<MajesticInfo> GetInfoAsync(MajesticEndpoint endpoint, CancellationToken ct)
    {
        using var resp = await SendAsync(endpoint, HttpMethod.Get, "api/v1/info.json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // Some firmwares don't serve info.json and return the web UI's HTML
        // index with 200 OK. Treat a non-JSON body as "no info available"
        // rather than letting the parse error abort the whole state load —
        // info is optional metadata (model/firmware), config.json is the part
        // that matters.
        if (!rawJson.TrimStart().StartsWith('{'))
        {
            _logger.LogDebug("Majestic info.json for {Host} was not JSON; skipping", endpoint.Host);
            return new MajesticInfo(null, null, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            return new MajesticInfo(
                Model: TryGetString(root, "model"),
                FirmwareVersion: TryGetString(root, "firmware") ?? TryGetString(root, "build"),
                ChipModel: TryGetString(root, "chip") ?? TryGetString(root, "soc"),
                Uptime: TryGetString(root, "uptime"));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Majestic info.json parse failed for {Host}", endpoint.Host);
            return new MajesticInfo(null, null, null, null);
        }
    }

    public async Task UpdateConfigAsync(MajesticEndpoint endpoint, MajesticConfigPatch patch, CancellationToken ct)
    {
        // Read current config so we POST a full payload (read-modify-write).
        // Partial POSTs work on some Majestic builds and break others; safer
        // to send the merged whole.
        using var getResp = await SendAsync(endpoint, HttpMethod.Get, "api/v1/config.json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(getResp, ct).ConfigureAwait(false);
        var rawJson = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var root = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new InvalidOperationException("Majestic config root is not a JSON object");

        var video = (root["video0"] ?? root["video"]) as JsonObject;
        if (video is not null)
        {
            if (patch.Codec is { } codec) video["codec"] = codec;
            if (patch.Fps is { } fps) video["fps"] = fps;
            if (patch.Resolution is { } size) video["size"] = size;
            if (patch.Bitrate is { } br) video["bitrate"] = br;
            if (patch.Profile is { } prof) video["profile"] = prof;
        }

        // Majestic exposes RTMP push under root.rtmp = { enabled, url }. Some
        // builds ship without the section at all — create the object on first
        // write so a user can flip it on from a fresh config.
        if (patch.RtmpEnabled is not null || patch.RtmpUrl is not null)
        {
            if (root["rtmp"] is not JsonObject rtmp)
            {
                rtmp = new JsonObject();
                root["rtmp"] = rtmp;
            }
            if (patch.RtmpEnabled is { } en) rtmp["enabled"] = en;
            if (patch.RtmpUrl is { } url) rtmp["url"] = url;
        }

        // Audio capture lives under root.audio = { enabled }. Create on first
        // write so a user can turn the mic on from a build that shipped it off.
        if (patch.AudioEnabled is { } audioEnabled)
        {
            if (root["audio"] is not JsonObject audio)
            {
                audio = new JsonObject();
                root["audio"] = audio;
            }
            audio["enabled"] = audioEnabled;
        }

        var body = root.ToJsonString();
        using var postReq = BuildRequest(endpoint, HttpMethod.Post, "api/v1/config.json");
        postReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var postResp = await _http.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(postResp, ct).ConfigureAwait(false);
    }

    public async Task UpdateRawConfigAsync(MajesticEndpoint endpoint, string rawJson, CancellationToken ct)
    {
        // Cheap well-formedness check — Majestic's config endpoint accepts
        // anything that parses but a typo (trailing comma, unclosed brace)
        // can brick the camera, so we'd rather refuse early than POST garbage.
        try { using var _ = JsonDocument.Parse(rawJson); }
        catch (JsonException ex) { throw new ArgumentException("Invalid JSON: " + ex.Message, nameof(rawJson), ex); }

        using var postReq = BuildRequest(endpoint, HttpMethod.Post, "api/v1/config.json");
        postReq.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        using var postResp = await _http.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(postResp, ct).ConfigureAwait(false);
    }

    public async Task SetNightModeAsync(MajesticEndpoint endpoint, NightMode mode, CancellationToken ct)
    {
        var path = mode switch
        {
            NightMode.Day => "night/off",
            NightMode.Night => "night/on",
            NightMode.Auto => "night/auto",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        using var resp = await SendAsync(endpoint, HttpMethod.Get, path, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> SnapshotJpegAsync(MajesticEndpoint endpoint, MajesticSnapshotOptions options, CancellationToken ct)
    {
        var path = "image.jpg";
        if (options.Width is { } w && options.Height is { } h)
            path = $"image.jpg?width={w}&height={h}";
        else if (options.Width is { } w2)
            path = $"image.jpg?width={w2}";

        using var resp = await SendAsync(endpoint, HttpMethod.Get, path, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildRequest(MajesticEndpoint ep, HttpMethod method, string relativePath)
    {
        var uri = new Uri(ep.BaseUri, relativePath);
        var req = new HttpRequestMessage(method, uri);
        if (ep.Credentials is { } c)
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{c.Username}:{c.Password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(MajesticEndpoint ep, HttpMethod method, string relativePath, CancellationToken ct)
    {
        using var req = BuildRequest(ep, method, relativePath);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        if ((int)resp.StatusCode == 401)
            throw new MajesticAuthException();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new MajesticHttpException((int)resp.StatusCode, body);
    }

    private static MajesticConfig ParseConfig(string rawJson, JsonElement root)
    {
        // Majestic groups video knobs under video0 / video1 (mainstream / sub).
        // We surface video0 since it's what most users care about; substream
        // edits are deferred to phase-05c.
        var video = TryGetObject(root, "video0") ?? TryGetObject(root, "video");
        var isp = TryGetObject(root, "isp");
        var rtmp = TryGetObject(root, "rtmp");
        var audio = TryGetObject(root, "audio");

        var nightMode = NightMode.Unknown;
        var ircut = isp is { } i ? TryGetString(i, "ircut") : null;
        if (ircut is not null)
        {
            if (ircut.Equals("on", StringComparison.OrdinalIgnoreCase)) nightMode = NightMode.Night;
            else if (ircut.Equals("off", StringComparison.OrdinalIgnoreCase)) nightMode = NightMode.Day;
            else if (ircut.Equals("auto", StringComparison.OrdinalIgnoreCase)) nightMode = NightMode.Auto;
        }

        return new MajesticConfig(
            RawJson: rawJson,
            Codec: video is { } v1 ? TryGetString(v1, "codec") : null,
            Fps: video is { } v2 ? TryGetInt(v2, "fps") : null,
            Resolution: video is { } v3 ? TryGetString(v3, "size") : null,
            Bitrate: video is { } v4 ? TryGetInt(v4, "bitrate") : null,
            Profile: video is { } v5 ? TryGetString(v5, "profile") : null,
            NightMode: nightMode,
            RtmpEnabled: rtmp is { } r1 ? TryGetBool(r1, "enabled") : null,
            RtmpUrl: rtmp is { } r2 ? TryGetString(r2, "url") : null,
            AudioEnabled: audio is { } a1 ? TryGetBool(a1, "enabled") : null);
    }

    private static JsonElement? TryGetObject(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public void Dispose() => _http.Dispose();
}

public sealed class MajesticAuthException : Exception
{
    public MajesticAuthException() : base("Majestic returned 401 Unauthorized") { }
}

public sealed class MajesticHttpException : Exception
{
    public int StatusCode { get; }
    public string Body { get; }
    public MajesticHttpException(int statusCode, string body) : base($"Majestic returned HTTP {statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}
