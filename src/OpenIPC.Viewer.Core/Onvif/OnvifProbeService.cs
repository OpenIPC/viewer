using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Onvif;

// Orchestrates the GetCapabilities / GetDeviceInfo / GetProfiles / GetStreamUri
// chain used by the onboarding flow (phase-04 §4.3) to produce one
// OnvifProbeResult that the Add-camera dialog can save straight to the DB.
public sealed class OnvifProbeService
{
    private readonly IOnvifClient _client;

    public OnvifProbeService(IOnvifClient client)
    {
        _client = client;
    }

    public async Task<OnvifProbeResult> ProbeAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var caps = await _client.GetCapabilitiesAsync(endpoint, ct).ConfigureAwait(false);
        var info = await _client.GetDeviceInformationAsync(endpoint, ct).ConfigureAwait(false);
        var profiles = await _client.GetProfilesAsync(endpoint, ct).ConfigureAwait(false);

        if (profiles.Count == 0)
            throw new InvalidOperationException("Camera returned zero media profiles");

        // Prefer the profile that carries a PTZ configuration token — that's the one the
        // joystick will drive. Falls back to the first profile for non-PTZ cameras.
        var profile = profiles.FirstOrDefault(p => p.PtzConfigurationToken is not null) ?? profiles[0];

        var streamUri = await _client.GetStreamUriAsync(endpoint, profile.Token, ct).ConfigureAwait(false);

        return new OnvifProbeResult(
            RtspMainUri: streamUri,
            ProfileToken: profile.Token,
            HasPtz: caps.HasPtz && profile.PtzConfigurationToken is not null,
            Manufacturer: info.Manufacturer,
            Model: info.Model,
            FirmwareVersion: info.FirmwareVersion,
            // Audio capability is camera-wide, so OR across every profile (the
            // backchannel may live on a different profile than the main stream).
            HasAudioIn: profiles.Any(p => p.HasAudioIn),
            HasAudioOut: profiles.Any(p => p.HasAudioOut));
    }
}
