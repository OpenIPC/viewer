using System;
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
    StreamQualityOverride StreamQualityOverride = StreamQualityOverride.Auto);
