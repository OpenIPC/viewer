using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
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

    // In-memory secrets store so credential export/mirror can be exercised.
    private sealed class FakeSecrets : ISecretsStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();
        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct) { _store[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken ct) { _store.TryRemove(key, out _); return Task.CompletedTask; }
    }

    private async Task<(SqliteCameraRepository cams, SqliteLayoutRepository layouts, SqliteConfigBackupService backup, FakeSecrets secrets)> NewDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oipc-backup-{Guid.NewGuid():N}.db");
        _dbs.Add(path);
        var factory = new SqliteConnectionFactory(path);
        await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync(CancellationToken.None);
        var cams = new SqliteCameraRepository(factory);
        var layouts = new SqliteLayoutRepository(factory);
        var secrets = new FakeSecrets();
        return (cams, layouts, new SqliteConfigBackupService(cams, layouts, secrets), secrets);
    }

    [Fact]
    public async Task Export_Import_RoundTripsCamerasAndLayoutTiles()
    {
        var src = await NewDbAsync();
        var a = await src.cams.AddAsync(Cam("A"), CancellationToken.None);
        var b = await src.cams.AddAsync(Cam("B"), CancellationToken.None);
        var def = (await src.layouts.GetAllAsync(CancellationToken.None))[0];
        await src.layouts.SetTilesAsync(def.Id, new[] { b, a }, CancellationToken.None);

        var json = await src.backup.ExportAsync(null, CancellationToken.None);

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
        var json = await src.backup.ExportAsync(null, CancellationToken.None);

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
        var json = await src.backup.ExportAsync(null, CancellationToken.None);
        // No plaintext credential material — the entity carries none.
        Assert.DoesNotContain("hunter2", json);
    }

    [Fact]
    public async Task Export_WithPassphrase_EncryptsCredentials_AndMirrorRestoresThem()
    {
        var src = await NewDbAsync();
        var cam = Cam("A") with { UsernameRef = "u-ref", PasswordRef = "p-ref" };
        var id = await src.cams.AddAsync(cam, CancellationToken.None);
        await src.secrets.SetAsync("u-ref", "admin", CancellationToken.None);
        await src.secrets.SetAsync("p-ref", "hunter2", CancellationToken.None);

        var json = await src.backup.ExportAsync("fleet-key", CancellationToken.None);
        // Password is present but only as ciphertext — never plaintext.
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("CredsSalt", json);

        // Mirror into a fresh machine with the same passphrase → secrets restored.
        var dst = await NewDbAsync();
        await dst.backup.MirrorAsync(json, "fleet-key", CancellationToken.None);
        Assert.Equal("admin", await dst.secrets.GetAsync("u-ref", CancellationToken.None));
        Assert.Equal("hunter2", await dst.secrets.GetAsync("p-ref", CancellationToken.None));
    }

    [Fact]
    public async Task Mirror_WrongPassphrase_DoesNotRestoreCredentials()
    {
        var src = await NewDbAsync();
        var cam = Cam("A") with { UsernameRef = "u-ref", PasswordRef = "p-ref" };
        await src.cams.AddAsync(cam, CancellationToken.None);
        await src.secrets.SetAsync("u-ref", "admin", CancellationToken.None);
        await src.secrets.SetAsync("p-ref", "hunter2", CancellationToken.None);
        var json = await src.backup.ExportAsync("fleet-key", CancellationToken.None);

        var dst = await NewDbAsync();
        await dst.backup.MirrorAsync(json, "wrong-key", CancellationToken.None);
        Assert.Null(await dst.secrets.GetAsync("p-ref", CancellationToken.None));
    }

    [Fact]
    public async Task Mirror_RemovesCamerasAbsentFromFile()
    {
        var src = await NewDbAsync();
        await src.cams.AddAsync(Cam("Keep"), CancellationToken.None);
        var json = await src.backup.ExportAsync(null, CancellationToken.None);

        // dst starts with an extra camera that the file omits → mirror drops it.
        var dst = await NewDbAsync();
        await dst.cams.AddAsync(Cam("Stale"), CancellationToken.None);
        var result = await dst.backup.MirrorAsync(json, null, CancellationToken.None);

        Assert.Equal(1, result.CamerasRemoved);
        var cams = await dst.cams.GetAllAsync(CancellationToken.None);
        Assert.Single(cams);
        Assert.Equal("Keep", cams[0].Name);
    }

    [Fact]
    public async Task Mirror_EmptyCameraFile_DoesNotWipeLocalCameras()
    {
        var dst = await NewDbAsync();
        await dst.cams.AddAsync(Cam("Local"), CancellationToken.None);
        var emptyJson = "{ \"SchemaVersion\": 2, \"Cameras\": [], \"Layouts\": [] }";

        var result = await dst.backup.MirrorAsync(emptyJson, null, CancellationToken.None);

        Assert.Equal(0, result.CamerasRemoved);
        Assert.Single(await dst.cams.GetAllAsync(CancellationToken.None));
    }

    private static Camera Cam(string name) => new(
        CameraId.New(), null, name, "10.0.0.5", null, 80,
        new Uri("rtsp://10.0.0.5/main"), null,
        null, null, false, null, null, null, true, false, false, 0,
        DateTime.UtcNow, DateTime.UtcNow);
}
