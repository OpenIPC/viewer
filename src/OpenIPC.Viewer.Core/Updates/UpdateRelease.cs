using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenIPC.Viewer.Core.Updates;

/// <summary>Platform names shared by the feeds and the running app.</summary>
public static class UpdatePlatforms
{
    public const string Windows = "windows";
    public const string Linux = "linux";
    public const string MacOs = "macos";
    public const string Android = "android";
    public const string Ios = "ios";
}

/// <summary>One downloadable build of a release.</summary>
/// <param name="Platform">One of <see cref="UpdatePlatforms"/>.</param>
/// <param name="Arch">"x64", "arm64" or "armv7".</param>
/// <param name="Sha256">Lower-case hex, or null when the feed publishes no hash.</param>
public sealed record UpdateAsset(
    string Platform,
    string Arch,
    string FileName,
    Uri Url,
    long Size,
    string? Sha256);

/// <summary>The newest release a feed offers.</summary>
/// <param name="PageUrl">Release notes / download page, when the feed has one.</param>
public sealed record UpdateRelease(
    AppVersion Version,
    Uri? PageUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateAsset? FindAsset(string platform, string arch) =>
        Assets.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.Ordinal) &&
            string.Equals(a.Arch, arch, StringComparison.Ordinal));
}
