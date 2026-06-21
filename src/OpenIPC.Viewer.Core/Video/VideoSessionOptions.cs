using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Video;

public sealed record VideoSessionOptions(
    Uri RtspUri,
    CameraCredentials? Credentials,
    RtspTransport Transport,
    HwAccelHint HwAccel,
    TimeSpan NetworkCaching,
    bool AutoReconnect = true,
    // Decode the RTSP audio track too (Phase 17.1). Off by default so a grid of
    // tiles never burns CPU/traffic decoding audio for cameras nobody is
    // listening to — the single-camera page opts in.
    bool EnableAudio = false)
{
    public static VideoSessionOptions Default(Uri uri, CameraCredentials? creds = null) =>
        new(uri, creds, RtspTransport.Tcp, HwAccelHint.Auto, TimeSpan.FromMilliseconds(150));
}
