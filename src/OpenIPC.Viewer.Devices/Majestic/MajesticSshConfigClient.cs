using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Devices.Majestic;

/// <summary>
/// SSH-based <see cref="IMajesticSshConfigClient"/>. Uses <c>cat</c> to read and
/// an SCP upload + atomic <c>mv</c> to write, then <c>killall -HUP majestic</c>
/// to make the daemon reload (phase-13 §13.5).
/// </summary>
public sealed class MajesticSshConfigClient : IMajesticSshConfigClient
{
    private const string RemoteTempPath = "/tmp/.majestic.yaml.upload";
    private const string RestartCommand = "killall -HUP majestic";

    private readonly ISshSessionFactory _sessions;
    private readonly ILogger<MajesticSshConfigClient> _logger;

    public MajesticSshConfigClient(ISshSessionFactory sessions, ILogger<MajesticSshConfigClient> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public string ConfigPath { get; } = "/etc/majestic.yaml";

    public async Task<bool> ConfigExistsAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync($"test -f {ConfigPath}", ct).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<string> ReadRawAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync($"cat {ConfigPath}", ct).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"Failed to read {ConfigPath}: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }

    public async Task WriteRawAsync(SshEndpoint endpoint, string rawYaml, bool restart, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);

        // Stage to a temp file, then move into place — a half-written
        // majestic.yaml (e.g. if the transfer is cut) would brick the streamer.
        var local = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(local, rawYaml, ct).ConfigureAwait(false);
            await session.UploadAsync(local, RemoteTempPath, progress: null, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(local); } catch (IOException) { /* best effort */ }
        }

        var move = await session.ExecAsync($"mv -f {RemoteTempPath} {ConfigPath}", ct).ConfigureAwait(false);
        if (!move.Success)
            throw new IOException($"Failed to write {ConfigPath}: {move.StandardError.Trim()}");

        if (restart)
        {
            var reload = await session.ExecAsync(RestartCommand, ct).ConfigureAwait(false);
            if (!reload.Success)
                _logger.LogWarning("majestic reload returned {Exit}: {Err}",
                    reload.ExitCode, reload.StandardError.Trim());
        }
    }
}
