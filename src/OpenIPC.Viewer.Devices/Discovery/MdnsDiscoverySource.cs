using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Devices.Discovery;

// Passive mDNS source — catches OpenIPC cameras that advertise _rtsp/_http/_onvif
// over zeroconf without an active sweep. Hand-rolled (no NuGet): builds the DNS
// query and parses the responses (name compression, A/PTR/SRV/TXT).
//
// Android-safe by the same trick as WS-Discovery: queries set the mDNS QU bit
// (unicast-response-requested), so responders reply UNICAST to our ephemeral
// source port. We therefore never join the 224.0.0.251 multicast group and need
// no WifiManager.MulticastLock. Not every responder honours QU — the subnet
// sweep (Slice C) covers the stubborn ones.
public sealed class MdnsDiscoverySource : IDiscoverySource
{
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly string[] Services =
    {
        "_rtsp._tcp.local",
        "_http._tcp.local",
        "_onvif._tcp.local",
    };

    private readonly INetworkInterfaceProvider _nics;
    private readonly IUserSettingsAccessor _settings;
    private readonly ILogger<MdnsDiscoverySource> _logger;

    public MdnsDiscoverySource(
        INetworkInterfaceProvider nics,
        IUserSettingsAccessor settings,
        ILogger<MdnsDiscoverySource> logger)
    {
        _nics = nics;
        _settings = settings;
        _logger = logger;
    }

    public string Name => "mDNS";

    public bool IsEnabled(DiscoveryOptions options) => true;

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var bind = NetworkInterfaceSelector.ResolveBindAddress(_nics.GetCandidates(), _settings.PreferredNetworkInterface);
        var local = !string.IsNullOrWhiteSpace(bind) && IPAddress.TryParse(bind, out var p) ? p : IPAddress.Any;

        using var client = new UdpClient(new IPEndPoint(local, 0));
        try { client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); }
        catch { /* best effort */ }

        var query = BuildQuery(Services);
        try
        {
            await client.SendAsync(query, query.Length, MdnsGroup).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "mDNS query send failed");
            progress?.Report(1.0);
            yield break;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS receive failed");
                break;
            }

            DiscoveredDevice? device = null;
            try { device = TryParseResponse(result.Buffer, result.RemoteEndPoint); }
            catch (Exception ex) { _logger.LogDebug(ex, "mDNS parse failed"); }

            if (device is not null)
                yield return device;
        }

        progress?.Report(1.0);
    }

    // --- DNS message build ---------------------------------------------------

    private static byte[] BuildQuery(string[] services)
    {
        var buf = new List<byte>(64);
        buf.AddRange(new byte[] { 0, 0, 0, 0 });          // id=0, flags=0 (standard query)
        WriteUInt16(buf, (ushort)services.Length);         // QDCOUNT
        buf.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });     // AN/NS/AR = 0

        foreach (var service in services)
        {
            WriteName(buf, service);
            WriteUInt16(buf, 12);     // QTYPE = PTR
            WriteUInt16(buf, 0x8001); // QU bit (unicast response) + QCLASS IN
        }
        return buf.ToArray();
    }

    private static void WriteName(List<byte> buf, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
    }

    private static void WriteUInt16(List<byte> buf, ushort value)
    {
        buf.Add((byte)(value >> 8));
        buf.Add((byte)(value & 0xFF));
    }

    // --- DNS response parse --------------------------------------------------

    private static DiscoveredDevice? TryParseResponse(byte[] msg, IPEndPoint from)
    {
        if (msg.Length < 12) return null;

        int qd = (msg[4] << 8) | msg[5];
        int an = (msg[6] << 8) | msg[7];
        int ns = (msg[8] << 8) | msg[9];
        int ar = (msg[10] << 8) | msg[11];

        var offset = 12;
        // Skip questions.
        for (var i = 0; i < qd && offset < msg.Length; i++)
        {
            ReadName(msg, ref offset);
            offset += 4; // QTYPE + QCLASS
        }

        string? ipv4 = null;
        var protocols = DiscoveryProtocol.Mdns;
        var ports = new List<int>();
        string? name = null;

        var total = an + ns + ar;
        for (var i = 0; i < total && offset < msg.Length; i++)
        {
            var owner = ReadName(msg, ref offset);
            if (offset + 10 > msg.Length) break;
            int type = (msg[offset] << 8) | msg[offset + 1];
            int rdlen = (msg[offset + 8] << 8) | msg[offset + 9];
            offset += 10;
            if (offset + rdlen > msg.Length) break;
            var rdata = offset;

            // Service tokens can appear in any owner name (PTR=service type,
            // SRV/TXT=instance) — fold them into the protocol set.
            protocols |= ProtocolsFromName(owner);

            switch (type)
            {
                case 1 when rdlen == 4: // A
                    ipv4 ??= $"{msg[rdata]}.{msg[rdata + 1]}.{msg[rdata + 2]}.{msg[rdata + 3]}";
                    break;
                case 33 when rdlen >= 6: // SRV: priority(2) weight(2) port(2) target
                    var port = (msg[rdata + 4] << 8) | msg[rdata + 5];
                    if (port > 0) ports.Add(port);
                    name ??= FirstLabel(owner);
                    break;
                case 12: // PTR rdata = instance name
                    var inner = rdata;
                    name ??= FirstLabel(ReadName(msg, ref inner));
                    break;
            }

            offset += rdlen;
        }

        // Fall back to the responder's source address if it sent no A record.
        ipv4 ??= from.Address.AddressFamily == AddressFamily.InterNetwork ? from.Address.ToString() : null;
        if (ipv4 is null) return null;

        return new DiscoveredDevice(ipv4, protocols, ports, Name: name);
    }

    private static DiscoveryProtocol ProtocolsFromName(string name)
    {
        var p = DiscoveryProtocol.None;
        if (name.Contains("_rtsp", StringComparison.OrdinalIgnoreCase)) p |= DiscoveryProtocol.Rtsp;
        if (name.Contains("_http", StringComparison.OrdinalIgnoreCase)) p |= DiscoveryProtocol.Http;
        if (name.Contains("_onvif", StringComparison.OrdinalIgnoreCase)) p |= DiscoveryProtocol.Onvif;
        return p;
    }

    private static string? FirstLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var dot = name.IndexOf('.');
        var label = dot < 0 ? name : name.Substring(0, dot);
        return label.StartsWith("_", StringComparison.Ordinal) ? null : label;
    }

    // Reads a DNS name with 0xC0 compression pointers. Advances `offset` past the
    // name (or, when it jumped, to just after the first pointer). Bounded so a
    // malformed self-referential packet can't loop forever.
    private static string ReadName(byte[] msg, ref int offset)
    {
        var labels = new List<string>();
        var pos = offset;
        var jumped = false;
        var afterPointer = -1;
        var safety = 0;

        while (safety++ < 128 && pos < msg.Length)
        {
            int len = msg[pos];
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= msg.Length) break;
                var ptr = ((len & 0x3F) << 8) | msg[pos + 1];
                if (!jumped) afterPointer = pos + 2;
                jumped = true;
                pos = ptr;
                continue;
            }
            if (len == 0) { pos++; break; }
            pos++;
            if (pos + len > msg.Length) break;
            labels.Add(Encoding.ASCII.GetString(msg, pos, len));
            pos += len;
        }

        offset = jumped ? afterPointer : pos;
        return string.Join(".", labels);
    }
}
