using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Infrastructure.Persistence;

namespace OpenIPC.Viewer.Infrastructure.Tests;

public sealed class SqliteSnapshotRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"oipc-snaps-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = default!;
    private SqliteSnapshotRepository _repo = default!;
    private CameraId _camA;
    private CameraId _camB;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(_dbPath);
        await new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);

        var cameras = new SqliteCameraRepository(_factory);
        _camA = await cameras.AddAsync(Cam("A"), CancellationToken.None);
        _camB = await cameras.AddAsync(Cam("B"), CancellationToken.None);
        _repo = new SqliteSnapshotRepository(_factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
    }

    [Fact]
    public async Task AddAndGet_RoundTrips()
    {
        var snap = Snap(_camA, DateTime.UtcNow, SnapshotSource.HttpSnapshot);
        await _repo.AddAsync(snap, CancellationToken.None);

        var fetched = await _repo.GetAsync(snap.Id, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(snap.Path, fetched!.Path);
        Assert.Equal(1920, fetched.Width);
        Assert.Equal(SnapshotSource.HttpSnapshot, fetched.Source);
    }

    [Fact]
    public async Task List_FiltersByCamera()
    {
        await _repo.AddAsync(Snap(_camA, DateTime.UtcNow, SnapshotSource.HttpSnapshot), CancellationToken.None);
        await _repo.AddAsync(Snap(_camB, DateTime.UtcNow, SnapshotSource.HttpSnapshot), CancellationToken.None);

        var onlyA = await _repo.ListAsync(_camA, null, null, 100, CancellationToken.None);

        Assert.Single(onlyA);
        Assert.Equal(_camA, onlyA[0].CameraId);
    }

    [Fact]
    public async Task List_FiltersByDateRange_AndSortsNewestFirst()
    {
        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await _repo.AddAsync(Snap(_camA, t0, SnapshotSource.HttpSnapshot), CancellationToken.None);
        await _repo.AddAsync(Snap(_camA, t0.AddDays(5), SnapshotSource.HttpSnapshot), CancellationToken.None);
        await _repo.AddAsync(Snap(_camA, t0.AddDays(10), SnapshotSource.HttpSnapshot), CancellationToken.None);

        var since = await _repo.ListAsync(null, t0.AddDays(3), null, 100, CancellationToken.None);
        Assert.Equal(2, since.Count);
        Assert.True(since[0].TakenAt > since[1].TakenAt); // newest first

        var window = await _repo.ListAsync(null, t0.AddDays(1), t0.AddDays(9), 100, CancellationToken.None);
        Assert.Single(window);
        Assert.Equal(t0.AddDays(5), window[0].TakenAt);
    }

    [Fact]
    public async Task Remove_Deletes()
    {
        var snap = Snap(_camA, DateTime.UtcNow, SnapshotSource.LiveMain);
        await _repo.AddAsync(snap, CancellationToken.None);

        await _repo.RemoveAsync(snap.Id, CancellationToken.None);

        Assert.Null(await _repo.GetAsync(snap.Id, CancellationToken.None));
    }

    private static Snapshot Snap(CameraId cam, DateTime takenAtUtc, SnapshotSource source) => new(
        SnapshotId.New(), cam, takenAtUtc,
        $"/snaps/{cam}/{Guid.NewGuid():N}.jpg", "/snaps/.thumbs/t.jpg",
        1920, 1080, source, SnapshotKind.Manual);

    private static Camera Cam(string name) => new(
        CameraId.New(), null, name, "10.0.0.5", null, 80,
        new Uri("rtsp://10.0.0.5/main"), null,
        null, null, false, null, null, null, true, false, false, 0,
        DateTime.UtcNow, DateTime.UtcNow);
}
