using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Onvif;

// Stateless client — each call opens a fresh short-lived SOAP channel. Per arch
// 4.3 + phase-04 §4.2 notes: Onvif client-objects are NOT thread-safe and live
// short. Keeping the abstraction here means future swap (Mictlanix, SharpOnvif,
// hand-rolled SOAP) is one file in Devices.
public interface IOnvifClient
{
    Task<OnvifCapabilities> GetCapabilitiesAsync(OnvifEndpoint endpoint, CancellationToken ct);
    Task<OnvifDeviceInfo> GetDeviceInformationAsync(OnvifEndpoint endpoint, CancellationToken ct);
    Task<IReadOnlyList<MediaProfile>> GetProfilesAsync(OnvifEndpoint endpoint, CancellationToken ct);
    Task<Uri> GetStreamUriAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct);

    Task ContinuousMoveAsync(OnvifEndpoint endpoint, string profileToken, PtzVelocity velocity, TimeSpan? timeout, CancellationToken ct);
    Task StopPtzAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct);
    Task<IReadOnlyList<PtzPreset>> GetPresetsAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct);
    Task GotoPresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct);
    Task<string> SetPresetAsync(OnvifEndpoint endpoint, string profileToken, string name, CancellationToken ct);
    Task RemovePresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct);
}
