using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Web;

namespace OpenIPC.Viewer.App.Services;

// Parses the three supported QR payload shapes into a single QrPayload record
// the Library page can hand to the CameraEditor:
//
//   1. rtsp://[user[:pass]@]host[:port]/path  — bare RTSP URL
//   2. JSON object: {"name","rtsp","onvif","username","password"} — all opt
//   3. openipc-viewer://camera?host=...&rtsp=...&onvif=...&user=...&pass=...&name=...
//
// "Loose" parsing on purpose — QRs are user-supplied and we'd rather pre-fill
// what we can and let the editor's validators reject the rest than refuse
// outright on a single missing field.
public static class QrPayloadParser
{
    public static QrPayload? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (text.StartsWith("openipc-viewer://", StringComparison.OrdinalIgnoreCase))
            return ParseCustomScheme(text);
        if (text.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            return ParseRtsp(text);
        if (text.StartsWith("{", StringComparison.Ordinal))
            return ParseJson(text);

        return null;
    }

    private static QrPayload? ParseRtsp(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

        string? user = null, pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        // Strip user-info from the URI we surface — credentials live in the
        // editor's Username/Password fields, not embedded in the saved RTSP.
        var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;

        return new QrPayload(
            Name: null,
            Host: uri.Host,
            OnvifPort: null,
            HttpPort: null,
            RtspMain: clean.ToString(),
            Username: user,
            Password: pass);
    }

    private static QrPayload? ParseJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            return new QrPayload(
                Name: GetString(root, "name"),
                Host: GetString(root, "host"),
                OnvifPort: GetInt(root, "onvif"),
                HttpPort: GetInt(root, "http"),
                RtspMain: GetString(root, "rtsp"),
                Username: GetString(root, "username") ?? GetString(root, "user"),
                Password: GetString(root, "password") ?? GetString(root, "pass"));
        }
        catch (JsonException) { return null; }
    }

    private static QrPayload? ParseCustomScheme(string text)
    {
        // openipc-viewer://camera?host=...&rtsp=...&onvif=...&user=...&pass=...&name=...
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var q = HttpUtility.ParseQueryString(uri.Query);
        return new QrPayload(
            Name: q["name"],
            Host: q["host"],
            OnvifPort: TryParseInt(q["onvif"]),
            HttpPort: TryParseInt(q["http"]),
            RtspMain: q["rtsp"],
            Username: q["user"] ?? q["username"],
            Password: q["pass"] ?? q["password"]);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out var i) ? i : null;
}

public sealed record QrPayload(
    string? Name,
    string? Host,
    int? OnvifPort,
    int? HttpPort,
    string? RtspMain,
    string? Username,
    string? Password);
