using System.Linq;
using OpenIPC.Viewer.Core.Ssh;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Ssh;

// Pure Phase 13 helpers — busybox `ls -la` parsing (13.4) and the root-delete
// guard (13.4, after dashboard v0.1.2).
public sealed class SshParsingTests
{
    private const string BusyboxListing =
        "total 12\n" +
        "drwxr-xr-x    3 root     root          4096 Jan  1 00:00 .\n" +
        "drwxr-xr-x    5 root     root          4096 Jan  1 00:00 ..\n" +
        "drwxr-xr-x    2 root     root          4096 Feb 14 2024 recordings\n" +
        "-rw-r--r--    1 root     root           220 Mar  3 2023 majestic.yaml\n" +
        "lrwxrwxrwx    1 root     root            11 Jan  1 00:00 sh -> busybox\n";

    [Fact]
    public void Parse_SkipsTotalAndDotEntries()
    {
        var entries = LsParser.Parse(BusyboxListing);
        Assert.DoesNotContain(entries, e => e.Name is "." or "..");
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Parse_ReadsKindSizeAndName()
    {
        var entries = LsParser.Parse(BusyboxListing);

        var dir = entries.Single(e => e.Name == "recordings");
        Assert.Equal(RemoteEntryKind.Directory, dir.Kind);
        Assert.True(dir.IsDirectory);

        var file = entries.Single(e => e.Name == "majestic.yaml");
        Assert.Equal(RemoteEntryKind.File, file.Kind);
        Assert.Equal(220, file.Size);
    }

    [Fact]
    public void Parse_StripsSymlinkTarget()
    {
        var link = LsParser.Parse(BusyboxListing).Single(e => e.Kind == RemoteEntryKind.SymbolicLink);
        Assert.Equal("sh", link.Name);
    }

    [Fact]
    public void Parse_YearFormGetsDate_TimeFormStaysNull()
    {
        var entries = LsParser.Parse(BusyboxListing);
        // "Feb 14 2024" carries a year; "Jan 1 00:00" omits it.
        Assert.NotNull(entries.Single(e => e.Name == "recordings").Modified);
    }

    [Fact]
    public void Parse_ToleratesGarbageLines()
    {
        var entries = LsParser.Parse("not a real ls line\n-rw-r--r-- 1 a b 5 Jan 2 2020 ok.txt\n");
        Assert.Single(entries);
        Assert.Equal("ok.txt", entries[0].Name);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("/", true)]
    [InlineData("/etc", true)]
    [InlineData("/etc/", true)]
    [InlineData("/bin", true)]
    [InlineData("/etc/majestic.yaml", false)]
    [InlineData("/tmp/clip.mp4", false)]
    [InlineData("/mnt/sd/recordings/x.mp4", false)]
    public void RemotePathGuard_ProtectsRootLevel(string? path, bool expected)
    {
        Assert.Equal(expected, RemotePathGuard.IsProtected(path));
    }

    [Theory]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/etc", "majestic.yaml", "/etc/majestic.yaml")]
    [InlineData("/etc/", "/sub", "/etc/sub")]
    public void RemotePath_Combine(string dir, string name, string expected)
    {
        Assert.Equal(expected, RemotePath.Combine(dir, name));
    }

    [Theory]
    [InlineData("/etc/majestic.yaml", "/etc")]
    [InlineData("/etc", "/")]
    [InlineData("/", "/")]
    public void RemotePath_Parent(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.Parent(path));
    }

    [Theory]
    [InlineData("/etc/majestic.yaml", "majestic.yaml")]
    [InlineData("/etc", "etc")]
    [InlineData("/", "/")]
    public void RemotePath_Name(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.Name(path));
    }
}
