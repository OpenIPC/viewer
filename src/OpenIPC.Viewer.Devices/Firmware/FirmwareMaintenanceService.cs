using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Firmware;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Devices.Firmware;

// SSH-backed IFirmwareMaintenanceService. Each operation opens its own session
// (cheap, and keeps a reboot from poisoning a shared connection), mirroring
// MajesticSshConfigClient. Command strings come from the pure FirmwareCommands.
public sealed class FirmwareMaintenanceService : IFirmwareMaintenanceService
{
    private readonly ISshSessionFactory _sessions;
    private readonly ILogger<FirmwareMaintenanceService> _logger;

    public FirmwareMaintenanceService(ISshSessionFactory sessions, ILogger<FirmwareMaintenanceService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public async Task RebootAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            await session.ExecAsync(FirmwareCommands.Reboot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The device tears the connection down as it reboots — the command
            // still took. Only a connect failure (above) is a real error.
            _logger.LogDebug(ex, "Reboot command channel closed (expected as the device restarts)");
        }
    }

    public async Task<DeviceTimeInfo> GetTimeAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        var result = await RunAsync(endpoint, FirmwareCommands.ReadTime(), ct).ConfigureAwait(false);
        return FirmwareCommands.ParseTime(result.StandardOutput);
    }

    public async Task SetTimeFromHostAsync(SshEndpoint endpoint, DateTimeOffset hostNow, CancellationToken ct)
    {
        var result = await RunAsync(endpoint, FirmwareCommands.SetTimeUtc(hostNow), ct).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"Failed to set device time: {result.StandardError.Trim()}");
    }

    public async Task SyncNtpAsync(SshEndpoint endpoint, string ntpServer, CancellationToken ct)
    {
        var result = await RunAsync(endpoint, FirmwareCommands.SyncNtp(ntpServer), ct).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"NTP sync failed: {result.StandardError.Trim()}");
    }

    public async Task<string> ReadLogAsync(SshEndpoint endpoint, DeviceLogKind kind, CancellationToken ct)
    {
        var result = await RunAsync(endpoint, FirmwareCommands.ReadLog(kind), ct).ConfigureAwait(false);
        // logread/dmesg often write to stderr or exit non-zero with content still
        // useful — return whatever came back rather than throwing.
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
    }

    private async Task<CommandResult> RunAsync(SshEndpoint endpoint, string command, CancellationToken ct)
    {
        await using var session = _sessions.Create();
        await session.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        return await session.ExecAsync(command, ct).ConfigureAwait(false);
    }
}
