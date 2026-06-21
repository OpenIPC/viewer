using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Infrastructure.Persistence;

namespace OpenIPC.Viewer.Infrastructure.Tests;

public sealed class SqliteLayoutRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"oipc-layouts-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = default!;
    private SqliteLayoutRepository _repo = default!;
    private SqliteCameraRepository _cameras = default!;
    private CameraId _camA;
    private CameraId _camB;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(_dbPath);
        await new MigrationRunner(_factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);
        _cameras = new SqliteCameraRepository(_factory);
        _camA = await _cameras.AddAsync(Cam("A"), CancellationToken.None);
        _camB = await _cameras.AddAsync(Cam("B"), CancellationToken.None);
        _repo = new SqliteLayoutRepository(_factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
    }

    [Fact]
    public async Task Migration_SeedsOneDefaultLayout()
    {
        var layouts = await _repo.GetAllAsync(CancellationToken.None);
        var only = Assert.Single(layouts);
        Assert.Equal("Default", only.Name);
        Assert.Equal(2, only.GridSize);
    }

    [Fact]
    public async Task SetTiles_RoundTripsInOrder()
    {
        var def = (await _repo.GetAllAsync(CancellationToken.None))[0];
        await _repo.SetTilesAsync(def.Id, new[] { _camB, _camA }, CancellationToken.None);

        var tiles = await _repo.GetTilesAsync(def.Id, CancellationToken.None);
        Assert.Equal(new[] { _camB, _camA }, tiles);
    }

    [Fact]
    public async Task AddAndRemoveTile()
    {
        var def = (await _repo.GetAllAsync(CancellationToken.None))[0];
        await _repo.SetTilesAsync(def.Id, System.Array.Empty<CameraId>(), CancellationToken.None);

        await _repo.AddTileAsync(def.Id, _camA, CancellationToken.None);
        await _repo.AddTileAsync(def.Id, _camB, CancellationToken.None);
        await _repo.AddTileAsync(def.Id, _camA, CancellationToken.None); // duplicate ignored
        Assert.Equal(new[] { _camA, _camB }, await _repo.GetTilesAsync(def.Id, CancellationToken.None));

        await _repo.RemoveTileAsync(def.Id, _camA, CancellationToken.None);
        Assert.Equal(new[] { _camB }, await _repo.GetTilesAsync(def.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetTiles_ExcludesDeletedCameras()
    {
        var def = (await _repo.GetAllAsync(CancellationToken.None))[0];
        await _repo.SetTilesAsync(def.Id, new[] { _camA, _camB }, CancellationToken.None);

        await _cameras.RemoveAsync(_camB, CancellationToken.None);

        Assert.Equal(new[] { _camA }, await _repo.GetTilesAsync(def.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Add_Rename_Resize_Reorder_Remove()
    {
        var def = (await _repo.GetAllAsync(CancellationToken.None))[0];
        var office = await _repo.AddAsync("Office", 3, 1, CancellationToken.None);

        await _repo.RenameAsync(office, "Warehouse", CancellationToken.None);
        await _repo.SetGridSizeAsync(office, 1, CancellationToken.None);

        // Put the new layout first.
        await _repo.ReorderAsync(new[] { office, def.Id }, CancellationToken.None);
        var ordered = await _repo.GetAllAsync(CancellationToken.None);
        Assert.Equal(office, ordered[0].Id);
        Assert.Equal("Warehouse", ordered[0].Name);
        Assert.Equal(1, ordered[0].GridSize);

        await _repo.RemoveAsync(office, CancellationToken.None);
        Assert.Single(await _repo.GetAllAsync(CancellationToken.None));
    }

    private static Camera Cam(string name) => new(
        CameraId.New(), null, name, "10.0.0.5", null, 80,
        new Uri("rtsp://10.0.0.5/main"), null,
        null, null, false, null, null, null, true, false, false, 0,
        DateTime.UtcNow, DateTime.UtcNow);
}
