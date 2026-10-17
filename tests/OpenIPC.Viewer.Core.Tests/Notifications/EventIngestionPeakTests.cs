using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Tests.Fakes;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Notifications;

// The ingestion service re-publishes an open episode when its peak confidence
// or object count rises — that is what lets a confidence threshold fire on a
// detection that firms up after the first frame.
public class EventIngestionPeakTests
{
    [Fact]
    public async Task PeakRise_RepublishesOpenEpisode()
    {
        var (ingestion, source, camera, seen) = await StartAsync();

        source.Emit(new MotionTick(camera, DateTime.UtcNow, "test", EventKind.Detection, "person ×1", 0.5f, 1));
        await WaitForAsync(() => seen.Count >= 1);

        source.Emit(new MotionTick(camera, DateTime.UtcNow, "test", EventKind.Detection, "person ×1", 0.4f, 1)); // lower → no update
        source.Emit(new MotionTick(camera, DateTime.UtcNow, "test", EventKind.Detection, "person ×2", 0.9f, 2)); // higher → update
        await WaitForAsync(() => seen.Count >= 2);

        lock (seen)
        {
            Assert.Equal(2, seen.Count);
            Assert.All(seen, e => Assert.Null(e.EndedAt));
            Assert.Equal(seen[0].Id, seen[1].Id);
            Assert.Equal(0.9f, seen[1].PeakConfidence);
            Assert.Equal(2, seen[1].PeakObjects);
        }
        await ingestion.DisposeAsync();
    }

    [Fact]
    public async Task PlainMotion_HasNoPeaks()
    {
        var (ingestion, source, camera, seen) = await StartAsync();
        source.Emit(new MotionTick(camera, DateTime.UtcNow, "test"));
        await WaitForAsync(() => seen.Count >= 1);
        lock (seen)
        {
            Assert.Null(seen[0].PeakConfidence);
            Assert.Equal(0, seen[0].PeakObjects);
        }
        await ingestion.DisposeAsync();
    }

    private static async Task<(EventIngestionService, TestSource, CameraId, List<CameraEvent>)> StartAsync()
    {
        var cameras = new CameraDirectoryService(new InMemoryCameraRepository(), new InMemoryGroupRepository(), new InMemorySecretsStore());
        var id = await cameras.AddAsync(new NewCameraRequest(
            "Gate", "192.168.1.10", 80, null, new Uri("rtsp://192.168.1.10/"), null,
            new CameraCredentials("admin", "x")), CancellationToken.None);

        var source = new TestSource();
        var ingestion = new EventIngestionService(new[] { source }, new NullRepo(), cameras);
        var seen = new List<CameraEvent>();
        ingestion.Events.Subscribe(new Collector(seen));
        await ingestion.StartAsync(CancellationToken.None);
        return (ingestion, source, id, seen);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    private sealed class TestSource : IMotionEventSource
    {
        private readonly List<IObserver<MotionTick>> _observers = new();
        public string Name => "test";
        public IDisposable Watch(CameraId cameraId, IObserver<MotionTick> observer, CancellationToken ct)
        {
            _observers.Add(observer);
            return new Noop();
        }
        public void Emit(MotionTick tick) { foreach (var o in _observers.ToArray()) o.OnNext(tick); }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class Collector : IObserver<CameraEvent>
    {
        private readonly List<CameraEvent> _seen;
        public Collector(List<CameraEvent> seen) => _seen = seen;
        public void OnNext(CameraEvent value) { lock (_seen) _seen.Add(value); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class NullRepo : IEventRepository
    {
        public Task<IReadOnlyList<CameraEvent>> ListAsync(CameraId? cameraId, EventKind? kind, DateTime? since, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CameraEvent>>(Array.Empty<CameraEvent>());
        public Task AddAsync(CameraEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(CameraEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct) => Task.FromResult(0);
    }
}
