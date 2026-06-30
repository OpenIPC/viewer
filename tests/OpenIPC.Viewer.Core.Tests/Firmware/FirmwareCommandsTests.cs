using System;
using OpenIPC.Viewer.Core.Firmware;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Firmware;

// Pure command builders / parser for firmware-lite — the shell strings the SSH
// service sends, tested without a network.
public sealed class FirmwareCommandsTests
{
    [Fact]
    public void SetTimeUtc_UsesBusyboxMMDDhhmmYYYYssInUtc()
    {
        // 2026-06-29 12:34:56 UTC  ->  06 29 12 34 2026 . 56
        var when = new DateTimeOffset(2026, 6, 29, 14, 34, 56, TimeSpan.FromHours(2)); // 12:34:56 UTC
        var cmd = FirmwareCommands.SetTimeUtc(when);
        Assert.Contains("date -u -s 062912342026.56", cmd);
        Assert.Contains("hwclock -w", cmd);
    }

    [Fact]
    public void ParseTime_ExtractsTaggedFields()
    {
        var output = "TIME=Mon Jun 29 12:34:56 UTC 2026\nTZ=Europe/Berlin\nUP= 12:34:56 up 3 days\n";
        var info = FirmwareCommands.ParseTime(output);
        Assert.Equal("Mon Jun 29 12:34:56 UTC 2026", info.DeviceTime);
        Assert.Equal("Europe/Berlin", info.Timezone);
        Assert.Contains("up 3 days", info.Uptime);
    }

    [Fact]
    public void ParseTime_MissingTimezone_IsNull()
    {
        var info = FirmwareCommands.ParseTime("TIME=x\nTZ=\nUP=y\n");
        Assert.Null(info.Timezone);
    }

    [Theory]
    [InlineData("pool.ntp.org", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("time.cloudflare.com", true)]
    [InlineData("a; rm -rf /", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void IsValidNtpServer_RejectsShellInjection(string server, bool expected)
    {
        Assert.Equal(expected, FirmwareCommands.IsValidNtpServer(server));
    }

    [Fact]
    public void SyncNtp_ThrowsOnInvalidServer()
    {
        Assert.Throws<ArgumentException>(() => FirmwareCommands.SyncNtp("$(reboot)"));
    }

    [Theory]
    [InlineData(DeviceLogKind.Kernel, "dmesg")]
    [InlineData(DeviceLogKind.Syslog, "logread")]
    [InlineData(DeviceLogKind.Majestic, "majestic")]
    public void ReadLog_PicksTheRightSourceAndCaps(DeviceLogKind kind, string expectedToken)
    {
        var cmd = FirmwareCommands.ReadLog(kind);
        Assert.Contains(expectedToken, cmd);
        Assert.Contains("tail -300", cmd);
    }
}
