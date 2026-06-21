namespace OpenIPC.Viewer.Core.Onvif;

// HasAudioIn = the profile carries an audio encoder config (camera mic → we can
// listen). HasAudioOut = an audio output config (camera speaker → Profile T
// backchannel target, we can talk). Phase 17.4/17.5.
public sealed record MediaProfile(
    string Token,
    string Name,
    string? PtzConfigurationToken,
    bool HasAudioIn = false,
    bool HasAudioOut = false);
