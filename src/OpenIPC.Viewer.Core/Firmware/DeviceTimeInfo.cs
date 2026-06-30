namespace OpenIPC.Viewer.Core.Firmware;

// Snapshot of a camera's clock, parsed from a single SSH `date`/`uptime` probe.
// All fields are best-effort — a field is null when the device didn't report it.
public sealed record DeviceTimeInfo(string? DeviceTime, string? Timezone, string? Uptime);
