using System.IO;
using System.Text;
using OpenIPC.Viewer.Infrastructure.Secrets;

namespace OpenIPC.Viewer.Core.Tests.Secrets;

public sealed class EncryptedFileSecretsStoreTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public EncryptedFileSecretsStoreTests()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "openipc-test-" + Guid.NewGuid().ToString("N")));
        _tempDir.Create();
    }

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SetGet_RoundTripsValue()
    {
        var store = NewStore("test-key-material");
        await store.SetAsync("cam:1:password", "hunter2", CancellationToken.None);
        var got = await store.GetAsync("cam:1:password", CancellationToken.None);
        Assert.Equal("hunter2", got);
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var store = NewStore("test-key-material");
        var got = await store.GetAsync("nope", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task Remove_DeletesValue()
    {
        var store = NewStore("test-key-material");
        await store.SetAsync("a", "v", CancellationToken.None);
        await store.RemoveAsync("a", CancellationToken.None);
        Assert.Null(await store.GetAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task Persistence_SurvivesRestart()
    {
        var first = NewStore("stable-material");
        await first.SetAsync("k", "v", CancellationToken.None);
        // Simulate restart — new instance reading the same files.
        var second = NewStore("stable-material");
        Assert.Equal("v", await second.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task DifferentKeyMaterial_CannotDecryptForeignSecrets()
    {
        var withMaterialA = NewStore("material-A");
        await withMaterialA.SetAsync("k", "v", CancellationToken.None);

        // Same dir, different key — decryption should fail noisily, not silently
        // return null. Otherwise machine-id rotation would look like a fresh
        // install instead of a recovery problem.
        var withMaterialB = NewStore("material-B");
        await Assert.ThrowsAnyAsync<Exception>(() => withMaterialB.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Set_OverwritesExistingValue()
    {
        var store = NewStore("test-key-material");
        await store.SetAsync("k", "old", CancellationToken.None);
        await store.SetAsync("k", "new", CancellationToken.None);
        Assert.Equal("new", await store.GetAsync("k", CancellationToken.None));
    }

    private EncryptedFileSecretsStore NewStore(string material)
        => new(_tempDir, Encoding.UTF8.GetBytes(material));
}
