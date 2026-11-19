using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.LinkHealth;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

public sealed class FailureDomainAnalyzerTests
{
    private static readonly CameraId A = new(Guid.NewGuid());
    private static readonly CameraId B = new(Guid.NewGuid());
    private static readonly CameraId C = new(Guid.NewGuid());
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static ConnectionEvent Drop(CameraId id, double minutes, double seconds = 0) =>
        new(id, T0.AddMinutes(minutes).AddSeconds(seconds), ConnectionEventKind.Dropped);

    [Fact]
    public void NoDrops_NotEnoughData()
    {
        var r = FailureDomainAnalyzer.Analyze(new[] { new ConnectionEvent(A, T0, ConnectionEventKind.FailedAttempt) });
        Assert.Equal(FailureDomainVerdict.NotEnoughData, r.Verdict);
        Assert.Equal(0, r.Incidents);
    }

    [Fact]
    public void CamerasDroppingTogether_PointAtSharedSegment()
    {
        var events = new[]
        {
            Drop(A, 0), Drop(B, 0, 3), Drop(C, 0, 8),
            Drop(A, 60), Drop(B, 60, 1), Drop(C, 60, 2),
            Drop(A, 120), Drop(C, 120, 5),
        };
        var r = FailureDomainAnalyzer.Analyze(events);
        Assert.Equal(FailureDomainVerdict.SharedSegment, r.Verdict);
        Assert.Equal(3, r.Incidents);
        Assert.Equal(3, r.SharedIncidents);
        Assert.Equal(3, r.Cameras.Single(p => p.CameraId == A).SharedDrops);
    }

    [Fact]
    public void CamerasDroppingAlone_PointAtBranches()
    {
        var events = new[] { Drop(A, 0), Drop(A, 30), Drop(B, 70), Drop(A, 90) };
        var r = FailureDomainAnalyzer.Analyze(events);
        Assert.Equal(FailureDomainVerdict.IndividualBranches, r.Verdict);
        var a = r.Cameras.First();
        Assert.Equal(A, a.CameraId);
        Assert.Equal(3, a.SoloDrops);
    }

    [Fact]
    public void BothKinds_IsMixed()
    {
        var events = new[]
        {
            Drop(A, 0), Drop(B, 0, 2),
            Drop(A, 10), Drop(B, 10, 2),
            Drop(C, 20), Drop(C, 30), Drop(C, 40), Drop(A, 50),
        };
        Assert.Equal(FailureDomainVerdict.Mixed, FailureDomainAnalyzer.Analyze(events).Verdict);
    }

    [Fact]
    public void Window_SeparatesIncidents()
    {
        var events = new[] { Drop(A, 0), Drop(B, 0, 30) };
        Assert.Equal(2, FailureDomainAnalyzer.Analyze(events, TimeSpan.FromSeconds(20)).Incidents);
        Assert.Equal(1, FailureDomainAnalyzer.Analyze(events, TimeSpan.FromSeconds(40)).Incidents);
    }
}
