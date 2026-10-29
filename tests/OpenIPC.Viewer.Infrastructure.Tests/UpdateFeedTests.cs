using OpenIPC.Viewer.Core.Updates;
using OpenIPC.Viewer.Infrastructure.Updates;

namespace OpenIPC.Viewer.Infrastructure.Tests;

public sealed class UpdateFeedTests
{
    // Trimmed from api.github.com/repos/OpenIPC/viewer/releases/latest (v0.3.19).
    private const string GithubJson = """
        {
          "tag_name": "v0.3.19",
          "html_url": "https://github.com/OpenIPC/viewer/releases/tag/v0.3.19",
          "published_at": "2026-10-20T12:00:00Z",
          "assets": [
            { "name": "openipc-viewer-linux-x64.tar.gz", "size": 240559030,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/openipc-viewer-linux-x64.tar.gz",
              "digest": "sha256:D01E84893ED547B10C718798EFFA517715DF5B38BA25D65762D4FEA3AA7AE77D" },
            { "name": "openipc-viewer-osx-arm64.tar.gz", "size": 71498807,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/openipc-viewer-osx-arm64.tar.gz" },
            { "name": "openipc-viewer-win-x64.zip", "size": 153199216,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/openipc-viewer-win-x64.zip",
              "digest": "sha256:3a49fb5193773e4826c50a67f32ed2acfbd9bd3681834234da2883beea685657" },
            { "name": "org.openipc.viewer-Signed.apk", "size": 70449103,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/org.openipc.viewer-Signed.apk" },
            { "name": "org.openipc.viewer-x64-Signed.apk", "size": 70449103,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/org.openipc.viewer-x64-Signed.apk" },
            { "name": "notes.txt", "size": 10,
              "browser_download_url": "https://github.com/OpenIPC/viewer/releases/download/v0.3.19/notes.txt" }
          ]
        }
        """;

    [Fact]
    public void Github_release_maps_every_build_to_its_platform()
    {
        var r = GithubReleaseFeed.Parse(GithubJson)!;

        Assert.Equal("0.3.19", r.Version.ToString());
        Assert.Equal("https://github.com/OpenIPC/viewer/releases/tag/v0.3.19", r.PageUrl!.AbsoluteUri);
        Assert.Equal(5, r.Assets.Count);
        Assert.Equal("openipc-viewer-win-x64.zip", r.FindAsset(UpdatePlatforms.Windows, "x64")!.FileName);
        Assert.Equal("openipc-viewer-linux-x64.tar.gz", r.FindAsset(UpdatePlatforms.Linux, "x64")!.FileName);
        Assert.Equal("openipc-viewer-osx-arm64.tar.gz", r.FindAsset(UpdatePlatforms.MacOs, "arm64")!.FileName);
        Assert.Equal("org.openipc.viewer-Signed.apk", r.FindAsset(UpdatePlatforms.Android, "arm64")!.FileName);
        Assert.Equal("org.openipc.viewer-x64-Signed.apk", r.FindAsset(UpdatePlatforms.Android, "x64")!.FileName);
        Assert.Null(r.FindAsset(UpdatePlatforms.Android, "armv7"));
    }

    [Fact]
    public void Github_digest_becomes_a_lower_case_hash()
    {
        var r = GithubReleaseFeed.Parse(GithubJson)!;
        Assert.Equal("d01e84893ed547b10c718798effa517715df5b38ba25d65762d4fea3aa7ae77d",
            r.FindAsset(UpdatePlatforms.Linux, "x64")!.Sha256);
        Assert.Null(r.FindAsset(UpdatePlatforms.MacOs, "arm64")!.Sha256);
    }

    // A release manifest (see ManifestUpdateFeed); unknown fields are ignored.
    private const string ManifestJson = """
        {
          "version": "0.3.17-vendor1",
          "publishedAt": "2026-10-17T21:53:36+00:00",
          "files": [
            { "platform": "android", "label": "arm64", "hint": "…",
              "file": "openipc-viewer-0.3.17-vendor1-arm64.apk", "size": 36000000,
              "sha256": "1111111111111111111111111111111111111111111111111111111111111111" },
            { "platform": "android", "label": "armv7",
              "file": "openipc-viewer-0.3.17-vendor1-armv7.apk", "size": 35000000,
              "sha256": "2222222222222222222222222222222222222222222222222222222222222222" },
            { "platform": "windows", "label": "Windows x64",
              "file": "openipc-viewer-0.3.17-vendor1-win-x64.zip", "size": 205000000,
              "sha256": "3333333333333333333333333333333333333333333333333333333333333333" },
            { "platform": "linux", "label": "Linux x64",
              "file": "openipc-viewer-0.3.17-vendor1-linux-x64.tar.gz", "size": 177000000,
              "sha256": "4444444444444444444444444444444444444444444444444444444444444444" }
          ],
          "features": [ "extra" ]
        }
        """;

    [Fact]
    public void Manifest_files_resolve_next_to_the_manifest()
    {
        var url = new Uri("https://updates.example.com/viewer/latest.json");
        var r = ManifestUpdateFeed.Parse(ManifestJson, url)!;

        Assert.Equal("0.3.17-vendor1", r.Version.ToString());
        Assert.Null(r.PageUrl);
        var win = r.FindAsset(UpdatePlatforms.Windows, "x64")!;
        Assert.Equal(
            "https://updates.example.com/viewer/openipc-viewer-0.3.17-vendor1-win-x64.zip",
            win.Url.AbsoluteUri);
        Assert.Equal(205000000, win.Size);
        Assert.NotNull(r.FindAsset(UpdatePlatforms.Linux, "x64"));
        Assert.NotNull(r.FindAsset(UpdatePlatforms.Android, "armv7"));
        Assert.Null(r.FindAsset(UpdatePlatforms.Android, "x64"));
    }

    [Theory]
    [InlineData("github:OpenIPC/viewer", typeof(GithubReleaseFeed))]
    [InlineData("https://updates.example.com/viewer/latest.json", typeof(ManifestUpdateFeed))]
    public void Factory_reads_the_build_property(string spec, Type expected)
    {
        using var http = new HttpClient();
        Assert.IsType(expected, UpdateFeedFactory.Create(spec, http));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("github:viewer")]
    [InlineData("http://example.com/latest.json")]
    public void Factory_turns_checks_off_for_none_or_junk(string spec)
    {
        using var http = new HttpClient();
        Assert.Null(UpdateFeedFactory.Create(spec, http));
    }

    // Directory.Build.props stamps UpdateFeed into every assembly; without it no build would check.
    // "none" is a valid stamp (store builds), so this checks the stamp, not that a feed comes out.
    [Fact]
    public void Build_is_stamped_with_a_feed() =>
        Assert.False(string.IsNullOrEmpty(UpdateFeedFactory.ConfiguredSpec));

    [Fact]
    public void Github_skips_a_pre_release_tag_it_was_handed()
    {
        var json = GithubJson.Replace("\"v0.3.19\"", "\"v0.3.20-preview1\"", StringComparison.Ordinal);
        Assert.Null(GithubReleaseFeed.Parse(json));
    }

    [Fact]
    public void Manifest_accepts_only_https_links()
    {
        const string json = """
            {
              "version": "0.3.20",
              "pageUrl": "file:///C:/Windows/System32/calc.exe",
              "files": [
                { "platform": "windows", "file": "http://updates.example.com/a-win-x64.zip", "sha256": "11" },
                { "platform": "linux", "file": "ms-settings:privacy", "sha256": "22" },
                { "platform": "macos", "file": "https://mirror.example.com/a-osx-arm64.tar.gz", "sha256": "33" }
              ]
            }
            """;
        var r = ManifestUpdateFeed.Parse(json, new Uri("https://updates.example.com/viewer/latest.json"))!;

        Assert.Null(r.PageUrl);
        var only = Assert.Single(r.Assets);
        Assert.Equal(UpdatePlatforms.MacOs, only.Platform);
    }

    [Fact]
    public void Manifest_skips_entries_that_are_not_objects()
    {
        const string json = """
            { "version": "0.3.20", "files": [ 42, "x", null,
              { "platform": "linux", "file": "a-linux-x64.tar.gz", "sha256": "44" } ] }
            """;
        var r = ManifestUpdateFeed.Parse(json, new Uri("https://updates.example.com/viewer/latest.json"))!;
        Assert.Single(r.Assets);
    }
}
