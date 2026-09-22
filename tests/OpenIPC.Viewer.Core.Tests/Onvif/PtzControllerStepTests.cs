using System;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Core.Tests.Onvif;

// What one press of a step key actually sends, per capability profile. The
// review pass on this feature found three ways for it to be quietly wrong —
// values outside an asymmetric declared range, zoom conflated with pan/tilt,
// and a continuous fallback fired at cameras that declared no continuous
// space — so each is pinned against a fake client that records the calls.
public sealed class PtzControllerStepTests
{
    private static readonly OnvifEndpoint Endpoint =
        OnvifEndpoint.FromHost("127.0.0.1", 80, credentials: null);

    private static PtzCapabilities Caps(
        bool relPanTilt = false, bool relZoom = false,
        bool contPanTilt = false, bool contZoom = false,
        PtzRange? pan = null, PtzRange? tilt = null, PtzRange? zoom = null) => new(
        SupportsContinuousPanTilt: contPanTilt,
        SupportsContinuousZoom: contZoom,
        SupportsRelativePanTilt: relPanTilt,
        SupportsRelativeZoom: relZoom,
        SupportsAbsolute: false,
        SupportsHome: false,
        SupportsSetHome: false,
        SupportsMoveStatus: false,
        RelativeIsFieldOfView: false,
        RelativePan: pan ?? PtzRange.Normalized,
        RelativeTilt: tilt ?? PtzRange.Normalized,
        RelativeZoom: zoom ?? PtzRange.Normalized,
        AbsoluteZoom: PtzRange.Normalized,
        AuxiliaryCommands: Array.Empty<string>());

    private static PtzController Controller(RecordingClient client, PtzCapabilities caps) =>
        new(client, Endpoint, "Profile_1", caps);

    [Fact]
    public async Task AnArrowUsesRelativeMove_WhenThePanTiltSpaceIsDeclared()
    {
        var client = new RecordingClient();
        await Controller(client, Caps(relPanTilt: true))
            .StepAsync(new PtzVelocity(0.16f, 0f, 0f), speed: 0.6f, CancellationToken.None);

        var move = Assert.Single(client.RelativeMoves);
        Assert.Equal(0.16f, move.PanX, 4);
        Assert.Empty(client.ContinuousMoves);
    }

    // The review case: on a declared range of [0, 100] a negative step must
    // clamp to the range's own floor, never be sent below it — and an axis the
    // press never touched must stay exactly zero, not drift to a midpoint.
    [Fact]
    public async Task AsymmetricRanges_NeverProduceOutOfRangeValues_AndUntouchedAxesStayZero()
    {
        var client = new RecordingClient();
        var caps = Caps(relPanTilt: true, pan: new PtzRange(0f, 100f), tilt: new PtzRange(0f, 100f));

        await Controller(client, caps)
            .StepAsync(new PtzVelocity(-0.16f, 0f, 0f), speed: 0.6f, CancellationToken.None);

        var move = Assert.Single(client.RelativeMoves);
        Assert.True(move.PanX >= 0f, $"pan {move.PanX} sent below the declared minimum");
        Assert.Equal(0f, move.TiltY);
    }

    // Zoom is its own space. A camera with relative pan/tilt but only
    // continuous zoom steps the arrows via RelativeMove and the zoom keys via
    // the timed fallback — neither is sent an operation its axis lacks.
    [Fact]
    public async Task ZoomFallsBackToContinuous_WhenOnlyItsContinuousSpaceIsDeclared()
    {
        var client = new RecordingClient();
        var caps = Caps(relPanTilt: true, contZoom: true);

        await Controller(client, caps)
            .StepAsync(new PtzVelocity(0f, 0f, 0.16f), speed: 0.5f, CancellationToken.None);

        Assert.Empty(client.RelativeMoves);
        var (velocity, timeout) = Assert.Single(client.ContinuousMoves);
        Assert.Equal(0.08f, velocity.Zoom, 4);   // step × speed
        Assert.Equal(0f, velocity.PanX);
        Assert.NotNull(timeout);                 // self-stopping, always
    }

    // A camera that declared neither a relative nor a continuous space for the
    // axis gets nothing — not a continuous move that is guaranteed to fault.
    [Fact]
    public async Task AnAxisTheCameraCannotServe_SendsNothing()
    {
        var client = new RecordingClient();

        await Controller(client, Caps(relZoom: true))
            .StepAsync(new PtzVelocity(0.16f, 0f, 0f), speed: 0.6f, CancellationToken.None);

        Assert.Empty(client.RelativeMoves);
        Assert.Empty(client.ContinuousMoves);
    }

    // Seeded capabilities are trusted as given: no discovery call is made, so
    // the web API's per-camera cache actually saves the round trips.
    [Fact]
    public async Task SeededCapabilities_SkipDiscovery()
    {
        var client = new RecordingClient();

        await Controller(client, Caps(relPanTilt: true))
            .StepAsync(new PtzVelocity(0.16f, 0f, 0f), speed: 0.6f, CancellationToken.None);

        Assert.Equal(0, client.CapabilityReads);
    }

    private sealed class RecordingClient : IOnvifClient
    {
        public List<PtzVelocity> RelativeMoves { get; } = new();
        public List<(PtzVelocity Velocity, TimeSpan? Timeout)> ContinuousMoves { get; } = new();
        public int CapabilityReads;

        public Task RelativeMoveAsync(OnvifEndpoint endpoint, string profileToken, PtzVelocity step, float speed, CancellationToken ct)
        {
            RelativeMoves.Add(step);
            return Task.CompletedTask;
        }

        public Task ContinuousMoveAsync(OnvifEndpoint endpoint, string profileToken, PtzVelocity velocity, TimeSpan? timeout, CancellationToken ct)
        {
            ContinuousMoves.Add((velocity, timeout));
            return Task.CompletedTask;
        }

        public Task<PtzCapabilities> GetPtzCapabilitiesAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct)
        {
            CapabilityReads++;
            return Task.FromResult(PtzCapabilities.ContinuousOnly);
        }

        // The rest of the surface is not part of stepping.
        public Task<OnvifCapabilities> GetCapabilitiesAsync(OnvifEndpoint endpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<OnvifDeviceInfo> GetDeviceInformationAsync(OnvifEndpoint endpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaProfile>> GetProfilesAsync(OnvifEndpoint endpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> GetStreamUriAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct) => throw new NotSupportedException();
        public Task StopPtzAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PtzPreset>> GetPresetsAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct) => throw new NotSupportedException();
        public Task GotoPresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> SetPresetAsync(OnvifEndpoint endpoint, string profileToken, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task RemovePresetAsync(OnvifEndpoint endpoint, string profileToken, string presetToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<PtzStatus> GetPtzStatusAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct) => throw new NotSupportedException();
        public Task GotoHomeAsync(OnvifEndpoint endpoint, string profileToken, float speed, CancellationToken ct) => throw new NotSupportedException();
        public Task SetHomeAsync(OnvifEndpoint endpoint, string profileToken, CancellationToken ct) => throw new NotSupportedException();
    }
}
