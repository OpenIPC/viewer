using System.Diagnostics;
using System.Net.NetworkInformation;
using OpenIPC.Viewer.Core.LinkHealth;

namespace OpenIPC.Viewer.Infrastructure.Net;

// ICMP echo via System.Net.NetworkInformation.Ping. Where the platform refuses
// ICMP (some sandboxed mobile builds) the probe returns null instead of a fake
// 100% loss, so the analyzer doesn't call a healthy camera dead.
public sealed class IcmpPingProbe : IPingProbe
{
    private const int TimeoutMs = 1000;

    public async Task<PingStats?> PingAsync(string host, int count, int payloadBytes, TimeSpan interval, CancellationToken ct)
    {
        var buffer = Payload(payloadBytes);
        var rtts = new List<double>();
        var sent = 0;
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rtt = await SendAsync(ping, host, buffer).ConfigureAwait(false);
            if (rtt is null && sent == 0 && i == 0 && !await IsIcmpUsableAsync(host).ConfigureAwait(false))
                return null;
            sent++;
            if (rtt is { } r && r >= 0) rtts.Add(r);
            if (i < count - 1)
                await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        return Stats(payloadBytes, sent, rtts);
    }

    public async Task<PingStats?> FloodAsync(string host, int payloadBytes, TimeSpan duration, int parallel, CancellationToken ct)
    {
        var buffer = Payload(payloadBytes);
        var stop = Stopwatch.StartNew();
        var lockObj = new object();
        var rtts = new List<double>();
        var sent = 0;
        var unusable = false;

        async Task Loop()
        {
            using var ping = new Ping();
            while (stop.Elapsed < duration && !ct.IsCancellationRequested && !unusable)
            {
                double? rtt;
                try
                {
                    rtt = await SendAsync(ping, host, buffer).ConfigureAwait(false);
                }
                catch (PlatformNotSupportedException)
                {
                    unusable = true;
                    return;
                }
                lock (lockObj)
                {
                    sent++;
                    if (rtt is { } r && r >= 0) rtts.Add(r);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, parallel)).Select(_ => Task.Run(Loop, ct))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return unusable ? null : Stats(payloadBytes, sent, rtts);
    }

    // Round trip in ms, or null for a lost echo. Ping.SendPingAsync reports the
    // RTT rounded to whole ms; LAN replies under 1 ms read as 0, which is fine.
    private static async Task<double?> SendAsync(Ping ping, string host, byte[] buffer)
    {
        try
        {
            var reply = await ping.SendPingAsync(host, TimeoutMs, buffer, new PingOptions(64, dontFragment: true))
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    // Distinguishes "the camera doesn't answer" from "this process can't send
    // ICMP at all": loopback always answers when ICMP works.
    private static async Task<bool> IsIcmpUsableAsync(string host)
    {
        _ = host;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("127.0.0.1", TimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] Payload(int size)
    {
        var buffer = new byte[Math.Clamp(size, 0, 65000)];
        // A mixed pattern rather than zeros: long runs of identical bits are the
        // easy case for a marginal link.
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)((i * 37) ^ 0xA5);
        return buffer;
    }

    private static PingStats Stats(int payload, int sent, List<double> rtts)
    {
        if (rtts.Count == 0)
            return new PingStats(payload, sent, 0, null, null, null, null);
        double? jitter = null;
        if (rtts.Count > 1)
        {
            double sum = 0;
            for (var i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
            jitter = sum / (rtts.Count - 1);
        }
        return new PingStats(payload, sent, rtts.Count, rtts.Min(), rtts.Average(), rtts.Max(), jitter);
    }
}
