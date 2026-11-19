using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Ssh;
using OpenIPC.Viewer.Devices.LinkHealth;

namespace OpenIPC.Viewer.Devices.Tests.LinkHealth;

public sealed class SshCameraLinkClientTests
{
    private static readonly SshEndpoint Endpoint = new("192.168.1.141", 22, "root", new SshAuth.Password("x"));

    private const string FullRead = "if=eth0\noperstate=up\ncarrier=1\nspeed=100\nduplex=full\nuptime=100.5\nethtool=0\nend=1\n";

    private static (SshCameraLinkClient Client, FakeSessions Sessions) Create()
    {
        var sessions = new FakeSessions();
        return (new SshCameraLinkClient(sessions, NullLogger<SshCameraLinkClient>.Instance, TimeSpan.Zero), sessions);
    }

    [Fact]
    public async Task Read_FailedCommand_IsAnError_NotADefaultSnapshot()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(1, "", "sh: cannot run"));
        await Assert.ThrowsAsync<IOException>(() => client.ReadAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task Read_CutOffOutput_IsAnError()
    {
        // Exit 0 but the run never reached its end marker.
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(0, "if=eth0\noperstate=up\n", ""));
        await Assert.ThrowsAsync<IOException>(() => client.ReadAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task Read_CompleteOutput_Parses()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(0, FullRead, ""));
        var s = await client.ReadAsync(Endpoint, CancellationToken.None);
        Assert.Equal(100, s.SpeedMbps);
        Assert.Equal(100.5, s.UptimeSeconds);
    }

    [Fact]
    public async Task Restart_FailingInitScript_IsAnError()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(1, "", ""));
        await Assert.ThrowsAsync<IOException>(() => client.RestartStreamerAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task LinkMode_Applied_ReturnsTrue()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(0, "ok=1\n", ""));
        Assert.True(await client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
    }

    [Fact]
    public async Task LinkMode_CommandFailed_IsAnError()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(75, "", "ethtool: Operation not supported"));
        await Assert.ThrowsAsync<IOException>(() => client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
    }

    [Fact]
    public async Task LinkMode_NoEthtool_ReturnsFalse()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => new CommandResult(3, "noethtool=1\n", ""));
        Assert.False(await client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
    }

    [Fact]
    public async Task LinkMode_ChannelCut_ConfirmedByReadBack_ReturnsTrue()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => throw new IOException("channel closed"));
        sessions.Exec(cmd =>
        {
            Assert.Contains("sed 's/^/eth:/'", cmd);
            Assert.DoesNotContain("ethtool -s", cmd); // the read-back only reads
            return new CommandResult(0, "if=eth0\neth:\tAdvertised link modes:  10baseT/Full\neth:\tAuto-negotiation: on\nend=1\n", "");
        });
        Assert.True(await client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
        Assert.Equal(2, sessions.Created);
    }

    [Fact]
    public async Task LinkMode_ChannelCut_NotApplied_IsAnError()
    {
        // Regression: any exception used to count as "applied".
        var (client, sessions) = Create();
        sessions.Exec(_ => throw new IOException("channel closed"));
        sessions.Exec(_ => new CommandResult(0,
            "if=eth0\neth:\tAdvertised link modes:  10baseT/Half 10baseT/Full\neth:\t                        100baseT/Half 100baseT/Full\nend=1\n", ""));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
        Assert.Contains("not confirmed", ex.Message);
    }

    [Fact]
    public async Task LinkMode_ChannelCut_CameraGone_IsAnError()
    {
        var (client, sessions) = Create();
        sessions.Exec(_ => throw new IOException("channel closed"));
        sessions.FailConnectAfter = 1; // the read-back session can't connect
        await Assert.ThrowsAsync<IOException>(() => client.SetLinkModeAsync(Endpoint, CameraLinkMode.Force10Full, CancellationToken.None));
    }

    private sealed class FakeSessions : ISshSessionFactory
    {
        private readonly Queue<Func<string, CommandResult>> _execs = new();

        public int Created { get; private set; }
        public int FailConnectAfter { get; set; } = int.MaxValue;

        public void Exec(Func<string, CommandResult> step) => _execs.Enqueue(step);

        public ISshSession Create()
        {
            Created++;
            return new FakeSession(this, Created > FailConnectAfter);
        }

        public CommandResult Next(string command) =>
            _execs.Count > 0 ? _execs.Dequeue()(command) : throw new InvalidOperationException("unexpected exec: " + command);
    }

    private sealed class FakeSession : ISshSession
    {
        private readonly FakeSessions _owner;
        private readonly bool _failConnect;

        public FakeSession(FakeSessions owner, bool failConnect)
        {
            _owner = owner;
            _failConnect = failConnect;
        }

        public Task ConnectAsync(SshEndpoint endpoint, CancellationToken ct) =>
            _failConnect ? Task.FromException(new IOException("connection refused")) : Task.CompletedTask;

        public Task<CommandResult> ExecAsync(string command, CancellationToken ct)
        {
            try
            {
                return Task.FromResult(_owner.Next(command));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                return Task.FromException<CommandResult>(ex);
            }
        }

        public Task<ISshShell> OpenShellAsync(uint columns, uint rows, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string remotePath, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string remotePath, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => default;
    }
}
