using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Ssh;
using OpenIPC.Viewer.Infrastructure.Ssh;

namespace OpenIPC.Viewer.Infrastructure.Tests;

// Round-trips the SSH suite against a real sshd container (phase-13 §13.6).
// Skips when the container isn't up — Windows CI without Docker, etc.
public sealed class SshSessionIntegrationTests
{
    private static readonly ISshSessionFactory Factory =
        new SshNetSessionFactory(new InMemorySecretsStore(), NullLoggerFactory.Instance);

    private static async Task<ISshSession> ConnectAsync()
    {
        var session = Factory.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.ConnectAsync(SshdFixture.Endpoint, cts.Token);
        return session;
    }

    [SkippableFact]
    public async Task Exec_CapturesStdoutAndExitCode()
    {
        Skip.IfNot(SshdFixture.IsReachable(), "sshd container not running — start tools/sshd/docker-compose.yml.");

        await using var session = await ConnectAsync();
        var result = await session.ExecAsync("echo openipc-it", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("openipc-it", result.StandardOutput);
    }

    [SkippableFact]
    public async Task Upload_Download_RoundTrips()
    {
        Skip.IfNot(SshdFixture.IsReachable(), "sshd container not running.");

        var name = $"openipc-it-{Guid.NewGuid():N}.txt";
        var remote = $"/tmp/{name}";
        var content = "hello from openipc viewer\nsecond line\n";

        var localUp = Path.GetTempFileName();
        var localDown = Path.GetTempFileName();
        await File.WriteAllTextAsync(localUp, content);

        try
        {
            await using var session = await ConnectAsync();
            await session.UploadAsync(localUp, remote, progress: null, CancellationToken.None);
            await session.DownloadAsync(remote, localDown, progress: null, CancellationToken.None);

            Assert.Equal(content, await File.ReadAllTextAsync(localDown));

            // Cleanup the remote artifact.
            await session.DeleteAsync(remote, CancellationToken.None);
        }
        finally
        {
            File.Delete(localUp);
            File.Delete(localDown);
        }
    }

    [SkippableFact]
    public async Task List_ReflectsCreateAndDelete()
    {
        Skip.IfNot(SshdFixture.IsReachable(), "sshd container not running.");

        var dir = $"/tmp/openipc-itdir-{Guid.NewGuid():N}";
        await using var session = await ConnectAsync();

        await session.CreateDirectoryAsync(dir, CancellationToken.None);
        var afterCreate = await CollectNames(session, "/tmp");
        Assert.Contains(RemotePath.Name(dir), afterCreate);

        await session.DeleteAsync(dir, CancellationToken.None);
        var afterDelete = await CollectNames(session, "/tmp");
        Assert.DoesNotContain(RemotePath.Name(dir), afterDelete);
    }

    [Fact]
    public async Task Delete_RejectsRootLevel_WithoutNetwork()
    {
        // Guard is enforced before any I/O, so this needs no container.
        await using var session = Factory.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DeleteAsync("/etc", CancellationToken.None));
    }

    private static async Task<List<string>> CollectNames(ISshSession session, string path)
    {
        var names = new List<string>();
        await foreach (var entry in session.ListAsync(path, CancellationToken.None))
            names.Add(entry.Name);
        return names;
    }
}
