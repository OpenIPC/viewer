using OpenIPC.Viewer.Core.Status;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Status;

// Pure Health Center logic: worst-first ordering and the online/attention/offline
// rollup, extracted from the view model so it tests without a directory or probe.
public sealed class HealthRollupTests
{
    [Theory]
    [InlineData(CameraStatus.Offline, 0)]
    [InlineData(CameraStatus.Attention, 1)]
    [InlineData(CameraStatus.Connecting, 2)]
    [InlineData(CameraStatus.Unknown, 3)]
    [InlineData(CameraStatus.Online, 4)]
    public void SortRank_WorstFirst(CameraStatus status, int expectedRank)
    {
        Assert.Equal(expectedRank, HealthRollup.SortRank(status));
    }

    [Fact]
    public void SortRank_ProblemsOutrankOnline()
    {
        Assert.True(HealthRollup.SortRank(CameraStatus.Offline) < HealthRollup.SortRank(CameraStatus.Online));
        Assert.True(HealthRollup.SortRank(CameraStatus.Attention) < HealthRollup.SortRank(CameraStatus.Online));
    }

    [Fact]
    public void Counts_BucketsByStatus_FoldingUnknownIntoOffline()
    {
        var (online, attention, offline) = HealthRollup.Counts(new[]
        {
            CameraStatus.Online, CameraStatus.Online,
            CameraStatus.Attention,
            CameraStatus.Offline, CameraStatus.Unknown,
            CameraStatus.Connecting, // transient — counted in no bucket
        });

        Assert.Equal(2, online);
        Assert.Equal(1, attention);
        Assert.Equal(2, offline); // Offline + Unknown
    }
}
