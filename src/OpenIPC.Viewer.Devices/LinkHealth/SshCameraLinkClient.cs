using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.LinkHealth;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Devices.LinkHealth;

// SSH-backed ICameraLinkClient. One session per operation, like the firmware
// tools: a streamer restart or a link-mode change can drop the connection.
// Command strings and parsing live in the pure CameraLinkCommands.
public sealed class SshCameraLinkClient : ICameraLinkClient
{
    private readonly ISshSessionFactory _sessions;
    private readonly ILogger<SshCameraLinkClient> _logger;

    public SshCameraLinkClient(ISshSessionFactory sessions, ILogger<SshCameraLinkClient> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<CameraLinkSnapshot> ReadAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync(CameraLinkCommands.ReadLinkState(), ct).ConfigureAwait(false);
        return CameraLinkCommands.Parse(result.StandardOutput);
    }

    public async Task RestartStreamerAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync(CameraLinkCommands.RestartStreamer, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"Streamer restart failed: {result.StandardError.Trim()}");
        _logger.LogInformation("Streamer restarted on {Host}: {Out}", endpoint.Host, result.StandardOutput.Trim());
    }

    public async Task<bool> SetLinkModeAsync(SshEndpoint endpoint, CameraLinkMode mode, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        CommandResult result;
        try
        {
            result = await session.ExecAsync(CameraLinkCommands.SetLinkMode(mode), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Renegotiation drops the link for a moment and can take the SSH
            // channel with it; the setting has still been applied.
            _logger.LogDebug(ex, "Link mode channel closed during renegotiation");
            return true;
        }
        if (result.StandardOutput.Contains("noethtool=1", StringComparison.Ordinal))
            return false;
        if (!result.Success)
            throw new IOException($"Link mode change failed: {result.StandardError.Trim()}");
        _logger.LogInformation("Link mode on {Host} set to {Mode}", endpoint.Host, mode);
        return true;
    }
}
