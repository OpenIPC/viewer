using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Tests.Fakes;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Notifications;

// Event data reaching the outside world must stay data: no escape decoding,
// no control bytes, no extra program arguments.
public class ActionInjectionTests
{
    private static EventActionContext Ctx(string camera, string? summary = "person ×1") => new(
        new CameraEvent(EventId.New(), new CameraId(Guid.NewGuid()), EventKind.Detection, EventSeverity.Info,
            DateTime.UtcNow, null, "analytics", summary, 0.9f, 1),
        camera, "Object detected", DateTime.UtcNow);

    [Fact]
    public void Signal_EscapesInEventData_AreNotDecoded()
    {
        var bytes = SignalPayload.Build("ALARM {camera}\\r\\n", Ctx("Gate\\r\\nDISARM\\x00"));
        Assert.Equal("ALARM Gate\\r\\nDISARM\\x00\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Signal_ControlCharsInEventData_AreDropped()
    {
        var bytes = SignalPayload.Build("{camera}|", Ctx("Ga\r\nte\0"));
        Assert.Equal("Gate|", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Signal_TemplateEscapes_StillWork()
    {
        Assert.Equal(new byte[] { (byte)'A', 0x07, (byte)'B', 0x0A },
            SignalPayload.Build("A\\x07{camera}\\n", Ctx("B")));
    }

    [Fact]
    public void Signal_HexWithPlaceholder_IsRejected()
    {
        Assert.Throws<FormatException>(() => SignalPayload.Build("hex: A0 {camera}", Ctx("01")));
    }

    [Theory]
    [InlineData("\"{camera}\" {kind} \"{summary}\"", new[] { "{camera}", "{kind}", "{summary}" })]
    [InlineData("  --name \"two words\"  -x ", new[] { "--name", "two words", "-x" })]
    [InlineData("a \"\" b", new[] { "a", "", "b" })]
    [InlineData("", new string[0])]
    public void SplitArguments_BeforeRendering(string template, string[] expected)
    {
        Assert.Equal(expected, EventActionTemplate.SplitArguments(template));
    }

    [Fact]
    public void RenderedArgument_StaysOneArgument()
    {
        var hostile = "x\" & calc & \"";
        var args = EventActionTemplate.SplitArguments("\"{camera}\" {kind}");
        var first = EventActionTemplate.Render(args[0], Ctx(hostile), EventActionTemplate.StripCmdMetacharacters);
        Assert.Equal("x  calc  ", first);
        Assert.Equal(2, args.Count);
    }

    [Fact]
    public void StripCmdMetacharacters_RemovesShellSyntax()
    {
        Assert.Equal("ab cd", EventActionTemplate.StripCmdMetacharacters("a\"%&|<b> ^!c\r\nd"));
    }
}

public class CoordinatorReviewTests
{
    private static readonly DateTime Noon = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Local);

    // A channel held back by its cooldown is not "spent": the episode's next
    // publication may deliver once the cooldown is over.
    [Fact]
    public void SuppressedChannel_GetsAnotherChance_OnLaterUpdate()
    {
        var now = Noon;
        var h = Make(new Settings { NotificationCooldownSeconds = 30 }, () => now);
        var cam = new CameraId(Guid.NewGuid());

        h.Src.Emit(Ev(EventKind.Motion, cam));             // shown, cooldown starts
        now = now.AddSeconds(10);
        var second = Ev(EventKind.Motion, cam);
        h.Src.Emit(second);                                  // held back by cooldown
        Assert.Single(h.Sink.Shown);

        now = now.AddSeconds(25);                            // cooldown over, episode still going
        h.Src.Emit(second with { LastSeenAt = second.OccurredAt.AddSeconds(25) });
        Assert.Equal(2, h.Sink.Shown.Count);

        now = now.AddSeconds(1);
        h.Src.Emit(second with { LastSeenAt = second.OccurredAt.AddSeconds(26) });
        Assert.Equal(2, h.Sink.Shown.Count);                 // still once per episode
    }

    [Fact]
    public void ThrowingSink_DoesNotDropActions_AndIsReported()
    {
        var src = new Subject();
        var actions = new FakeDispatcher();
        var failures = new List<Exception>();
        using var coord = new NotificationCoordinator(src, new ThrowingSink(), new Settings { EventActions = ActionsOn() },
            () => Noon, actions);
        coord.DeliveryFailed += (_, ex) => failures.Add(ex);
        coord.Start();

        src.Emit(Ev(EventKind.Motion));

        Assert.Single(actions.Dispatched);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public void LatePublication_AfterClose_IsIgnored()
    {
        var h = Make(new Settings { NotifyMinDurationSeconds = 5 });
        var open = Ev(EventKind.Motion);

        h.Src.Emit(open with { EndedAt = open.OccurredAt.AddSeconds(1) }); // close first (too short)
        h.Src.Emit(open with { LastSeenAt = open.OccurredAt.AddSeconds(9) }); // stale/late open

        Assert.Empty(h.Sink.Shown);
    }

    [Fact]
    public void Actions_IgnoreToastMasterSwitchAndToggles()
    {
        var actions = new FakeDispatcher();
        var h = Make(new Settings { NotificationsEnabled = false, NotifyOnMotion = false, EventActions = ActionsOn() }, actions: actions);
        h.Src.Emit(Ev(EventKind.Motion));
        Assert.Empty(h.Sink.Shown);
        Assert.Single(actions.Dispatched);
    }

    [Fact]
    public void Actions_HaveTheirOwnEventTypes()
    {
        var actions = new FakeDispatcher();
        var h = Make(new Settings { EventActions = ActionsOn() with { OnMotion = false } }, actions: actions);
        h.Src.Emit(Ev(EventKind.Motion));
        h.Src.Emit(Ev(EventKind.Detection, confidence: 0.9f));
        Assert.Equal(2, h.Sink.Shown.Count);
        Assert.Equal(EventKind.Detection, Assert.Single(actions.Dispatched).Event.Kind);
    }

    private static EventActionSettings ActionsOn() => new(Enabled: true, HttpEnabled: true, HttpUrl: "http://127.0.0.1/hook");

    private static CameraEvent Ev(EventKind kind, CameraId? cam = null, float? confidence = null) => new(
        new EventId(Guid.NewGuid()), cam ?? new CameraId(Guid.NewGuid()), kind, EventSeverity.Info,
        Noon.ToUniversalTime(), null, "test", "summary", confidence, confidence is null ? 0 : 1);

    private sealed record Harness(Subject Src, FakeSink Sink, NotificationCoordinator Coord);

    private static Harness Make(Settings s, Func<DateTime>? clock = null, IEventActionDispatcher? actions = null)
    {
        var src = new Subject();
        var sink = new FakeSink();
        var coord = new NotificationCoordinator(src, sink, s, clock ?? (() => Noon), actions);
        coord.Start();
        return new Harness(src, sink, coord);
    }

    private sealed class FakeSink : INotificationService
    {
        public bool IsAvailable => true;
        public List<NotificationRequest> Shown { get; } = new();
        public void Show(NotificationRequest request) => Shown.Add(request);
    }

    private sealed class ThrowingSink : INotificationService
    {
        public bool IsAvailable => true;
        public void Show(NotificationRequest request) => throw new InvalidOperationException("toast backend down");
    }

    private sealed class FakeDispatcher : IEventActionDispatcher
    {
        public List<EventActionContext> Dispatched { get; } = new();
        public Task DispatchAsync(EventActionContext context, EventActionSettings settings, CancellationToken ct)
        {
            Dispatched.Add(context);
            return Task.CompletedTask;
        }
        public Task TestAsync(EventActionChannel channel, EventActionContext context, EventActionSettings settings, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class Subject : IObservable<CameraEvent>
    {
        private readonly List<IObserver<CameraEvent>> _obs = new();
        public void Emit(CameraEvent e) { foreach (var o in _obs.ToArray()) o.OnNext(e); }
        public IDisposable Subscribe(IObserver<CameraEvent> observer) { _obs.Add(observer); return new Noop(); }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class Settings : Core.Settings.IUserSettingsAccessor
    {
        public string? RecordingsDirectoryOverride => null;
        public int MaxConcurrentGridSessions => 9;
        public string? PreferredNetworkInterface => null;
        public bool SshStrictHostKey => true;
        public int SshDefaultPort => 22;
        public string MajesticConfigPath => "/etc/majestic.yaml";
        public Core.Analytics.AiAcceleration AiAcceleration => Core.Analytics.AiAcceleration.Auto;
        public int ActiveLayoutId => 0;
        public bool NotificationsEnabled { get; init; } = true;
        public bool NotifyOnMotion { get; init; } = true;
        public bool NotifyOnDetection { get; init; } = true;
        public int NotificationCooldownSeconds { get; init; }
        public bool QuietHoursEnabled { get; init; }
        public int QuietHoursStartHour { get; init; } = 22;
        public int QuietHoursEndHour { get; init; } = 7;
        public int NotifyMinConfidencePercent { get; init; }
        public int NotifyMinDurationSeconds { get; init; }
        public int NotifyMinObjects { get; init; } = 1;
        public EventActionSettings EventActions { get; init; } = EventActionSettings.Default;
    }
}

public class WavBoundsTests
{
    [Fact]
    public void Read_WithLimit_LoadsOnlyThatMuch()
    {
        var clip = new PcmClip(new byte[48000 * 4 * 10], 48000, 2); // 10 s
        using var ms = new MemoryStream();
        WavCodec.Write(ms, clip);
        ms.Position = 0;

        var read = WavCodec.Read(ms, TimeSpan.FromSeconds(2));
        Assert.Equal(48000 * 4 * 2, read.Pcm16.Length);
    }

    [Theory]
    [InlineData(1u << 30)] // would overflow BytesPerSecond
    [InlineData(10u)]
    public void Read_AbsurdSampleRate_IsInvalidData(uint rate)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(40); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((ushort)1); w.Write((ushort)1); w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(4); w.Write(0);
        }
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => WavCodec.Read(ms));
    }
}

// The close of an episode must never overtake its open, even when the insert
// into the event log is slow.
public class IngestionOrderTests
{
    [Fact]
    public async Task SlowInsert_CloseStillFollowsOpen()
    {
        var cameras = new CameraDirectoryService(new InMemoryCameraRepository(), new InMemoryGroupRepository(), new InMemorySecretsStore());
        var cam = await cameras.AddAsync(new NewCameraRequest(
            "Gate", "192.168.1.10", 80, null, new Uri("rtsp://192.168.1.10/"), null,
            new CameraCredentials("admin", "x")), CancellationToken.None);

        var repo = new SlowRepo();
        var source = new ManualMotionEventSource();
        var ingestion = new EventIngestionService(new IMotionEventSource[] { source }, repo, cameras);
        var seen = new List<CameraEvent>();
        ingestion.Events.Subscribe(new Collector(seen));
        await ingestion.StartAsync(CancellationToken.None);

        source.Trigger(cam);
        await Task.Delay(TimeSpan.FromSeconds(6)); // quiet-close timer (5 s) fires while the insert hangs
        lock (seen) Assert.Empty(seen);

        repo.ReleaseInsert();
        for (var i = 0; i < 100 && seen.Count < 2; i++) await Task.Delay(20);

        lock (seen)
        {
            Assert.Equal(2, seen.Count);
            Assert.Null(seen[0].EndedAt);
            Assert.NotNull(seen[1].EndedAt);
        }
        await ingestion.DisposeAsync();
    }

    private sealed class SlowRepo : IEventRepository
    {
        private readonly TaskCompletionSource _insert = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseInsert() => _insert.TrySetResult();
        public Task<IReadOnlyList<CameraEvent>> ListAsync(CameraId? cameraId, EventKind? kind, DateTime? since, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CameraEvent>>(Array.Empty<CameraEvent>());
        public Task AddAsync(CameraEvent ev, CancellationToken ct) => _insert.Task;
        public Task UpdateAsync(CameraEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Collector : IObserver<CameraEvent>
    {
        private readonly List<CameraEvent> _seen;
        public Collector(List<CameraEvent> seen) => _seen = seen;
        public void OnNext(CameraEvent value) { lock (_seen) _seen.Add(value); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
