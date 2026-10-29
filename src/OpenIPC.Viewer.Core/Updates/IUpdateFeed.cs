using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Updates;

/// <summary>
/// Where the app learns about new versions. Public builds read GitHub Releases, builds published
/// elsewhere a latest.json manifest — which one is a build-time choice (the UpdateFeed MSBuild property).
/// </summary>
public interface IUpdateFeed
{
    /// <summary>Shown in logs and the About page ("GitHub", the site's host).</summary>
    string DisplayName { get; }

    /// <summary>The newest release, or null when the feed has none yet. Throws on network errors.</summary>
    Task<UpdateRelease?> GetLatestAsync(CancellationToken ct);
}

/// <summary>Fetches an update archive and checks it against the feed's hash.</summary>
public interface IUpdateDownloader
{
    /// <summary>
    /// False on mobile, where the app hands the link to the browser instead (the system installer
    /// takes an APK from there; the app cannot replace itself).
    /// </summary>
    bool SupportsInAppDownload { get; }

    /// <summary>The running app as the feeds name it — platform and architecture.</summary>
    (string Platform, string Arch) CurrentTarget { get; }

    /// <summary>
    /// Saves the asset into the user's Downloads folder and returns the file's path. Reports
    /// progress 0..1 when the size is known. Throws when the hash does not match.
    /// </summary>
    Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct);
}
