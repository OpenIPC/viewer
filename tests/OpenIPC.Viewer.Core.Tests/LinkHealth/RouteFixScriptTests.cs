using OpenIPC.Viewer.Core.LinkHealth;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.LinkHealth;

public sealed class RouteFixScriptTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    private static RouteFixPlan Plan(params RouteFixEntry[] routes) =>
        new(routes, new[] { new AddressConflict("192.168.1.240", "Ethernet", "Wi-Fi", "9C-97-1B-56-2A-C0") });

    private static RouteFixEntry Entry(string ip, string iface = "Ethernet", int index = 8) =>
        new(ip, ip, iface, index, PathEvidence.Stream);

    [Fact]
    public void PowerShell_PinsEachCamera()
    {
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.130"), Entry("192.168.1.141")), RouteScriptTarget.Windows, At, russian: false);

        Assert.Contains("[CmdletBinding(SupportsShouldProcess = $true)]", text);
        Assert.Contains("Camera = '192.168.1.130'; Alias = 'Ethernet'; Index = 8", text);
        Assert.Contains("Camera = '192.168.1.141'; Alias = 'Ethernet'; Index = 8", text);
        Assert.Contains("$PSCmdlet.ShouldProcess", text);
        Assert.Contains("param([switch]$Undo, [switch]$Pause, [string]$LogFile)", text);
        Assert.Contains("Find-NetRoute -RemoteIPAddress $r.Camera", text);
    }

    [Fact]
    public void PowerShell_CreatesTheRouteOnceForBothStores()
    {
        // Regression: the first version added the route to ActiveStore and then
        // separately to PersistentStore; the second call fails because a route
        // without -PolicyStore already goes to both, and the script stopped
        // after the first camera. Creation must not name a store; only the
        // checks and the clean-up of earlier pins do.
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141")), RouteScriptTarget.Windows, At, russian: false);
        var create = text.Split('\n').Where(l => l.Contains("New-NetRoute", StringComparison.Ordinal)).ToList();
        var call = Assert.Single(create, l => !l.TrimStart().StartsWith('#'));
        Assert.DoesNotContain("PolicyStore", call);
        Assert.Contains("-NextHop '0.0.0.0'", call);
        Assert.Contains("Test-PinnedRoute $prefix $adapter.ifIndex 'ActiveStore'", text);
        Assert.Contains("Test-PinnedRoute $prefix $adapter.ifIndex 'PersistentStore'", text);
    }

    [Fact]
    public void PowerShell_OneCameraFailing_DoesNotStopTheOthers_AndAlwaysPauses()
    {
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.130"), Entry("192.168.1.141")), RouteScriptTarget.Windows, At, russian: false);
        Assert.Contains("$failed++", text);
        Assert.Contains("} finally {", text);
        Assert.Contains("if ($Pause) { [void](Read-Host $msg.PressEnter) }", text);
        Assert.Contains("fix-camera-routes.log", text);
        Assert.Contains("exit $code", text);
        // No exit inside the try: the finally (pause + log) must always run.
        var tryStart = text.IndexOf("$code = 0\r\ntry {", StringComparison.Ordinal);
        var finallyAt = text.IndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(tryStart > 0 && finallyAt > tryStart);
        Assert.DoesNotContain("exit ", text.Substring(tryStart, finallyAt - tryStart));
    }

    [Fact]
    public void PowerShell_ReportsTheConflictButDoesNotChangeAddresses()
    {
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141")), RouteScriptTarget.Windows, At, russian: false);
        Assert.Contains("Address = '192.168.1.240'; Own = 'Ethernet'; SeenOn = 'Wi-Fi'; Mac = '9C-97-1B-56-2A-C0'", text);
        Assert.DoesNotContain("New-NetIPAddress", text);
        Assert.DoesNotContain("Set-NetIPAddress", text);
        Assert.DoesNotContain("Set-NetIPInterface", text);
    }

    [Fact]
    public void PowerShell_UsesCrlfAndQuotesAdapterNames()
    {
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141", "Cam's LAN")), RouteScriptTarget.Windows, At, russian: true);
        Assert.Contains("Alias = 'Cam''s LAN'", text);
        Assert.DoesNotContain("\r\r", text);
        Assert.DoesNotContain(text.Replace("\r\n", ""), c => c == '\n');
        Assert.Contains("Готово.", text);
        Assert.True(RouteFixScript.NeedsBom(RouteScriptTarget.Windows));
    }

    [Fact]
    public void PowerShell_SkipsMalformedValues()
    {
        var plan = new RouteFixPlan(
            new[] { Entry("192.168.1.141; Remove-Item C:\\"), Entry("192.168.1.130") },
            new[] { new AddressConflict("192.168.1.240", "Ethernet", "Wi-Fi", "not-a-mac'; evil") });
        var text = RouteFixScript.Build(plan, RouteScriptTarget.Windows, At, russian: false);
        Assert.DoesNotContain("Remove-Item C:", text);
        Assert.DoesNotContain("evil", text);
        Assert.Contains("Camera = '192.168.1.130'", text);
    }

    [Fact]
    public void Linux_UsesIpRouteReplaceWithValidatedNames()
    {
        var plan = new RouteFixPlan(new[] { Entry("192.168.1.141", "eth0"), Entry("192.168.1.130", "bad name;rm") }, Array.Empty<AddressConflict>());
        var text = RouteFixScript.Build(plan, RouteScriptTarget.Linux, At, russian: false);
        Assert.StartsWith("#!/bin/sh\n", text);
        Assert.Contains("ip route replace \"$1/32\" dev \"$2\" scope link", text);
        Assert.Contains("pin 192.168.1.141 eth0 || {", text);
        Assert.DoesNotContain("rm", text.Split('\n').Where(l => l.StartsWith("pin ", StringComparison.Ordinal)).SelectMany(l => l.Split(' ')));
        Assert.Contains("# skipped 192.168.1.130", text);
        Assert.DoesNotContain("\r", text);
        Assert.False(RouteFixScript.NeedsBom(RouteScriptTarget.Linux));
    }

    [Fact]
    public void PowerShell_DoublesTypographicQuotesInAdapterNames()
    {
        // Regression: PowerShell also ends a single-quoted literal at U+2018–U+201B.
        var name = "Bob\u2019s \u2018cam\u201B LAN\u201A";
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141", name)), RouteScriptTarget.Windows, At, russian: false);
        Assert.Contains("Alias = 'Bob\u2019\u2019s \u2018\u2018cam\u201B\u201B LAN\u201A\u201A'", text);
        Assert.Equal("'it''s\u2019\u2019'", RouteFixScript.PsLiteral("it's\u2019"));
    }

    [Fact]
    public void Sh_CountsFailuresAndExitsNonZero()
    {
        // Regression: the sh version printed "Done." and exited 0 even when
        // every route failed.
        var plan = new RouteFixPlan(new[] { Entry("192.168.1.141", "eth0"), Entry("192.168.1.130", "eth0") }, Array.Empty<AddressConflict>());
        var text = RouteFixScript.Build(plan, RouteScriptTarget.Linux, At, russian: false);
        Assert.Contains("failed=0\n", text);
        Assert.Contains("pin 192.168.1.141 eth0 || { echo '192.168.1.141: not pinned - the ip route replace command failed' >&2; failed=$((failed+1)); }\n", text);
        Assert.Contains("if [ \"$failed\" -gt 0 ]; then echo 'Cameras not pinned: '\"$failed\"'. See the messages above.' >&2; exit 2; fi\n", text);
        var exitAt = text.IndexOf("exit 2", StringComparison.Ordinal);
        var doneAt = text.IndexOf("echo 'Done.'", StringComparison.Ordinal);
        Assert.True(exitAt > 0 && doneAt > exitAt);
    }

    [Fact]
    public void Sh_SkippedAdapterCountsAsFailure()
    {
        var plan = new RouteFixPlan(new[] { Entry("192.168.1.141", "bad name;rm") }, Array.Empty<AddressConflict>());
        var text = RouteFixScript.Build(plan, RouteScriptTarget.Linux, At, russian: false);
        Assert.Contains("# skipped 192.168.1.141: unusual adapter name\nfailed=$((failed+1))\n", text);
    }

    [Theory]
    [InlineData(RouteScriptTarget.Linux)]
    [InlineData(RouteScriptTarget.MacOs)]
    public void Sh_ParsesInARealShell(RouteScriptTarget target)
    {
        var sh = OperatingSystem.IsWindows()
            ? new[] { @"C:\Program Files\Git\usr\bin\sh.exe", @"C:\Program Files\Git\bin\sh.exe" }.FirstOrDefault(File.Exists)
            : "/bin/sh";
        if (sh is null || !File.Exists(sh)) return;

        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141", "eth0"), Entry("192.168.1.130", "en5")), target, At, russian: true);
        var path = Path.Combine(Path.GetTempPath(), "openipc-fix-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(sh) { RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-n"); // parse only, run nothing
            psi.ArgumentList.Add(path.Replace('\\', '/'));
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            Assert.True(p.ExitCode == 0, err);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ElevatedStub_RunsVerifiedBytesFromMemory_NotTheFile()
    {
        // Regression: the elevated PowerShell used to run "-File <user-writable path>",
        // which another program could swap while the UAC prompt was open.
        var hash = new string('A', 64);
        var stub = RouteFixScript.ElevatedStub(@"C:\Users\Bob’s\fixes\fix-camera-routes.ps1", hash, @"C:\Users\Bob’s\fixes\fix-camera-routes.log", russian: false);

        Assert.Contains("[System.IO.File]::ReadAllBytes('C:\\Users\\Bob’’s\\fixes\\fix-camera-routes.ps1')", stub);
        Assert.Contains("if ($hash -ne '" + hash + "')", stub);
        Assert.Contains("exit 4", stub);
        Assert.Contains("& ([ScriptBlock]::Create($text)) -Pause -LogFile 'C:\\Users\\Bob’’s\\fixes\\fix-camera-routes.log'", stub);
        Assert.DoesNotContain("-File", stub);
        // The file is read exactly once: the hashed bytes are the executed bytes.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(stub, "ReadAllBytes"));
    }

    [Fact]
    public void PowerShell_DoesNotRelyOnScriptScope()
    {
        // Regression: run from memory by the stub there is no script scope, so
        // "$script:logging" was null and the log stayed empty.
        var text = RouteFixScript.Build(Plan(Entry("192.168.1.141")), RouteScriptTarget.Windows, At, russian: false);
        Assert.DoesNotContain("$script:", text);
    }

    [Fact]
    public void ElevatedStub_RejectsAMalformedHash()
    {
        Assert.Throws<ArgumentException>(() => RouteFixScript.ElevatedStub("x.ps1", "abc'; Remove-Item C:\\", "x.log", russian: false));
        Assert.Throws<ArgumentException>(() => RouteFixScript.ElevatedStub("x.ps1", new string('a', 64), "x.log", russian: false));
    }

    [Fact]
    public void EncodeCommand_IsBase64OfUtf16()
    {
        var encoded = RouteFixScript.EncodeCommand("exit 4");
        Assert.Equal("exit 4", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void FileBytesAndHash_MatchWhatIsWritten()
    {
        var ps = RouteFixScript.FileBytes("x", RouteScriptTarget.Windows);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }, ps);
        Assert.Equal(new[] { (byte)'x' }, RouteFixScript.FileBytes("x", RouteScriptTarget.Linux));
        // SHA-256("abc"), upper-case hex — the form the stub compares.
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            RouteFixScript.Sha256Hex(System.Text.Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void MacOs_UsesRouteAddHostInterface()
    {
        var plan = new RouteFixPlan(new[] { Entry("192.168.1.141", "en5") }, Array.Empty<AddressConflict>());
        var text = RouteFixScript.Build(plan, RouteScriptTarget.MacOs, At, russian: false);
        Assert.Contains("route -n add -host \"$1\" -interface \"$2\"", text);
        Assert.Contains("pin 192.168.1.141 en5", text);
    }

    [Fact]
    public void EmptyPlan_SaysNothingToChange()
    {
        var text = RouteFixScript.Build(new RouteFixPlan(Array.Empty<RouteFixEntry>(), Array.Empty<AddressConflict>()),
            RouteScriptTarget.Windows, At, russian: false);
        Assert.Contains("$routes = @(\r\n)", text);
        Assert.Contains("Nothing = 'Nothing to change.'", text);
    }
}
