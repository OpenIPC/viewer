using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Web.Api;

// The browser-safe projection of a Camera. It exposes NOTHING secret:
//   * no camera password (it's not on the entity — only secret-store key refs,
//     which we also don't expose; HasCredentials is a plain bool instead);
//   * RTSP URIs are sanitized — any embedded user:pass@ is stripped so a URL the
//     user typed with credentials never leaves the server.
// This is the contract the §20.3 "golden" test guards against regressions.
public sealed record CameraDto(
    string Id,
    string Name,
    string Host,
    int HttpPort,
    int? OnvifPort,
    int? GroupId,
    string? GroupName,
    string RtspMain,
    string? RtspSub,
    bool OnvifEnabled,
    string? ChipModel,
    string? FirmwareVersion,
    bool IncludedInGrid,
    bool HasPtz,
    bool PtzReady,
    bool IsMajestic,
    bool HasAudioIn,
    bool HasAudioOut,
    bool HasCredentials,
    string StreamQuality,
    int SortOrder)
{
    public static CameraDto From(Camera c, string? groupName) => new(
        Id: c.Id.ToString(),
        Name: c.Name,
        Host: c.Host,
        HttpPort: c.HttpPort,
        OnvifPort: c.OnvifPort,
        GroupId: c.GroupId?.Value,
        GroupName: groupName,
        RtspMain: SanitizeUri(c.RtspMainUri),
        RtspSub: c.RtspSubUri is null ? null : SanitizeUri(c.RtspSubUri),
        OnvifEnabled: c.OnvifEnabled,
        ChipModel: c.ChipModel,
        FirmwareVersion: c.FirmwareVersion,
        IncludedInGrid: c.IncludedInGrid,
        HasPtz: c.HasPtz,
        // HasPtz alone only says the camera advertises PTZ; the ONVIF calls also
        // need a media profile token from the probe. Without it the SPA shows the
        // capability badge but no controls (and /ptz/* answers 409).
        PtzReady: c.HasPtz && !string.IsNullOrEmpty(c.OnvifProfileToken),
        IsMajestic: c.IsMajestic,
        HasAudioIn: c.HasAudioIn,
        HasAudioOut: c.HasAudioOut,
        HasCredentials: c.UsernameRef is not null,
        StreamQuality: c.StreamQualityOverride.ToString(),
        SortOrder: c.SortOrder);

    // Drops any embedded credentials (user:pass@) from an RTSP URI so they never
    // cross the API boundary, even if the camera was configured with them inline.
    public static string SanitizeUri(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
            return uri.ToString();
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
    }
}
