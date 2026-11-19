using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>OS the fix script is written for.</summary>
public enum RouteScriptTarget
{
    Windows = 0,
    Linux,
    MacOs,
}

// Writes the script that pins each camera's address to the adapter it is on
// (a /32 on-link route, so it wins over the shared /24 regardless of adapter
// metrics). The app never runs it on its own: changing routes needs
// administrator rights, so the user runs it (or approves the UAC prompt).
//
// Windows: idempotent PowerShell with -WhatIf (preview, no admin needed),
// -Undo, routes in both the active and the persistent store, and a check of
// where each camera routes afterwards. Linux/macOS: a POSIX sh with --undo;
// those routes last until reboot (the script says how to keep them).
//
// Every value put into the script is either an IPv4 literal, a number, or an
// adapter name that is quoted (PowerShell) or validated (sh) — nothing from a
// camera or the network reaches the shell unquoted.
public static class RouteFixScript
{
    private static readonly Regex SafeUnixName = new("^[A-Za-z0-9_.:@-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant);
    private static readonly Regex Mac = new("^[0-9A-Fa-f]{2}([-:][0-9A-Fa-f]{2}){5}$", RegexOptions.CultureInvariant);

    public static string FileName(RouteScriptTarget target) =>
        target == RouteScriptTarget.Windows ? "fix-camera-routes.ps1" : "fix-camera-routes.sh";

    /// <summary>PowerShell must read the file as UTF-8 (adapter names can be non-ASCII); sh must not see a BOM.</summary>
    public static bool NeedsBom(RouteScriptTarget target) => target == RouteScriptTarget.Windows;

    public static string Build(RouteFixPlan plan, RouteScriptTarget target, DateTimeOffset generatedAt, bool russian)
    {
        var t = russian ? Texts.Ru : Texts.En;
        var routes = plan.Routes.Where(r => Ipv4.IsMatch(r.CameraIp)).ToList();
        var conflicts = plan.Conflicts.Where(c => Ipv4.IsMatch(c.Address) && Mac.IsMatch(c.ForeignMac)).ToList();
        // One line-ending style per file whatever the source file used: CRLF for
        // PowerShell (Notepad-friendly), LF for sh (CR would end up in arguments).
        return target == RouteScriptTarget.Windows
            ? Normalize(PowerShell(routes, conflicts, generatedAt, t), "\r\n")
            : Normalize(Shell(routes, conflicts, target == RouteScriptTarget.MacOs, generatedAt, t), "\n");
    }

    private static string Normalize(string text, string newline) =>
        text.Replace("\r\n", "\n").Replace("\n", newline);

    /// <summary>The script for the OS this process runs on.</summary>
    public static RouteScriptTarget CurrentTarget()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return RouteScriptTarget.Windows;
        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
            ? RouteScriptTarget.MacOs
            : RouteScriptTarget.Linux;
    }

    private static string PowerShell(List<RouteFixEntry> routes, List<AddressConflict> conflicts, DateTimeOffset at, Texts t)
    {
        var sb = new StringBuilder();
        sb.Append("<#\r\n");
        sb.Append("  OpenIPC Viewer: ").Append(t.Title).Append("\r\n");
        sb.Append("  ").Append(t.Generated).Append(' ').Append(at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n\r\n");
        foreach (var line in t.About) sb.Append("  ").Append(line).Append("\r\n");
        sb.Append("\r\n");
        sb.Append("  ").Append(t.UsageRun).Append("   .\\fix-camera-routes.ps1\r\n");
        sb.Append("  ").Append(t.UsagePreview).Append("   .\\fix-camera-routes.ps1 -WhatIf\r\n");
        sb.Append("  ").Append(t.UsageUndo).Append("   .\\fix-camera-routes.ps1 -Undo\r\n");
        sb.Append("#>\r\n");
        sb.Append("[CmdletBinding(SupportsShouldProcess = $true)]\r\n");
        sb.Append("param([switch]$Undo, [switch]$Pause)\r\n\r\n");
        sb.Append("$ErrorActionPreference = 'Stop'\r\n\r\n");

        sb.Append("$msg = @{\r\n");
        foreach (var kv in t.Messages)
            sb.Append("    ").Append(kv.Key).Append(" = '").Append(Ps(kv.Value)).Append("'\r\n");
        sb.Append("}\r\n\r\n");

        sb.Append("# ").Append(t.RoutesComment).Append("\r\n");
        sb.Append("$routes = @(\r\n");
        foreach (var r in routes)
        {
            sb.Append("    [pscustomobject]@{ Camera = '").Append(r.CameraIp)
              .Append("'; Alias = '").Append(Ps(r.InterfaceName))
              .Append("'; Index = ").Append(r.InterfaceIndex.ToString(CultureInfo.InvariantCulture))
              .Append("; Evidence = '").Append(r.Evidence.ToString()).Append("' }\r\n");
        }
        sb.Append(")\r\n\r\n");

        sb.Append("$conflicts = @(\r\n");
        foreach (var c in conflicts)
        {
            sb.Append("    [pscustomobject]@{ Address = '").Append(c.Address)
              .Append("'; Own = '").Append(Ps(c.OwnInterface))
              .Append("'; SeenOn = '").Append(Ps(c.SeenOnInterface))
              .Append("'; Mac = '").Append(c.ForeignMac).Append("' }\r\n");
        }
        sb.Append(")\r\n");

        sb.Append(PowerShellBody);
        return sb.ToString();
    }

    // The fixed part. Messages come from $msg, values from $routes/$conflicts.
    // New-NetRoute without -PolicyStore writes the route to BOTH the active and
    // the persistent store (Microsoft docs: "By default, a route is saved in
    // both stores"); adding the persistent copy separately after the active one
    // fails, which is what stopped the first version of this script after one
    // camera. Every camera is now handled on its own — a failure is reported and
    // the next camera still runs — the window waits for Enter (-Pause) even after
    // an error, and the outcome goes to fix-camera-routes.log next to the script.
    // Exit codes: 0 done, 1 not elevated, 2 some cameras failed, 3 crashed.
    private const string PowerShellBody = """


        # Load the network modules up front with -WhatIf off: their auto-import
        # would otherwise list its own aliases as "What if" noise.
        $whatIf = $WhatIfPreference
        $WhatIfPreference = $false
        Import-Module NetTCPIP, NetAdapter -ErrorAction SilentlyContinue
        $WhatIfPreference = $whatIf

        $logPath = if ($PSScriptRoot) { Join-Path $PSScriptRoot 'fix-camera-routes.log' } else { $null }
        $logging = [bool]$logPath -and -not $WhatIfPreference
        if ($logging) {
            try { Set-Content -LiteralPath $logPath -Value @() -Encoding UTF8 -WhatIf:$false -ErrorAction Stop } catch { $logging = $false }
        }

        function Write-Step([string]$text, [string]$color = 'Gray') {
            Write-Host $text -ForegroundColor $color
            if ($script:logging) {
                try {
                    Add-Content -LiteralPath $script:logPath -Value ('{0:HH:mm:ss} {1}' -f (Get-Date), $text) `
                        -Encoding UTF8 -WhatIf:$false -ErrorAction Stop
                } catch { }
            }
        }

        function Get-RouteAdapter($r) {
            $a = Get-NetAdapter -Name $r.Alias -ErrorAction SilentlyContinue
            if (-not $a) { $a = Get-NetAdapter -InterfaceIndex $r.Index -ErrorAction SilentlyContinue }
            $a
        }

        function Test-PinnedRoute([string]$prefix, [int]$ifIndex, [string]$store) {
            [bool](Get-NetRoute -DestinationPrefix $prefix -InterfaceIndex $ifIndex -PolicyStore $store -ErrorAction SilentlyContinue)
        }

        $code = 0
        try {
            $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
            if (-not $isAdmin -and -not $WhatIfPreference) {
                Write-Step $msg.NotAdmin 'Red'
                $code = 1
            } else {
                foreach ($c in $conflicts) {
                    Write-Step ($msg.Conflict -f $c.Address, $c.Own, $c.Mac, $c.SeenOn) 'Cyan'
                    Write-Step $msg.ConflictHint
                }
                if ($routes.Count -eq 0) { Write-Step $msg.Nothing }

                $failed = 0
                foreach ($r in $routes) {
                    $prefix = "$($r.Camera)/32"
                    try {
                        # Earlier pins for this camera in either store (a half-finished run included).
                        foreach ($store in 'ActiveStore', 'PersistentStore') {
                            foreach ($old in @(Get-NetRoute -DestinationPrefix $prefix -PolicyStore $store -ErrorAction SilentlyContinue)) {
                                if ($PSCmdlet.ShouldProcess("$prefix -> $($old.InterfaceAlias) [$store]", $msg.ActRemove)) {
                                    try {
                                        Remove-NetRoute -InputObject $old -Confirm:$false -ErrorAction Stop
                                    } catch {
                                        # Removing one store's copy can take the other with it.
                                        if (Test-PinnedRoute $prefix $old.ifIndex $store) { throw }
                                    }
                                }
                            }
                        }
                        if ($Undo) { continue }

                        $adapter = Get-RouteAdapter $r
                        if (-not $adapter) {
                            Write-Step ($msg.AdapterMissing -f $r.Alias, $r.Camera) 'Yellow'
                            $failed++
                            continue
                        }
                        if ($PSCmdlet.ShouldProcess("$prefix -> $($adapter.Name)", $msg.ActAdd)) {
                            # No -PolicyStore: active + persistent in one call (survives a reboot).
                            New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $adapter.ifIndex -NextHop '0.0.0.0' `
                                -RouteMetric 1 -ErrorAction Stop | Out-Null
                            if (-not (Test-PinnedRoute $prefix $adapter.ifIndex 'ActiveStore')) {
                                throw ($msg.NotActive -f $prefix)
                            }
                            if (-not (Test-PinnedRoute $prefix $adapter.ifIndex 'PersistentStore')) {
                                Write-Step ($msg.NotPersistent -f $r.Camera) 'Yellow'
                            }
                        }
                    } catch {
                        $failed++
                        Write-Step ($msg.Failed -f $r.Camera, $_.Exception.Message) 'Red'
                    }
                }

                if (-not $WhatIfPreference) {
                    foreach ($r in $routes) {
                        $via = (Find-NetRoute -RemoteIPAddress $r.Camera -ErrorAction SilentlyContinue |
                            Where-Object { $_.InterfaceAlias } | Select-Object -First 1).InterfaceAlias
                        $expected = (Get-RouteAdapter $r).Name
                        if ($Undo) {
                            Write-Step ($msg.ViaNow -f $r.Camera, $via)
                        } elseif ($via -and $via -eq $expected) {
                            Write-Step ($msg.VerifyOk -f $r.Camera, $via) 'Green'
                        } else {
                            Write-Step ($msg.VerifyBad -f $r.Camera, $via, $expected) 'Yellow'
                        }
                    }
                }

                if ($failed -gt 0) {
                    Write-Step ($msg.SomeFailed -f $failed) 'Red'
                    $code = 2
                } else {
                    Write-Step $msg.Done 'Green'
                }
            }
        } catch {
            Write-Step ($msg.Crashed -f $_.Exception.Message) 'Red'
            $code = 3
        } finally {
            if ($logging) { Write-Host ($msg.LogAt -f $logPath) }
            if ($Pause) { [void](Read-Host $msg.PressEnter) }
        }
        exit $code

        """;

    private static string Shell(List<RouteFixEntry> routes, List<AddressConflict> conflicts, bool mac, DateTimeOffset at, Texts t)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# OpenIPC Viewer: ").Append(t.Title).Append('\n');
        sb.Append("# ").Append(t.Generated).Append(' ').Append(at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n#\n");
        foreach (var line in t.About) sb.Append("# ").Append(line).Append('\n');
        sb.Append("#\n");
        sb.Append("# ").Append(t.UsageRun).Append("   sudo sh fix-camera-routes.sh\n");
        sb.Append("# ").Append(t.UsageUndo).Append("   sudo sh fix-camera-routes.sh --undo\n");
        sb.Append("#\n");
        foreach (var line in mac ? t.PersistMac : t.PersistLinux) sb.Append("# ").Append(line).Append('\n');
        sb.Append('\n');

        sb.Append("if [ \"$(id -u)\" -ne 0 ]; then echo ").Append(Sh(t.Messages["NotAdmin"])).Append(" >&2; exit 1; fi\n");
        sb.Append("undo=0; [ \"${1:-}\" = \"--undo\" ] && undo=1\n\n");

        foreach (var c in conflicts)
        {
            var text = string.Format(CultureInfo.InvariantCulture, t.Messages["Conflict"], c.Address, c.OwnInterface, c.ForeignMac, c.SeenOnInterface);
            sb.Append("echo ").Append(Sh("WARNING: " + text)).Append(" >&2\n");
            sb.Append("echo ").Append(Sh(t.Messages["ConflictHint"])).Append(" >&2\n");
        }

        if (mac)
        {
            sb.Append("""
                pin() {
                  route -n delete -host "$1" >/dev/null 2>&1
                  if [ "$undo" -eq 0 ]; then route -n add -host "$1" -interface "$2" >/dev/null || return 1; fi
                  echo "$1 -> $(route -n get "$1" 2>/dev/null | awk '/interface:/{print $2}')"
                }

                """);
        }
        else
        {
            sb.Append("""
                pin() {
                  if [ "$undo" -eq 1 ]; then
                    ip route del "$1/32" dev "$2" 2>/dev/null
                  else
                    ip route replace "$1/32" dev "$2" scope link || return 1
                  fi
                  echo "$1 -> $(ip route get "$1" 2>/dev/null | head -n 1)"
                }

                """);
        }

        var any = false;
        foreach (var r in routes)
        {
            if (!SafeUnixName.IsMatch(r.InterfaceName))
            {
                sb.Append("# skipped ").Append(r.CameraIp).Append(": unusual adapter name\n");
                continue;
            }
            sb.Append("pin ").Append(r.CameraIp).Append(' ').Append(r.InterfaceName).Append('\n');
            any = true;
        }
        if (!any)
            sb.Append("echo ").Append(Sh(t.Messages["Nothing"])).Append('\n');
        sb.Append("echo ").Append(Sh(t.Messages["Done"])).Append('\n');
        return sb.ToString();
    }

    private static string Ps(string s) => s.Replace("'", "''");

    // Single-quoted sh literal.
    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private sealed class Texts
    {
        public string Title = "";
        public string Generated = "";
        public string[] About = Array.Empty<string>();
        public string UsageRun = "";
        public string UsagePreview = "";
        public string UsageUndo = "";
        public string RoutesComment = "";
        public string[] PersistLinux = Array.Empty<string>();
        public string[] PersistMac = Array.Empty<string>();
        public Dictionary<string, string> Messages = new();

        public static readonly Texts En = new()
        {
            Title = "camera route fix",
            Generated = "Generated",
            About = new[]
            {
                "Two adapters of this computer share the cameras' subnet (for example Wi-Fi",
                "from the home router and the camera Ethernet port, both 192.168.1.x). The OS",
                "then sends camera traffic out of whichever adapter has the better metric, and",
                "cameras on the cable look offline. This pins every camera's address to the",
                "adapter the camera is really connected to.",
            },
            UsageRun = "Run as administrator:",
            UsagePreview = "Preview without changes:",
            UsageUndo = "Remove the routes again:",
            RoutesComment = "Camera address -> adapter it is connected to (Evidence: Stream = its stream port answered there, Arp = it answered ARP there, Neighbors = the other cameras of its subnet are there).",
            PersistLinux = new[]
            {
                "These routes last until reboot. To keep them, add each one to the connection,",
                "e.g. NetworkManager: nmcli connection modify \"<connection>\" +ipv4.routes \"<camera>/32\"",
            },
            PersistMac = new[]
            {
                "These routes last until reboot. To keep them, run this script at login",
                "(e.g. from a launchd job).",
            },
            Messages = new()
            {
                ["NotAdmin"] = "Run this script as administrator (right-click PowerShell, Run as administrator).",
                ["Conflict"] = "{0} (this computer's address on {1}) is also used by device {2} on {3}.",
                ["ConflictHint"] = "Harmless for the cameras while the two networks stay apart; a real clash only if they are joined. To clear it change this computer's address on the camera network or give the cameras their own subnet (for example 192.168.10.x); the script does not change addresses.",
                ["Nothing"] = "Nothing to change.",
                ["AdapterMissing"] = "Adapter '{0}' not found, skipped {1}.",
                ["ActAdd"] = "Add route",
                ["ActRemove"] = "Remove route",
                ["VerifyOk"] = "{0}: now goes via {1} - OK",
                ["VerifyBad"] = "{0}: goes via '{1}', expected '{2}'",
                ["ViaNow"] = "{0}: now goes via {1}",
                ["NotActive"] = "the route {0} did not appear in the routing table",
                ["NotPersistent"] = "{0}: pinned until the next reboot only (the persistent copy was not saved)",
                ["Failed"] = "{0}: not pinned - {1}",
                ["SomeFailed"] = "Cameras not pinned: {0}. See the messages above.",
                ["Crashed"] = "The script stopped: {0}",
                ["LogAt"] = "Log: {0}",
                ["Done"] = "Done.",
                ["PressEnter"] = "Press Enter to close",
            },
        };

        public static readonly Texts Ru = new()
        {
            Title = "исправление маршрутов к камерам",
            Generated = "Создан",
            About = new[]
            {
                "Два адаптера этого компьютера находятся в одной подсети с камерами (например,",
                "Wi-Fi от домашнего роутера и Ethernet-порт камер, оба 192.168.1.x). Тогда система",
                "отправляет трафик камер через адаптер с лучшей метрикой, и камеры в кабеле",
                "выглядят отключёнными. Скрипт закрепляет адрес каждой камеры за адаптером,",
                "к которому она действительно подключена.",
            },
            UsageRun = "Запуск от администратора:",
            UsagePreview = "Посмотреть без изменений:",
            UsageUndo = "Убрать маршруты обратно:",
            RoutesComment = "Адрес камеры -> адаптер, к которому она подключена (Evidence: Stream = там ответил порт потока, Arp = там ответила на ARP, Neighbors = там остальные камеры её подсети).",
            PersistLinux = new[]
            {
                "Эти маршруты действуют до перезагрузки. Чтобы сохранить, добавьте их в подключение,",
                "например NetworkManager: nmcli connection modify \"<подключение>\" +ipv4.routes \"<камера>/32\"",
            },
            PersistMac = new[]
            {
                "Эти маршруты действуют до перезагрузки. Чтобы сохранить, запускайте скрипт при входе",
                "(например, через launchd).",
            },
            Messages = new()
            {
                ["NotAdmin"] = "Запустите скрипт от имени администратора (правый клик по PowerShell, «Запуск от имени администратора»).",
                ["Conflict"] = "{0} (адрес этого компьютера в сети {1}) занят также устройством {2} в сети {3}.",
                ["ConflictHint"] = "Камерам это не мешает, пока сети разделены; настоящий конфликт будет, только если их соединить. Чтобы убрать, смените адрес компьютера в сети камер или выделите камерам отдельную подсеть (например 192.168.10.x); адреса скрипт не меняет.",
                ["Nothing"] = "Менять нечего.",
                ["AdapterMissing"] = "Адаптер '{0}' не найден, {1} пропущена.",
                ["ActAdd"] = "Добавить маршрут",
                ["ActRemove"] = "Удалить маршрут",
                ["VerifyOk"] = "{0}: теперь идёт через {1} - OK",
                ["VerifyBad"] = "{0}: идёт через '{1}', а должно через '{2}'",
                ["ViaNow"] = "{0}: сейчас идёт через {1}",
                ["NotActive"] = "маршрут {0} не появился в таблице маршрутов",
                ["NotPersistent"] = "{0}: закреплена только до перезагрузки (постоянная копия не сохранилась)",
                ["Failed"] = "{0}: не закреплена - {1}",
                ["SomeFailed"] = "Не закреплено камер: {0}. Подробности выше.",
                ["Crashed"] = "Скрипт остановился: {0}",
                ["LogAt"] = "Журнал: {0}",
                ["Done"] = "Готово.",
                ["PressEnter"] = "Нажмите Enter, чтобы закрыть",
            },
        };
    }
}
