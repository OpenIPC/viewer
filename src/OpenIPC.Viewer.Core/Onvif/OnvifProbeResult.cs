using System;

namespace OpenIPC.Viewer.Core.Onvif;

public sealed record OnvifProbeResult(
    Uri RtspMainUri,
    string ProfileToken,
    bool HasPtz,
    string? Manufacturer,
    string? Model,
    string? FirmwareVersion,
    // Audio capabilities across the camera's profiles (Phase 17): a mic to listen
    // to, a speaker to talk back through (Profile T backchannel).
    bool HasAudioIn = false,
    bool HasAudioOut = false);
