using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Onvif.Core.Client;
using Onvif.Core.Client.Common;
using Onvif.Core.Client.Device;
using Onvif.Core.Client.Media;
using Onvif.Core.Client.Ptz;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Devices.Onvif;

// Onvif.Core wrapper. Each call opens a short-lived WCF channel and closes it
// (Abort on fault). The library is stateless and clients are NOT thread-safe;
// sharing across requests is the classic ONVIF foot-gun.
public sealed class OnvifCoreClient : IOnvifClient
{
    private readonly ILogger<OnvifCoreClient> _logger;

    public OnvifCoreClient(ILogger<OnvifCoreClient> logger)
    {
        _logger = logger;
    }

    public async Task<OnvifCapabilities> GetCapabilitiesAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var device = await OpenDeviceAsync(endpoint).ConfigureAwait(false);
        try
        {
            var caps = await device.GetCapabilitiesAsync(new[] { CapabilityCategory.All }).ConfigureAwait(false);
            return new OnvifCapabilities(
                MediaServiceUri: TryUri(caps?.Capabilities?.Media?.XAddr),
                PtzServiceUri: TryUri(caps?.Capabilities?.PTZ?.XAddr));
        }
        finally
        {
            CloseQuietly(device);
        }
    }

    public async Task<OnvifDeviceInfo> GetDeviceInformationAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var device = await OpenDeviceAsync(endpoint).ConfigureAwait(false);
        try
        {
            var resp = await device.GetDeviceInformationAsync(new GetDeviceInformationRequest()).ConfigureAwait(false);
            return new OnvifDeviceInfo(
                Manufacturer: resp.Manufacturer,
                Model: resp.Model,
                FirmwareVersion: resp.FirmwareVersion,
                SerialNumber: resp.SerialNumber);
        }
        finally
        {
            CloseQuietly(device);
        }
    }

    public async Task<IReadOnlyList<MediaProfile>> GetProfilesAsync(OnvifEndpoint endpoint, CancellationToken ct)
    {
        var media = await OpenMediaAsync(endpoint).ConfigureAwait(false);
        try
        {
            var resp = await media.GetProfilesAsync().ConfigureAwait(false);
            var profiles = resp?.Profiles ?? Array.Empty<Profile>();
            return profiles
                .Select(p => new MediaProfile(
                    Token: p.token,
                    Name: p.Name,
                    PtzConfigurationToken: p.PTZConfiguration?.token))
                .ToList();
        }
        finally
        {
            CloseQuietly(media);
        }
    }

    public async Task<Uri> GetStreamUriAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var media = await OpenMediaAsync(endpoint).ConfigureAwait(false);
        try
        {
            var setup = new StreamSetup
            {
                Stream = StreamType.RTPUnicast,
                Transport = new Transport { Protocol = TransportProtocol.RTSP },
            };
            var uri = await media.GetStreamUriAsync(setup, profileToken).ConfigureAwait(false);
            if (uri?.Uri is null)
                throw new InvalidOperationException($"GetStreamUri returned null for profile {profileToken}");
            return new Uri(uri.Uri, UriKind.Absolute);
        }
        finally
        {
            CloseQuietly(media);
        }
    }

    public async Task ContinuousMoveAsync(OnvifEndpoint endpoint, string profileToken, PtzVelocity velocity, TimeSpan? timeout, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            var speed = new PTZSpeed
            {
                PanTilt = new Vector2D { x = velocity.PanX, y = velocity.TiltY },
                Zoom = new Vector1D { x = velocity.Zoom },
            };
            await ptz.ContinuousMoveAsync(profileToken, speed, timeout).ConfigureAwait(false);
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    public async Task StopPtzAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            await ptz.StopAsync(profileToken, true, true).ConfigureAwait(false);
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    public async Task<IReadOnlyList<PtzPreset>> GetPresetsAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            var resp = await ptz.GetPresetsAsync(profileToken).ConfigureAwait(false);
            var presets = resp?.Preset ?? Array.Empty<PTZPreset>();
            return presets
                .Where(p => !string.IsNullOrEmpty(p.token))
                .Select(p => new PtzPreset(Token: p.token, Name: p.Name ?? p.token))
                .ToList();
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    public async Task GotoPresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            // Speed=null → camera uses default move speed.
            await ptz.GotoPresetAsync(profileToken, presetToken, null).ConfigureAwait(false);
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    public async Task<string> SetPresetAsync(OnvifEndpoint endpoint, string profileToken, string name, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            // Empty token = create new preset; camera returns the assigned token.
            var req = new SetPresetRequest(profileToken, name, "");
            var resp = await ptz.SetPresetAsync(req).ConfigureAwait(false);
            return resp?.PresetToken ?? string.Empty;
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    public async Task RemovePresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct)
    {
        var ptz = await OpenPtzAsync(endpoint).ConfigureAwait(false);
        try
        {
            await ptz.RemovePresetAsync(profileToken, presetToken).ConfigureAwait(false);
        }
        finally
        {
            CloseQuietly(ptz);
        }
    }

    private static Task<DeviceClient> OpenDeviceAsync(OnvifEndpoint ep) =>
        OnvifClientBuilder.CreateDeviceClientAsync(ep);

    private static Task<MediaClient> OpenMediaAsync(OnvifEndpoint ep) =>
        OnvifClientBuilder.CreateMediaClientAsync(ep);

    private static Task<PTZClient> OpenPtzAsync(OnvifEndpoint ep) =>
        OnvifClientBuilder.CreatePtzClientAsync(ep);

    private static Uri? TryUri(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);

    private void CloseQuietly(ICommunicationObject co)
    {
        try
        {
            if (co.State == CommunicationState.Faulted)
                co.Abort();
            else
                co.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing ONVIF channel; aborting");
            try { co.Abort(); } catch { /* swallow */ }
        }
    }
}
