using System.Reflection;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.Infrastructure.Updates;

// Picks the update feed this build was made for. The choice is the UpdateFeed MSBuild property
// (Directory.Build.props), stamped into every assembly as metadata, so builds for different
// distribution channels differ by one property instead of by code:
//   github:<owner>/<repo>       → GitHub Releases
//   https://…/latest.json       → a release manifest on a web server
//   none                        → no update checks (F-Droid and other store builds)
public static class UpdateFeedFactory
{
    public const string MetadataKey = "OpenIpc.UpdateFeed";

    public static string? ConfiguredSpec =>
        typeof(UpdateFeedFactory).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value;

    public static IUpdateFeed? Create(string? spec, HttpClient http)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        spec = spec.Trim();
        if (string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase)) return null;

        if (spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var repo = spec["github:".Length..].Trim('/');
            return repo.Contains('/', StringComparison.Ordinal) ? new GithubReleaseFeed(http, repo) : null;
        }

        return Uri.TryCreate(spec, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new ManifestUpdateFeed(http, uri)
            : null;
    }
}
