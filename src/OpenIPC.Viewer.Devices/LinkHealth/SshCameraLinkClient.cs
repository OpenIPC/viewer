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
    // The PHY renegotiates after a link-mode change; give it time before the
    // read-back session (10BASE-T autoneg plus DHCP-less re-link is a few seconds).
    private static readonly TimeSpan RenegotiationSettle = TimeSpan.FromSeconds(4);

    private readonly ISshSessionFactory _sessions;
    private readonly ILogger<SshCameraLinkClient> _logger;
    private readonly TimeSpan _settle;

    public SshCameraLinkClient(ISshSessionFactory sessions, ILogger<SshCameraLinkClient> logger)
        : this(sessions, logger, RenegotiationSettle)
    {
    }

    internal SshCameraLinkClient(ISshSessionFactory sessions, ILogger<SshCameraLinkClient> logger, TimeSpan settle)
    {
        _sessions = sessions;
        _logger = logger;
        _settle = settle;
    }

    public async Task<CameraLinkSnapshot> ReadAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync(CameraLinkCommands.ReadLinkState(), ct).ConfigureAwait(false);
        // A failed or cut-off read must surface as a probe error, never as a
        // snapshot of defaults that the analyzer would take for measurements.
        if (!result.Success)
            throw new IOException($"Link state read failed (exit {result.ExitCode}): {Trim(result.StandardError)}");
        if (!CameraLinkCommands.IsComplete(result.StandardOutput))
            throw new IOException("Link state read returned incomplete output");
        return CameraLinkCommands.Parse(result.StandardOutput);
    }

    public async Task RestartStreamerAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        var result = await session.ExecAsync(CameraLinkCommands.RestartStreamer(), ct).ConfigureAwait(false);
        if (!result.Success || result.StandardOutput.IndexOf("restarted=", StringComparison.Ordinal) < 0)
            throw new IOException($"Streamer restart failed (exit {result.ExitCode}): {Trim(result.StandardError)}");
        _logger.LogInformation("Streamer restarted on {Host}: {Out}", endpoint.Host, result.StandardOutput.Trim());
    }

    public async Task<bool> SetLinkModeAsync(SshEndpoint endpoint, CameraLinkMode mode, CancellationToken ct)
    {
        CommandResult result;
        try
        {
            await using var session = _sessions.Create();
            await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            result = await session.ExecAsync(CameraLinkCommands.SetLinkMode(mode), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Renegotiation drops the link for a moment and can take the SSH
            // channel with it — or the command never ran. Only a fresh session
            // reading back what the PHY advertises can tell which.
            _logger.LogDebug(ex, "Link mode channel closed; verifying with a read-back");
            return await VerifyAsync(endpoint, mode, ex, ct).ConfigureAwait(false);
        }

        if (result.StandardOutput.Contains("noethtool=1", StringComparison.Ordinal))
            return false;
        if (!result.Success || !result.StandardOutput.Contains("ok=1", StringComparison.Ordinal))
            throw new IOException($"Link mode change failed (exit {result.ExitCode}): {Trim(result.StandardError)}");
        _logger.LogInformation("Link mode on {Host} set to {Mode}", endpoint.Host, mode);
        return true;
    }

    private async Task<bool> VerifyAsync(SshEndpoint endpoint, CameraLinkMode mode, Exception original, CancellationToken ct)
    {
        await Task.Delay(_settle, ct).ConfigureAwait(false);
        CommandResult readBack;
        try
        {
            await using var session = _sessions.Create();
            await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            readBack = await session.ExecAsync(CameraLinkCommands.ReadLinkMode(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException(
                $"Link mode change not confirmed: the camera did not answer afterwards ({ex.Message}; first error: {original.Message})", ex);
        }

        if (readBack.StandardOutput.Contains("noethtool=1", StringComparison.Ordinal))
            return false;
        var snapshot = CameraLinkCommands.IsComplete(readBack.StandardOutput)
            ? CameraLinkCommands.Parse(readBack.StandardOutput)
            : null;
        if (snapshot is null || !CameraLinkCommands.AdvertisesMode(snapshot.AdvertisedModes, mode))
        {
            throw new IOException(
                $"Link mode change not confirmed: the camera advertises '{snapshot?.AdvertisedModes ?? "?"}' (first error: {original.Message})",
                original);
        }
        _logger.LogInformation("Link mode on {Host} set to {Mode} (confirmed by read-back)", endpoint.Host, mode);
        return true;
    }

    private static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s.Trim();
}
