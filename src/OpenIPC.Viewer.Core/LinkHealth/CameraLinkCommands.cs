using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>Link mode the camera's PHY is told to advertise.</summary>
public enum CameraLinkMode
{
    // Normal auto-negotiation of 10/100 half/full.
    Auto = 0,
    // Auto-negotiation kept on, but only 10BASE-T full duplex advertised. Slower,
    // and noticeably more tolerant of a marginal cable/crimp/adapter. Kept on
    // autoneg (not forced) so the far end follows and no duplex mismatch appears.
    Force10Full,
}

// Pure builders + parsers for the SSH commands of the link-health check. Kept
// out of the Devices service so the busybox shell and the parsing of sysfs /
// /proc output are unit-testable without a camera (same split as
// FirmwareCommands). Every value is printed as a tagged KEY=VALUE line so the
// parser is order-independent and survives a missing file (empty value).
public static class CameraLinkCommands
{
    // Interface that carries the SSH session: the route back to the SSH client
    // first (works on isolated camera networks without a default gateway), then
    // the default route, then the usual names.
    private const string ResolveInterface = """
        C=${SSH_CLIENT%% *}
        IF=
        [ -n "$C" ] && IF=$(ip route get "$C" 2>/dev/null | awk '{for(i=1;i<NF;i++) if($i=="dev"){print $(i+1); exit}}')
        [ -z "$IF" ] && IF=$(ip route 2>/dev/null | awk '/^default/{for(i=1;i<NF;i++) if($i=="dev"){print $(i+1); exit}}')
        [ -z "$IF" ] && for d in eth0 end0 wlan0; do [ -e /sys/class/net/$d ] && IF=$d && break; done
        [ -z "$IF" ] && IF=eth0
        """;

    /// <summary>One read-only command that dumps everything the analyzer needs.</summary>
    public static string ReadLinkState() => ResolveInterface + "\n" + """
        S=/sys/class/net/$IF
        echo "if=$IF"
        for f in operstate carrier speed duplex carrier_changes carrier_up_count carrier_down_count; do echo "$f=$(cat $S/$f 2>/dev/null)"; done
        for f in rx_packets tx_packets rx_errors tx_errors rx_crc_errors rx_frame_errors rx_dropped collisions; do echo "st_$f=$(cat $S/statistics/$f 2>/dev/null)"; done
        echo "uptime=$(cut -d' ' -f1 /proc/uptime 2>/dev/null)"
        if command -v ethtool >/dev/null 2>&1; then echo "ethtool=1"; ethtool "$IF" 2>/dev/null | sed 's/^/eth:/'; else echo "ethtool=0"; fi
        awk 'NR>1 && $4=="01"{print "tcp="$2" "$3}' /proc/net/tcp 2>/dev/null
        echo "end=1"
        """;

    /// <summary>
    /// True when <paramref name="output"/> is a whole run of <see cref="ReadLinkState"/>:
    /// it names the interface and reaches the closing <c>end=1</c> marker. A
    /// failed or cut-off command must not pass for measurements (the parser
    /// would otherwise fill in defaults such as <c>eth0</c>).
    /// </summary>
    public static bool IsComplete(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var hasInterface = false;
        var hasEnd = false;
        foreach (var raw in output!.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("if=", StringComparison.Ordinal) && line.Length > 3) hasInterface = true;
            else if (line == "end=1") hasEnd = true;
        }
        return hasInterface && hasEnd;
    }

    public const string DefaultInitScript = "/etc/init.d/S95majestic";

    /// <summary>
    /// Restarts the streamer, which drops every RTSP client — stale sessions left
    /// behind by a computer that rebooted or lost the link included. Uses the
    /// init script when present; otherwise a SIGHUP reload. The success marker
    /// is chained with <c>&amp;&amp;</c>, so a failing restart leaves a non-zero exit
    /// status instead of being masked by the echo.
    /// </summary>
    public static string RestartStreamer(string initScript = DefaultInitScript)
    {
        var q = ShQuote(initScript);
        return $"if [ -x {q} ]; then {q} restart >/dev/null 2>&1 && echo restarted=init; " +
               "else killall -HUP majestic && echo restarted=hup; fi";
    }

    /// <summary>
    /// Changes what the PHY advertises. Not persistent: the camera returns to
    /// normal auto-negotiation on its next reboot. Exit code 3 = no ethtool.
    /// </summary>
    public static string SetLinkMode(CameraLinkMode mode)
    {
        var advertise = mode == CameraLinkMode.Force10Full ? "0x002" : "0x00f";
        return ResolveInterface + "\n" +
               "command -v ethtool >/dev/null 2>&1 || { echo noethtool=1; exit 3; }\n" +
               $"ethtool -s \"$IF\" autoneg on advertise {advertise} && echo ok=1";
    }

    /// <summary>
    /// Reads back what the PHY advertises (ethtool), to confirm a link-mode
    /// change whose SSH channel was cut by the renegotiation. Exit code 3 = no ethtool.
    /// </summary>
    public static string ReadLinkMode() => ResolveInterface + "\n" +
        "command -v ethtool >/dev/null 2>&1 || { echo noethtool=1; exit 3; }\n" +
        "echo \"if=$IF\"\n" +
        "ethtool \"$IF\" 2>/dev/null | sed 's/^/eth:/'\n" +
        "echo \"end=1\"";

    /// <summary>Whether ethtool's "Advertised link modes" match <paramref name="mode"/>.</summary>
    public static bool AdvertisesMode(string? advertised, CameraLinkMode mode)
    {
        if (string.IsNullOrWhiteSpace(advertised)) return false;
        var has100 = advertised!.IndexOf("100baseT", StringComparison.OrdinalIgnoreCase) >= 0;
        return mode == CameraLinkMode.Force10Full
            ? !has100 && advertised.IndexOf("10baseT/Full", StringComparison.OrdinalIgnoreCase) >= 0
            : has100;
    }

    // Single-quoted sh literal.
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static CameraLinkSnapshot Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var connections = new List<CameraTcpConnection>();
        string? autoneg = null;
        string? partner = null;
        string? advertised = null;
        string? ethKey = null;

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("eth:", StringComparison.Ordinal))
            {
                ParseEthtoolLine(line.Substring(4), ref ethKey, ref autoneg, ref partner, ref advertised);
                continue;
            }
            if (line.StartsWith("tcp=", StringComparison.Ordinal))
            {
                if (TryParseProcTcp(line.Substring(4), out var conn))
                    connections.Add(conn);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }

        string? Str(string k) => values.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        long? Long(string k) => Str(k) is { } v && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        // speed reads -1 (or fails) while the link is down — that's "unknown", not a speed.
        var speed = Long("speed") is { } sp && sp > 0 ? (int?)sp : null;
        var carrierRaw = Long("carrier");
        bool? carrier = carrierRaw == 1 ? true : carrierRaw == 0 ? false : null;
        double? uptime = Str("uptime") is { } up && double.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : null;

        return new CameraLinkSnapshot(
            Interface: Str("if") ?? "eth0",
            OperState: Str("operstate"),
            Carrier: carrier,
            SpeedMbps: speed,
            Duplex: Str("duplex"),
            CarrierChanges: Long("carrier_changes"),
            CarrierUpCount: Long("carrier_up_count"),
            CarrierDownCount: Long("carrier_down_count"),
            RxPackets: Long("st_rx_packets"),
            TxPackets: Long("st_tx_packets"),
            RxErrors: Long("st_rx_errors"),
            TxErrors: Long("st_tx_errors"),
            RxCrcErrors: Long("st_rx_crc_errors"),
            RxFrameErrors: Long("st_rx_frame_errors"),
            RxDropped: Long("st_rx_dropped"),
            Collisions: Long("st_collisions"),
            UptimeSeconds: uptime,
            HasEthtool: Str("ethtool") == "1",
            AutoNegotiation: autoneg is null ? null : autoneg.Equals("on", StringComparison.OrdinalIgnoreCase),
            LinkPartnerModes: partner,
            Connections: connections,
            AdvertisedModes: advertised);
    }

    private const string PartnerKey = "Link partner advertised link modes";
    private const string AdvertisedKey = "Advertised link modes";

    // ethtool prints "Key: value" with multi-line values continued on indented
    // lines that have no colon of their own.
    private static void ParseEthtoolLine(
        string line, ref string? key, ref string? autoneg, ref string? partner, ref string? advertised)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        var colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            key = trimmed.Substring(0, colon).Trim();
            var value = trimmed.Substring(colon + 1).Trim();
            if (key.Equals("Auto-negotiation", StringComparison.OrdinalIgnoreCase))
                autoneg = value;
            else if (key.Equals(PartnerKey, StringComparison.OrdinalIgnoreCase))
                partner = value;
            else if (key.Equals(AdvertisedKey, StringComparison.OrdinalIgnoreCase))
                advertised = value;
        }
        else if (key is not null && key.Equals(PartnerKey, StringComparison.OrdinalIgnoreCase))
        {
            partner = string.IsNullOrEmpty(partner) ? trimmed : partner + " " + trimmed;
        }
        else if (key is not null && key.Equals(AdvertisedKey, StringComparison.OrdinalIgnoreCase))
        {
            advertised = string.IsNullOrEmpty(advertised) ? trimmed : advertised + " " + trimmed;
        }
    }

    // /proc/net/tcp row fields 2 and 3: "0100A8C0:022A 0A00A8C0:D431" — IPv4 in
    // host (little-endian) byte order, port in big-endian hex.
    internal static bool TryParseProcTcp(string pair, out CameraTcpConnection connection)
    {
        connection = null!;
        var parts = pair.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!TryParseEndpoint(parts[0], out var la, out var lp)) return false;
        if (!TryParseEndpoint(parts[1], out var ra, out var rp)) return false;
        connection = new CameraTcpConnection(la, lp, ra, rp);
        return true;
    }

    private static bool TryParseEndpoint(string hex, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        var colon = hex.IndexOf(':');
        if (colon != 8) return false; // IPv4 rows only
        if (!uint.TryParse(hex.Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ip)) return false;
        if (!int.TryParse(hex.Substring(9), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port)) return false;
        address = string.Join(".",
            (ip & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((ip >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((ip >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((ip >> 24) & 0xFF).ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
