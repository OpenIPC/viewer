using System;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Entities;

public sealed record UpdateCameraRequest(
    string Name,
    string Host,
    int HttpPort,
    int? OnvifPort,
    Uri RtspMainUri,
    Uri? RtspSubUri,
    CameraCredentials? Credentials,
    GroupId? GroupId = null,
    StreamQualityOverride StreamQualityOverride = StreamQualityOverride.Auto,
    // SSH device suite (Phase 13). Null SshCredentials keeps the stored SSH
    // login untouched (mirrors how null Credentials keeps the main login).
    CameraCredentials? SshCredentials = null,
    int? SshPort = null,
    // Null keeps the stored analytics config untouched.
    AnalyticsSettings? Analytics = null);
