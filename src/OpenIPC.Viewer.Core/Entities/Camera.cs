using System;

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
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);
