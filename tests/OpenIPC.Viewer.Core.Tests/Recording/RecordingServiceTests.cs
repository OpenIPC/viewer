using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Tests.Fakes;

namespace OpenIPC.Viewer.Core.Tests.Recording;

public sealed class RecordingServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("openipc-rec-tests-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RecorderExitingBeforeFirstSegment_ClearsActiveStateAndReportsNothingWritten()
    {
        var (service, recorder, repo, id) = await CreateAsync();
        await service.StartAsync(id, CancellationToken.None);
        Assert.True(service.IsRecording(id));

        var failed = WaitForFailure(service);
        recorder.Last!.Emit(new RecordingEvent.Stopped(DateTime.UtcNow, RecordingStopReason.ProcessExited, "Connection timed out"));
        var e = await failed;

        Assert.False(service.IsRecording(id));
        Assert.Equal(id, e.CameraId);
        Assert.False(e.WroteSegment);
        Assert.Equal("Connection timed out", e.Message);
        Assert.True(recorder.Last.Disposed);
        Assert.Empty(repo.Rows);
    }

    [Fact]
    public async Task RecorderDyingMidSegment_FinalizesTheOpenRow()
    {
        var (service, recorder, repo, id) = await CreateAsync();
        await service.StartAsync(id, CancellationToken.None);

        var session = recorder.Last!;
        session.CurrentSegmentPath = Path.Combine(_root.FullName, "seg.mp4");
        session.Emit(new RecordingEvent.Started(session.CurrentSegmentPath, DateTime.UtcNow));
        await WaitUntilAsync(() => repo.Rows.Count == 1);

        var failed = WaitForFailure(service);
        session.Emit(new RecordingEvent.Stopped(DateTime.UtcNow, RecordingStopReason.Error));
        var e = await failed;

        Assert.True(e.WroteSegment);
        Assert.False(service.IsRecording(id));
        Assert.NotNull(Assert.Single(repo.Rows).EndedAt);
    }

    [Fact]
    public async Task UserStop_DoesNotRaiseRecordingFailed()
    {
        var (service, recorder, _, id) = await CreateAsync();
        await service.StartAsync(id, CancellationToken.None);

        var raised = false;
        service.RecordingFailed += (_, _) => raised = true;
        await service.StopAsync(id, CancellationToken.None);
        await Task.Delay(50);

        Assert.False(raised);
        Assert.False(service.IsRecording(id));
        Assert.True(recorder.Last!.Disposed);
    }

    private async Task<(RecordingService, FakeRecorder, FakeRecordingRepository, CameraId)> CreateAsync()
    {
        var cameras = new CameraDirectoryService(new InMemoryCameraRepository(), new InMemoryGroupRepository(), new InMemorySecretsStore());
        var id = await cameras.AddAsync(new NewCameraRequest(
            Name: "Yard",
            Host: "192.168.1.10",
            HttpPort: 80,
            OnvifPort: null,
            RtspMainUri: new Uri("rtsp://192.168.1.10/"),
            RtspSubUri: null,
            Credentials: null), CancellationToken.None);
        var recorder = new FakeRecorder();
        var repo = new FakeRecordingRepository();
        var service = new RecordingService(recorder, repo, new TempFileSystem(_root), cameras);
        return (service, recorder, repo, id);
    }

    private static Task<RecordingService.RecordingFailure> WaitForFailure(RecordingService service)
    {
        var tcs = new TaskCompletionSource<RecordingService.RecordingFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RecordingFailed += (_, e) => tcs.TrySetResult(e);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class TempFileSystem : IFileSystem
    {
        public TempFileSystem(DirectoryInfo root) => AppDataDir = RecordingsDir = SnapshotsDir = root;
        public DirectoryInfo AppDataDir { get; }
        public DirectoryInfo RecordingsDir { get; }
        public DirectoryInfo SnapshotsDir { get; }
    }

    private sealed class FakeRecorder : IRecorder
    {
        public FakeSession? Last { get; private set; }

        public Task<IRecordingSession> StartAsync(RecordingOptions options, CancellationToken ct)
        {
            Last = new FakeSession();
            return Task.FromResult<IRecordingSession>(Last);
        }
    }

    private sealed class FakeSession : IRecordingSession, IObservable<RecordingEvent>
    {
        private readonly List<IObserver<RecordingEvent>> _observers = new();

        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public string? CurrentSegmentPath { get; set; }
        public IObservable<RecordingEvent> Events => this;
        public bool Disposed { get; private set; }

        public void Emit(RecordingEvent ev)
        {
            foreach (var o in _observers.ToArray()) o.OnNext(ev);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Emit(new RecordingEvent.Stopped(DateTime.UtcNow, RecordingStopReason.User));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public IDisposable Subscribe(IObserver<RecordingEvent> observer)
        {
            _observers.Add(observer);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        private sealed class Unsubscriber(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FakeRecordingRepository : IRecordingRepository
    {
        private readonly object _gate = new();
        private readonly List<Core.Recording.Recording> _rows = new();

        public IReadOnlyList<Core.Recording.Recording> Rows
        {
            get { lock (_gate) return _rows.ToList(); }
        }

        public Task<IReadOnlyList<Core.Recording.Recording>> ListAsync(CameraId? cameraId, CancellationToken ct) =>
            Task.FromResult(Rows);

        public Task AddAsync(Core.Recording.Recording recording, CancellationToken ct)
        {
            lock (_gate) _rows.Add(recording);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Core.Recording.Recording recording, CancellationToken ct)
        {
            lock (_gate)
            {
                var i = _rows.FindIndex(r => r.Id == recording.Id);
                if (i >= 0) _rows[i] = recording;
            }
            return Task.CompletedTask;
        }

        public Task<Core.Recording.Recording?> GetByPathAsync(string filePath, CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_rows.FirstOrDefault(r => r.FilePath == filePath));
        }

        public Task RemoveAsync(RecordingId id, CancellationToken ct)
        {
            lock (_gate) _rows.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }
}
