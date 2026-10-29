using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.App.Services;

public enum UpdateCheckStatus
{
    /// <summary>The build has no update feed (UpdateFeed=none).</summary>
    Disabled,
    UpToDate,
    Available,
    /// <summary>A newer release exists, but without a build for this platform.</summary>
    NoBuildForPlatform,
    Failed,
}

public sealed record UpdateCheckOutcome(UpdateCheckStatus Status, UpdateRelease? Release = null, string? Error = null);

// Update notifications: asks the build's feed for the newest release and, when it is newer and has
// a build for this platform, publishes it as Available for the banner. Nothing is installed — the
// user downloads the archive (desktop) or the APK (via the browser on mobile) and replaces the app
// themselves. Call from the UI thread: continuations come back to it, so Changed fires there.
public sealed class UpdateService
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IUpdateFeed? _feed;
    private readonly IUpdateDownloader _downloader;
    private readonly UserSettingsService _settings;
    private readonly ILogger<UpdateService> _logger;
    private bool _backgroundStarted;

    public UpdateService(
        IUpdateFeed? feed,
        IUpdateDownloader downloader,
        UserSettingsService settings,
        ILogger<UpdateService> logger)
    {
        _feed = feed;
        _downloader = downloader;
        _settings = settings;
        _logger = logger;
        AppVersion.TryParse(VersionText, out var current);
        Current = current;
    }

    public bool IsEnabled => _feed is not null;
    public string? FeedName => _feed?.DisplayName;
    public bool SupportsInAppDownload => _downloader.SupportsInAppDownload;

    // Same git-derived string as Settings → About. Null when it doesn't parse (a build from a tree
    // without tags): such a build is never told it is out of date.
    public static string VersionText =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? string.Empty;
    public AppVersion? Current { get; }

    /// <summary>The release the banner offers, or null.</summary>
    public UpdateRelease? Available { get; private set; }
    public UpdateAsset? AvailableAsset { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Starts the launch check and the daily re-check. Idempotent.</summary>
    public void StartBackgroundChecks()
    {
        if (_backgroundStarted || _feed is null) return;
        _backgroundStarted = true;
        _ = BackgroundLoopAsync();
    }

    private async Task BackgroundLoopAsync()
    {
        await Task.Delay(FirstCheckDelay).ConfigureAwait(true);
        while (true)
        {
            // Re-read each round: the toggle in Settings applies without a restart.
            if (_settings.Current.CheckForUpdates)
                await CheckAsync(manual: false, CancellationToken.None).ConfigureAwait(true);
            await Task.Delay(CheckInterval).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// A manual check ignores a skipped version: the user asked, so they get the answer.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckAsync(bool manual, CancellationToken ct)
    {
        if (_feed is null) return new UpdateCheckOutcome(UpdateCheckStatus.Disabled);

        UpdateRelease? latest;
        try
        {
            latest = await _feed.GetLatestAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or the feed is down — routine for a background check, so no stack trace.
            _logger.LogInformation("Update check against {Feed} failed: {Error}", _feed.DisplayName, ex.Message);
            return new UpdateCheckOutcome(UpdateCheckStatus.Failed, Error: ex.Message);
        }

        if (latest is null || Current is null || latest.Version <= Current)
        {
            _logger.LogInformation("Update check: {Current} is current ({Feed} has {Latest})",
                VersionText, _feed.DisplayName, latest?.Version.ToString() ?? "nothing");
            Publish(null, null);
            return new UpdateCheckOutcome(UpdateCheckStatus.UpToDate, latest);
        }

        var (platform, arch) = _downloader.CurrentTarget;
        var asset = latest.FindAsset(platform, arch);
        if (asset is null)
        {
            _logger.LogInformation("Update {Latest} has no build for {Platform}/{Arch}", latest.Version, platform, arch);
            Publish(null, null);
            return new UpdateCheckOutcome(UpdateCheckStatus.NoBuildForPlatform, latest);
        }

        _logger.LogInformation("Update available: {Current} → {Latest} ({File})", VersionText, latest.Version, asset.FileName);
        var skipped = !manual && string.Equals(_settings.Current.SkippedUpdateVersion, latest.Version.ToString(), StringComparison.Ordinal);
        Publish(skipped ? null : latest, skipped ? null : asset, force: manual);
        return new UpdateCheckOutcome(UpdateCheckStatus.Available, latest);
    }

    /// <summary>"Skip this version": hide it until a newer release comes out.</summary>
    public async Task SkipAvailableAsync()
    {
        if (Available is null) return;
        var version = Available.Version.ToString();
        Publish(null, null);
        await _settings.UpdateAsync(_settings.Current with { SkippedUpdateVersion = version }).ConfigureAwait(true);
    }

    public Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken ct) =>
        AvailableAsset is { } asset
            ? _downloader.DownloadAsync(asset, progress, ct)
            : throw new InvalidOperationException("No update is available.");

    // force: a manual check re-raises Changed even for the release already on offer, so a banner
    // closed with "Later" comes back when the user asks.
    private void Publish(UpdateRelease? release, UpdateAsset? asset, bool force = false)
    {
        // The same release from a re-check keeps its objects, so the banner keeps its state
        // (a download in flight, the saved file).
        var unchanged = release is null
            ? Available is null
            : Available is not null && release.Version == Available.Version;
        if (!unchanged)
        {
            Available = release;
            AvailableAsset = asset;
        }
        else if (!force) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
