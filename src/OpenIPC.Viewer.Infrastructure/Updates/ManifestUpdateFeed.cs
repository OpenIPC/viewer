using System.Text.Json;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.Infrastructure.Updates;

// A release manifest on a web server, for builds not published on GitHub. File names in it are
// relative to the manifest's folder:
//   { "version": "0.3.17-vendor1", "publishedAt": "…", "pageUrl": "…" (optional),
//     "files": [ { "platform": "android", "label": "arm64", "file": "…-arm64.apk",
//                  "size": 123, "sha256": "…" }, … ] }
public sealed class ManifestUpdateFeed : IUpdateFeed
{
    private readonly HttpClient _http;
    private readonly Uri _manifest;

    public ManifestUpdateFeed(HttpClient http, Uri manifest)
    {
        _http = http;
        _manifest = manifest;
    }

    public string DisplayName => _manifest.Host;

    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_manifest, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json, _manifest);
    }

    public static UpdateRelease? Parse(string json, Uri manifestUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!AppVersion.TryParse(Str(root, "version"), out var version)) return null;

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                var file = Str(f, "file");
                var platform = Str(f, "platform")?.ToLowerInvariant();
                if (string.IsNullOrEmpty(file) || platform is null) continue;
                // "file" may be absolute; whatever it resolves to must still be https.
                if (!Uri.TryCreate(manifestUrl, file, out var url) || !IsHttps(url)) continue;
                var size = f.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var sha = Str(f, "sha256")?.ToLowerInvariant();
                assets.Add(new UpdateAsset(platform, ArchOf(file, Str(f, "label")), file, url, size, sha));
            }
        }

        // The page goes straight to the OS URL handler: a web page or nothing, never file:/custom schemes.
        var page = Uri.TryCreate(Str(root, "pageUrl"), UriKind.Absolute, out var p) && IsHttps(p) ? p : null;
        var published = DateTimeOffset.TryParse(Str(root, "publishedAt"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        return new UpdateRelease(version, page, published, assets);
    }

    // Android entries carry the ABI as their label; desktop ones only in the file name
    // ("…-win-x64.zip"), their label is display text ("Windows x64").
    private static string ArchOf(string file, string? label)
    {
        foreach (var arch in new[] { "arm64", "armv7", "x64" })
        {
            if (string.Equals(label, arch, StringComparison.OrdinalIgnoreCase)) return arch;
        }
        if (file.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return "arm64";
        if (file.Contains("armv7", StringComparison.OrdinalIgnoreCase)) return "armv7";
        return "x64";
    }

    private static bool IsHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps;

    // Tolerates any element kind: a stray number or string in "files" is skipped, not a throw.
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
