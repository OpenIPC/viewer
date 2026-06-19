using System;
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
    StreamQualityOverride StreamQualityOverride = StreamQualityOverride.Auto);
