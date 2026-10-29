using System.Net.Http.Headers;
using System.Text.Json;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.Infrastructure.Updates;

// Public builds: the repository's latest GitHub release. `releases/latest` skips drafts and
// pre-releases, so an rc tag never reaches users of a final build. Unauthenticated calls are
// limited to 60 an hour per IP, far above one check per launch.
public sealed class GithubReleaseFeed : IUpdateFeed
{
    private readonly HttpClient _http;
    private readonly string _repository;

    public GithubReleaseFeed(HttpClient http, string repository)
    {
        _http = http;
        _repository = repository;
    }

    public string DisplayName => "GitHub";

    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"https://api.github.com/repos/{_repository}/releases/latest"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects API calls without a User-Agent.
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenIPC-Viewer", "1"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        // 404 = the repository has no published release yet.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json);
    }

    public static UpdateRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!AppVersion.TryParse(Str(root, "tag_name"), out var version)) return null;
        // releases/latest already skips releases marked pre-release; this also catches a tag
        // like -preview1 that the release workflow didn't mark.
        if (version.IsPreRelease) return null;

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttps(uri)) continue;
                if (ClassifyAsset(name) is not { } target) continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                // "sha256:<hex>" — GitHub computes it for every uploaded asset.
                var digest = Str(a, "digest");
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..].ToLowerInvariant()
                    : null;
                assets.Add(new UpdateAsset(target.Platform, target.Arch, name, uri, size, sha));
            }
        }

        var page = Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var p) && IsHttps(p) ? p : null;
        var published = DateTimeOffset.TryParse(Str(root, "published_at"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        return new UpdateRelease(version, page, published, assets);
    }

    // Asset names as .github/workflows/build.yml uploads them:
    //   openipc-viewer-win-x64.zip, openipc-viewer-linux-x64.tar.gz, openipc-viewer-osx-arm64.tar.gz,
    //   org.openipc.viewer-Signed.apk (arm64), org.openipc.viewer-{armv7,x64}-Signed.apk.
    public static (string Platform, string Arch)? ClassifyAsset(string name)
    {
        if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            var arch = name.Contains("-armv7", StringComparison.OrdinalIgnoreCase) ? "armv7"
                : name.Contains("-x64", StringComparison.OrdinalIgnoreCase) ? "x64"
                : "arm64";
            return (UpdatePlatforms.Android, arch);
        }
        if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.Windows, "x64");
        if (name.Contains("win-arm64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.Windows, "arm64");
        if (name.Contains("linux-x64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.Linux, "x64");
        if (name.Contains("linux-arm64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.Linux, "arm64");
        if (name.Contains("osx-arm64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.MacOs, "arm64");
        if (name.Contains("osx-x64", StringComparison.OrdinalIgnoreCase)) return (UpdatePlatforms.MacOs, "x64");
        return null;
    }

    private static bool IsHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
