using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Events;

namespace OpenIPC.Viewer.Core.Tests.Analytics;

// Class-count summary that becomes the Detection event's Summary text (15.7).
public sealed class DetectionSummaryTests
{
    private static Detection Det(string cls) => new(0, cls, 0.9f, 0f, 0f, 0.1f, 0.1f);

    [Fact]
    public void Summarize_CountsPerClass_OrderedByDescendingCount()
    {
        var detections = new[] { Det("car"), Det("person"), Det("person") };
        Assert.Equal("person ×2, car ×1", AnalyticsMotionEventSource.Summarize(detections));
    }

    [Fact]
    public void Summarize_SingleClass()
    {
        Assert.Equal("person ×1", AnalyticsMotionEventSource.Summarize(new[] { Det("person") }));
    }
}
