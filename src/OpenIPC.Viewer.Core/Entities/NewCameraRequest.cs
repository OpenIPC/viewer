using System;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Entities;

public sealed record NewCameraRequest(
    string Name,
    string Host,
    int HttpPort,
    int? OnvifPort,
    Uri RtspMainUri,
    Uri? RtspSubUri,
    CameraCredentials? Credentials,
    GroupId? GroupId = null,
    StreamQualityOverride StreamQualityOverride = StreamQualityOverride.Auto,
    // SSH device suite (Phase 13). Separate from the RTSP/ONVIF credentials —
    // the SSH login may differ. Null SshCredentials means "no SSH-specific
    // login" (the resolver falls back to the main credentials).
    CameraCredentials? SshCredentials = null,
    int? SshPort = null,
    AnalyticsSettings? Analytics = null);
