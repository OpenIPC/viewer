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
        Assert.Contains("param([switch]$Undo, [switch]$Pause)", text);
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
        Assert.Contains("pin 192.168.1.141 eth0\n", text);
        Assert.DoesNotContain("rm", text.Split('\n').Where(l => l.StartsWith("pin ", StringComparison.Ordinal)).SelectMany(l => l.Split(' ')));
        Assert.Contains("# skipped 192.168.1.130", text);
        Assert.DoesNotContain("\r", text);
        Assert.False(RouteFixScript.NeedsBom(RouteScriptTarget.Linux));
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
