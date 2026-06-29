using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenIPC.Viewer.Core.Firmware;

// Pure builders + parsers for the firmware-lite SSH commands. Kept out of the
// Devices service so the shell strings (especially the busybox `date` set format
// and the NTP-server guard) are unit-testable without a network.
public static class FirmwareCommands
{
    public const string Reboot = "reboot";

    // Tag the time probe so the parser is order-independent and resilient to busybox
    // quirks. `uptime` and `date` exist on every OpenIPC build; /etc/timezone may not.
    public static string ReadTime() =>
        "echo TIME=\"$(date)\"; echo TZ=\"$(cat /etc/timezone 2>/dev/null)\"; echo UP=\"$(uptime)\"";

    public static DeviceTimeInfo ParseTime(string output)
    {
        string? time = null, tz = null, up = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("TIME=", StringComparison.Ordinal)) time = NullIfEmpty(line.Substring(5));
            else if (line.StartsWith("TZ=", StringComparison.Ordinal)) tz = NullIfEmpty(line.Substring(3));
            else if (line.StartsWith("UP=", StringComparison.Ordinal)) up = NullIfEmpty(line.Substring(3));
        }
        return new DeviceTimeInfo(time, tz, up);
    }

    // busybox `date` set format is MMDDhhmm[[CC]YY][.ss]; we always pass the full
    // year + seconds in UTC (-u) so DST/locale never enter into it, then persist
    // to the RTC if present. The trailing `date` echoes the new clock back.
    public static string SetTimeUtc(DateTimeOffset hostNow)
    {
        var stamp = hostNow.ToUniversalTime().ToString("MMddHHmmyyyy.ss", CultureInfo.InvariantCulture);
        return $"date -u -s {stamp} >/dev/null && hwclock -w 2>/dev/null; date";
    }

    // One-shot NTP sync against a single server. The server is validated (not
    // shell-escaped) so a hostname/IP can't smuggle in extra shell.
    public static string SyncNtp(string ntpServer)
    {
        if (!IsValidNtpServer(ntpServer))
            throw new ArgumentException($"Invalid NTP server: {ntpServer}", nameof(ntpServer));
        return $"ntpd -nq -p {ntpServer}";
    }

    public static bool IsValidNtpServer(string server) =>
        !string.IsNullOrWhiteSpace(server) && Regex.IsMatch(server, "^[A-Za-z0-9.:-]{1,253}$");

    // Cap every log read so a huge syslog doesn't pull megabytes over SSH.
    public static string ReadLog(DeviceLogKind kind) => kind switch
    {
        DeviceLogKind.Kernel => "dmesg | tail -300",
        DeviceLogKind.Majestic =>
            "(cat /tmp/majestic.log 2>/dev/null || logread 2>/dev/null | grep -i majestic) | tail -300",
        _ => "(logread 2>/dev/null || cat /var/log/messages 2>/dev/null) | tail -300",
    };

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
