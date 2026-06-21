using System.IO;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Tests.Snapshots;

public sealed class SnapshotServiceTests
{
    [Fact]
    public async Task PrefersRunningMainstream_OverMajestic()
    {
        var (svc, ctx) = Build(majesticBytes: Bytes("HTTP"), freshFrame: null);
        var liveMain = new FakeSession(Bytes("MAIN"));

        var snap = await svc.CaptureAsync(Cam(majestic: true), liveMain, StreamQuality.Main, CancellationToken.None);

        Assert.Equal(SnapshotSource.LiveMain, snap.Source);
        Assert.Equal(0, ctx.Majestic.SnapshotCalls); // never reached for a live mainstream
        Assert.True(File.Exists(snap.Path));
        Assert.Equal("MAIN", File.ReadAllText(snap.Path));
        Assert.Single(ctx.Repo.Added);
        Assert.Equal(1920, snap.Width); // from the fake thumbnail generator
    }

    [Fact]
    public async Task UsesMajesticHttp_WhenNoLiveSession()
    {
        var (svc, _) = Build(majesticBytes: Bytes("HTTP"), freshFrame: null);

        var snap = await svc.CaptureAsync(Cam(majestic: true), liveSession: null, liveQuality: null, CancellationToken.None);

        Assert.Equal(SnapshotSource.HttpSnapshot, snap.Source);
        Assert.Equal("HTTP", File.ReadAllText(snap.Path));
    }

    [Fact]
    public async Task NeverGrabsSubstream_WhenAnHdSourceExists()
    {
        // A substream tile is live AND the camera is Majestic: must take the HD
        // HTTP snapshot, not the SD frame on screen.
        var (svc, ctx) = Build(majesticBytes: Bytes("HTTP"), freshFrame: null);
        var liveSub = new FakeSession(Bytes("SUB"));

        var snap = await svc.CaptureAsync(Cam(majestic: true), liveSub, StreamQuality.Sub, CancellationToken.None);

        Assert.Equal(SnapshotSource.HttpSnapshot, snap.Source);
        Assert.Equal(0, liveSub.SnapshotCalls);
    }

    [Fact]
    public async Task BrieflyOpensMainstream_WhenNoMajesticAndNoLiveMain()
    {
        var (svc, _) = Build(majesticBytes: null, freshFrame: Bytes("OPEN"));

        var snap = await svc.CaptureAsync(Cam(majestic: false), liveSession: null, liveQuality: null, CancellationToken.None);

        Assert.Equal(SnapshotSource.OpenedStream, snap.Source);
        Assert.Equal("OPEN", File.ReadAllText(snap.Path));
    }

    [Fact]
    public async Task FallsBackToSubstream_WhenNoHdSourceAvailable()
    {
        // No Majestic and the mainstream won't open (engine throws) → the live
        // substream frame is better than nothing.
        var (svc, _) = Build(majesticBytes: null, freshFrame: null);
        var liveSub = new FakeSession(Bytes("SUB"));

        var snap = await svc.CaptureAsync(Cam(majestic: false), liveSub, StreamQuality.Sub, CancellationToken.None);

        Assert.Equal(SnapshotSource.LiveSub, snap.Source);
        Assert.Equal("SUB", File.ReadAllText(snap.Path));
    }

    // --- harness ---

    private static byte[] Bytes(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static Camera Cam(bool majestic) => new(
        CameraId.New(), null, "Cam", "10.0.0.5", null, 80,
        new Uri("rtsp://10.0.0.5/main"), new Uri("rtsp://10.0.0.5/sub"),
        null, null, false, null, null, null, true, false, majestic, 0,
        DateTime.UtcNow, DateTime.UtcNow);

    private sealed record Ctx(FakeMajestic Majestic, FakeRepo Repo);

    private static (SnapshotService Service, Ctx Ctx) Build(byte[]? majesticBytes, byte[]? freshFrame)
    {
        var majestic = new FakeMajestic(majesticBytes);
        var repo = new FakeRepo();
        var fs = new FakeFileSystem();
        // The engine yields a fresh mainstream session only when freshFrame is set;
        // otherwise CreateSession throws so the brief-open path fails fast.
        var engine = new FakeEngine(freshFrame is null ? null : () => new FakeSession(freshFrame));
        var coordinator = new LiveStreamCoordinator(engine);
        var svc = new SnapshotService(majestic, coordinator, new FakeCreds(), repo, fs, new FakeThumbs(), new FakeEditor());
        return (svc, new Ctx(majestic, repo));
    }

    private sealed class FakeSession : IVideoSession
    {
        private readonly byte[]? _frame;
        public int SnapshotCalls { get; private set; }
        public FakeSession(byte[]? frame) => _frame = frame;
        public SessionState State { get; private set; } = SessionState.Idle;
        public string? LastError => null;
        public IObservable<SessionState> StateChanged { get; } = new NeverObservable<SessionState>();
        public IObservable<VideoFrame> Frames { get; } = new NeverObservable<VideoFrame>();
        public IObservable<AudioFrame> AudioFrames { get; } = new NeverObservable<AudioFrame>();
        public IObservable<SessionTelemetry> Telemetry { get; } = new NeverObservable<SessionTelemetry>();
        public Task StartAsync(CancellationToken ct) { State = SessionState.Playing; return Task.CompletedTask; }
        public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct)
        {
            SnapshotCalls++;
            return _frame is null
                ? throw new InvalidOperationException("No frame available yet")
                : Task.FromResult(_frame);
        }
        public void PauseDecode() { }
        public void Resume() { }
        public void SetAudioEnabled(bool enabled) { }
        public ValueTask DisposeAsync() => default;
    }

    private sealed class FakeEngine : IVideoEngine
    {
        private readonly Func<IVideoSession>? _factory;
        public FakeEngine(Func<IVideoSession>? factory) => _factory = factory;
        public IVideoSession CreateSession(VideoSessionOptions options) =>
            _factory is null ? throw new InvalidOperationException("mainstream unavailable") : _factory();
    }

    private sealed class FakeMajestic : IMajesticClient
    {
        private readonly byte[]? _bytes;
        public int SnapshotCalls { get; private set; }
        public FakeMajestic(byte[]? bytes) => _bytes = bytes;
        public Task<byte[]> SnapshotJpegAsync(MajesticEndpoint endpoint, MajesticSnapshotOptions options, CancellationToken ct)
        {
            SnapshotCalls++;
            return _bytes is null ? throw new InvalidOperationException("not majestic") : Task.FromResult(_bytes);
        }
        public Task<bool> PingAsync(MajesticEndpoint endpoint, CancellationToken ct) => Task.FromResult(_bytes is not null);
        public Task<MajesticConfig> GetConfigAsync(MajesticEndpoint endpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task<MajesticInfo> GetInfoAsync(MajesticEndpoint endpoint, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateConfigAsync(MajesticEndpoint endpoint, MajesticConfigPatch patch, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateRawConfigAsync(MajesticEndpoint endpoint, string rawJson, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNightModeAsync(MajesticEndpoint endpoint, NightMode mode, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeCreds : ICameraCredentialsProvider
    {
        public Task<CameraCredentials?> GetCredentialsAsync(CameraId id, CancellationToken ct) =>
            Task.FromResult<CameraCredentials?>(null);
    }

    private sealed class FakeRepo : ISnapshotRepository
    {
        public List<Snapshot> Added { get; } = new();
        public Task AddAsync(Snapshot snapshot, CancellationToken ct) { Added.Add(snapshot); return Task.CompletedTask; }
        public Task<IReadOnlyList<Snapshot>> ListAsync(CameraId? c, DateTime? s, DateTime? u, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Snapshot>>(Added);
        public Task<Snapshot?> GetAsync(SnapshotId id, CancellationToken ct) =>
            Task.FromResult(Added.Find(x => x.Id == id));
        public Task RemoveAsync(SnapshotId id, CancellationToken ct) { Added.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    }

    private sealed class FakeThumbs : IThumbnailGenerator
    {
        public Task<ImageSize> GenerateAsync(byte[] jpeg, string thumbPath, int maxDim, CancellationToken ct)
        {
            File.WriteAllBytes(thumbPath, jpeg); // stand-in thumbnail
            return Task.FromResult(new ImageSize(1920, 1080));
        }
    }

    private sealed class FakeEditor : IImageEditor
    {
        public Task<ImageSize> RenderAsync(string srcPath, SnapshotEdit edit, string outPath, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public FakeFileSystem()
        {
            var root = Path.Combine(Path.GetTempPath(), "oipc-snap-tests", Guid.NewGuid().ToString("N"));
            AppDataDir = Directory.CreateDirectory(root);
            RecordingsDir = Directory.CreateDirectory(Path.Combine(root, "recordings"));
            SnapshotsDir = Directory.CreateDirectory(Path.Combine(root, "snapshots"));
        }
        public DirectoryInfo AppDataDir { get; }
        public DirectoryInfo RecordingsDir { get; }
        public DirectoryInfo SnapshotsDir { get; }
    }

    private sealed class NeverObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
