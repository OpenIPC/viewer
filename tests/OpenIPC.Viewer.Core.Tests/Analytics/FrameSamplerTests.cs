using OpenIPC.Viewer.Core.Analytics;

namespace OpenIPC.Viewer.Core.Tests.Analytics;

// Wall-clock rate gate for the analytics tap (Phase 15.3).
public sealed class FrameSamplerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstFrame_IsAlwaysAdmitted()
    {
        var sampler = new FrameSampler(3);
        Assert.True(sampler.ShouldSample(T0));
    }

    [Fact]
    public void AdmitsAtTargetRate_DropsFramesInBetween()
    {
        var sampler = new FrameSampler(3); // one frame every ~333ms
        Assert.True(sampler.ShouldSample(T0));
        Assert.False(sampler.ShouldSample(T0.AddMilliseconds(100)));
        Assert.False(sampler.ShouldSample(T0.AddMilliseconds(300)));
        Assert.True(sampler.ShouldSample(T0.AddMilliseconds(334)));
        Assert.False(sampler.ShouldSample(T0.AddMilliseconds(400)));
        Assert.True(sampler.ShouldSample(T0.AddMilliseconds(700)));
    }

    [Theory]
    [InlineData(0, 1)]   // clamped up to 1 fps
    [InlineData(-5, 1)]
    [InlineData(100, 30)] // clamped down to 30 fps
    [InlineData(3, 3)]
    public void ClampsTargetFpsToSaneRange(int requested, int expected)
    {
        Assert.Equal(expected, new FrameSampler(requested).TargetFps);
    }

    [Fact]
    public void Reset_ReadmitsImmediately()
    {
        var sampler = new FrameSampler(1);
        Assert.True(sampler.ShouldSample(T0));
        Assert.False(sampler.ShouldSample(T0.AddMilliseconds(500)));
        sampler.Reset();
        Assert.True(sampler.ShouldSample(T0.AddMilliseconds(600)));
    }

    [Fact]
    public void EmptyClassFilter_MapsToAllClasses()
    {
        var settings = new AnalyticsSettings(Enabled: true, ClassIds: System.Array.Empty<int>());
        Assert.Null(settings.ToDetectOptions().ClassFilter);
    }

    [Fact]
    public void SelectedClasses_FlowToDetectOptions()
    {
        var settings = new AnalyticsSettings(Enabled: true, ClassIds: new[] { 0, 2 }, ConfidenceThreshold: 0.6f);
        var opts = settings.ToDetectOptions();
        Assert.Equal(0.6f, opts.ConfidenceThreshold, 3);
        Assert.Equal(new[] { 0, 2 }, opts.ClassFilter);
    }
}
