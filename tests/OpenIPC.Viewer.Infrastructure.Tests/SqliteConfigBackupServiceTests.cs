using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Infrastructure.Persistence;

namespace OpenIPC.Viewer.Infrastructure.Tests;

public sealed class SqliteConfigBackupServiceTests : IDisposable
{
    private readonly List<string> _dbs = new();

    public void Dispose()
    {
        foreach (var db in _dbs)
            try { if (File.Exists(db)) File.Delete(db); } catch { /* temp */ }
    }

    private async Task<(SqliteCameraRepository cams, SqliteLayoutRepository layouts, SqliteConfigBackupService backup)> NewDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oipc-backup-{Guid.NewGuid():N}.db");
        _dbs.Add(path);
        var factory = new SqliteConnectionFactory(path);
        await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);
        var cams = new SqliteCameraRepository(factory);
        var layouts = new SqliteLayoutRepository(factory);
        return (cams, layouts, new SqliteConfigBackupService(cams, layouts));
    }

    [Fact]
    public async Task Export_Import_RoundTripsCamerasAndLayoutTiles()
    {
        var src = await NewDbAsync();
        var a = await src.cams.AddAsync(Cam("A"), CancellationToken.None);
        var b = await src.cams.AddAsync(Cam("B"), CancellationToken.None);
        var def = (await src.layouts.GetAllAsync(CancellationToken.None))[0];
        await src.layouts.SetTilesAsync(def.Id, new[] { b, a }, CancellationToken.None);

        var json = await src.backup.ExportAsync(CancellationToken.None);

        // Import into a fresh machine/db.
        var dst = await NewDbAsync();
        var preview = await dst.backup.ImportAsync(json, CancellationToken.None);

        Assert.Equal(2, preview.CamerasAdded);
        Assert.Equal(0, preview.CamerasUpdated);

        var cams = await dst.cams.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, cams.Count);
        Assert.Contains(cams, c => c.Id == a && c.Name == "A");

        // The imported layout (appended last) preserved its tile order by GUID.
        var layouts = await dst.layouts.GetAllAsync(CancellationToken.None);
        var imported = layouts.OrderByDescending(l => l.Id.Value).First();
        var tiles = await dst.layouts.GetTilesAsync(imported.Id, CancellationToken.None);
        Assert.Equal(new[] { b, a }, tiles);
    }

    [Fact]
    public async Task Preview_CountsUpdatesForExistingCameras()
    {
        var src = await NewDbAsync();
        await src.cams.AddAsync(Cam("A"), CancellationToken.None);
        await src.cams.AddAsync(Cam("B"), CancellationToken.None);
        var json = await src.backup.ExportAsync(CancellationToken.None);

        // Re-importing into the same db updates both.
        var preview = await src.backup.PreviewAsync(json, CancellationToken.None);
        Assert.Equal(0, preview.CamerasAdded);
        Assert.Equal(2, preview.CamerasUpdated);
    }

    [Fact]
    public async Task Import_RejectsUnknownSchemaVersion()
    {
        var dst = await NewDbAsync();
        var json = "{ \"SchemaVersion\": 999, \"Cameras\": [], \"Layouts\": [] }";
        await Assert.ThrowsAsync<NotSupportedException>(() => dst.backup.ImportAsync(json, CancellationToken.None));
    }

    [Fact]
    public async Task Export_DoesNotLeakPasswords()
    {
        var src = await NewDbAsync();
        await src.cams.AddAsync(Cam("A"), CancellationToken.None);
        var json = await src.backup.ExportAsync(CancellationToken.None);
        // No plaintext credential material — the entity carries none.
        Assert.DoesNotContain("hunter2", json);
    }

    private static Camera Cam(string name) => new(
        CameraId.New(), null, name, "10.0.0.5", null, 80,
        new Uri("rtsp://10.0.0.5/main"), null,
        null, null, false, null, null, null, true, false, false, 0,
        DateTime.UtcNow, DateTime.UtcNow);
}
