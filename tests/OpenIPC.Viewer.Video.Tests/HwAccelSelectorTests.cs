using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video.Tests;

public sealed class HwAccelSelectorTests
{
    private static readonly NullLogger<HwAccelSelectorTests> Log = NullLogger<HwAccelSelectorTests>.Instance;

    [Fact]
    public void None_AlwaysReturnsNone()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.None, new StubFactory(HwAccelHint.D3d11Va, available: true), Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    [Fact]
    public void Auto_WithNoFactory_FallsBackToSoftware()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.Auto, factory: null, Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    [Fact]
    public void Auto_WithAvailableFactory_PicksFactoryKind()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.Auto, new StubFactory(HwAccelHint.VaApi, available: true), Log);
        Assert.Equal(HwAccelHint.VaApi, result);
    }

    [Fact]
    public void Auto_WithUnavailableFactory_FallsBackToSoftware()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.Auto, new StubFactory(HwAccelHint.VaApi, available: false), Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    [Fact]
    public void ExplicitHint_MatchingPlatformAndAvailable_UsesIt()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.VideoToolbox, new StubFactory(HwAccelHint.VideoToolbox, available: true), Log);
        Assert.Equal(HwAccelHint.VideoToolbox, result);
    }

    [Fact]
    public void ExplicitHint_OnWrongPlatform_FallsBackToSoftware()
    {
        // Asking for VaApi on a system whose factory is D3D11VA → never try VAAPI.
        var result = HwAccelSelector.Resolve(HwAccelHint.VaApi, new StubFactory(HwAccelHint.D3d11Va, available: true), Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    [Fact]
    public void ExplicitHint_MatchingButUnavailable_FallsBackToSoftware()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.D3d11Va, new StubFactory(HwAccelHint.D3d11Va, available: false), Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    [Fact]
    public void ExplicitHint_NoFactory_FallsBackToSoftware()
    {
        var result = HwAccelSelector.Resolve(HwAccelHint.D3d11Va, factory: null, Log);
        Assert.Equal(HwAccelHint.None, result);
    }

    private sealed class StubFactory : IHwDecoderFactory
    {
        private readonly bool _available;
        public HwAccelHint Kind { get; }
        public StubFactory(HwAccelHint kind, bool available) { Kind = kind; _available = available; }
        public HwProbeResult Probe() => _available
            ? HwProbeResult.Ok()
            : HwProbeResult.Unavailable("test stub says no");
    }
}
