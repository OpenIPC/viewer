using System;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Ssh;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Entities;

public sealed record Camera(
    CameraId Id,
    GroupId? GroupId,
    string Name,
    string Host,
    int? OnvifPort,
    int HttpPort,
    Uri RtspMainUri,
    Uri? RtspSubUri,
    string? UsernameRef,
    string? PasswordRef,
    bool OnvifEnabled,
    string? OnvifProfileToken,
    string? ChipModel,
    string? FirmwareVersion,
    bool IncludedInGrid,
    bool HasPtz,
    bool IsMajestic,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Per-camera SD/HD override (Phase 12.2). Trailing param with a default so
    // existing constructor calls stay valid.
    StreamQualityOverride StreamQualityOverride = StreamQualityOverride.Auto,
    // SSH port for the device suite (Phase 13). Null → default 22. Credentials
    // live in the secrets store under cam:{id}:ssh:* keys, not on the entity.
    int? SshPort = null,
    // Per-camera AI object detection (Phase 15). Disabled by default; stored
    // flat across the Ai* columns and recomposed here.
    AnalyticsSettings? Analytics = null)
{
    /// <summary>The camera's analytics config, never null (defaults to disabled).</summary>
    public AnalyticsSettings AnalyticsOrDefault => Analytics ?? AnalyticsSettings.Disabled;

    /// <summary>The SSH port to connect to, defaulting to 22 when unset.</summary>
    public int SshPortOrDefault => SshPort ?? SshEndpoint.DefaultPort;

    /// <summary>
    /// The device's HTTP web interface URL. Omits the port when it's the
    /// default 80 (Phase 13.2 "Open in browser").
    /// </summary>
    public string WebInterfaceUrl =>
        HttpPort == 80 ? $"http://{Host}" : $"http://{Host}:{HttpPort}";
}
