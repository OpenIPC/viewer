using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Core.Firmware;

// Firmware-lite: a safe subset of OpenIPC WebUI device operations over the
// existing SSH layer — reboot, clock, and log snapshots. Each call opens its own
// short-lived SSH session (like MajesticSshConfigClient). The implementation
// lives in Devices; App talks only to this Core contract.
public interface IFirmwareMaintenanceService
{
    // Best-effort reboot — the session drops as the device goes down, which is
    // not treated as a failure.
    Task RebootAsync(SshEndpoint endpoint, CancellationToken ct);

    Task<DeviceTimeInfo> GetTimeAsync(SshEndpoint endpoint, CancellationToken ct);

    // Push the host clock to the device (UTC) and persist to the RTC if present.
    Task SetTimeFromHostAsync(SshEndpoint endpoint, DateTimeOffset hostNow, CancellationToken ct);

    // One-shot NTP sync against a single server (hostname/IP).
    Task SyncNtpAsync(SshEndpoint endpoint, string ntpServer, CancellationToken ct);

    // A capped snapshot of the requested log.
    Task<string> ReadLogAsync(SshEndpoint endpoint, DeviceLogKind kind, CancellationToken ct);
}
