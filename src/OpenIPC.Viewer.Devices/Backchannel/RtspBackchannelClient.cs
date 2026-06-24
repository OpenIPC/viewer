using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Devices.Backchannel;

// Minimal ONVIF Profile T backchannel client (Phase 17.5). FFmpeg can't do this,
// so we speak just enough RTSP to negotiate a sendonly audio track and push RTP
// at it over a single TCP connection (interleaved framing — NAT/firewall safe,
// no extra UDP ports). Handles Basic + Digest auth. Verified shape against the
// ONVIF streaming spec + go2rtc's backchannel handling; needs a real Profile-T
// camera with a speaker to validate end-to-end.
public sealed class RtspBackchannelClient : IAudioBackchannelClient
{
    private readonly ILogger<RtspBackchannelClient> _logger;

    public RtspBackchannelClient(ILogger<RtspBackchannelClient> logger)
    {
        _logger = logger;
    }

    private const string BackchannelRequire = "www.onvif.org/ver20/backchannel";

    public async Task<IAudioBackchannelSession?> OpenAsync(BackchannelEndpoint endpoint, CancellationToken ct)
    {
        var uri = endpoint.RtspUri;
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 554;
        var baseUrl = StripUserInfo(uri);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var session = new Session(tcp, stream, _logger);

            var auth = new DigestState(endpoint.Credentials);
            var cseq = 0;

            // OPTIONS — also primes auth so DESCRIBE doesn't always cost a 401.
            await session.RequestAsync("OPTIONS", baseUrl, ++cseq, auth, null, ct).ConfigureAwait(false);

            // DESCRIBE with the backchannel Require header → SDP listing the
            // sendonly audio track we push into.
            var describe = await session.RequestAsync("DESCRIBE", baseUrl, ++cseq, auth,
                new[] { ("Accept", "application/sdp"), ("Require", BackchannelRequire) }, ct).ConfigureAwait(false);
            if (describe.Status != 200)
                throw new InvalidOperationException($"DESCRIBE failed: {describe.Status}");

            // No backchannel track is the expected "camera has no speaker" case,
            // not an error — return null so callers don't throw on every press.
            if (SdpParser.FindBackchannelAudio(describe.Body) is not { } track)
            {
                _logger.LogInformation("Camera advertises no backchannel audio track");
                tcp.Dispose();
                return null;
            }

            var controlUrl = ResolveControl(baseUrl, track.Control);

            // SETUP the backchannel track over TCP interleaved (channels 0/1).
            var setup = await session.RequestAsync("SETUP", controlUrl, ++cseq, auth,
                new[]
                {
                    ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"),
                    ("Require", BackchannelRequire),
                }, ct).ConfigureAwait(false);
            if (setup.Status != 200)
                throw new InvalidOperationException($"SETUP failed: {setup.Status}");

            var sessionId = ExtractSessionId(setup.Headers);
            var rtpChannel = ExtractInterleavedRtpChannel(setup.Headers);

            // PLAY starts the backchannel.
            var play = await session.RequestAsync("PLAY", baseUrl, ++cseq, auth,
                new[] { ("Session", sessionId), ("Require", BackchannelRequire) }, ct).ConfigureAwait(false);
            if (play.Status != 200)
                throw new InvalidOperationException($"PLAY failed: {play.Status}");

            session.Activate(rtpChannel, track.ALaw, track.SampleRate, sessionId, baseUrl, auth, cseq);
            _logger.LogInformation("Backchannel open: {Codec} {Rate}Hz, channel {Ch}",
                track.ALaw ? "PCMA" : "PCMU", track.SampleRate, rtpChannel);
            return session;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<bool> ProbeAsync(BackchannelEndpoint endpoint, CancellationToken ct)
    {
        var uri = endpoint.RtspUri;
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 554;
        var baseUrl = StripUserInfo(uri);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var session = new Session(tcp, tcp.GetStream(), _logger);
            var auth = new DigestState(endpoint.Credentials);
            var cseq = 0;

            await session.RequestAsync("OPTIONS", baseUrl, ++cseq, auth, null, ct).ConfigureAwait(false);
            var describe = await session.RequestAsync("DESCRIBE", baseUrl, ++cseq, auth,
                new[] { ("Accept", "application/sdp"), ("Require", BackchannelRequire) }, ct).ConfigureAwait(false);
            if (describe.Status != 200)
                throw new InvalidOperationException($"DESCRIBE failed: {describe.Status}");

            return SdpParser.FindBackchannelAudio(describe.Body) is not null;
        }
        finally
        {
            tcp.Dispose();
        }
    }

    private static string StripUserInfo(Uri uri)
    {
        var b = new UriBuilder(uri) { UserName = "", Password = "" };
        return b.Uri.ToString();
    }

    private static string ResolveControl(string baseUrl, string control)
    {
        if (string.IsNullOrEmpty(control) || control == "*") return baseUrl;
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return control;
        var b = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        return b + control.TrimStart('/');
    }

    private static string ExtractSessionId(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("session", out var raw)) return "";
        // "Session: 12345678;timeout=60" → "12345678"
        var semi = raw.IndexOf(';');
        return (semi >= 0 ? raw[..semi] : raw).Trim();
    }

    private static byte ExtractInterleavedRtpChannel(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("transport", out var t))
        {
            var idx = t.IndexOf("interleaved=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = t[(idx + "interleaved=".Length)..];
                var dash = rest.IndexOf('-');
                var first = dash >= 0 ? rest[..dash] : rest;
                var end = 0;
                while (end < first.Length && char.IsDigit(first[end])) end++;
                if (end > 0 && byte.TryParse(first[..end], out var ch)) return ch;
            }
        }
        return 0;
    }

    // --- The live session: interleaved RTP writer + keepalive + drain ---------
    private sealed class Session : IAudioBackchannelSession
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly byte[] _readBuf = new byte[4096];

        private byte _rtpChannel;
        private CancellationTokenSource? _bg;

        public bool ALaw { get; private set; }
        public int SampleRate { get; private set; } = 8000;

        public Session(TcpClient tcp, NetworkStream stream, ILogger logger)
        {
            _tcp = tcp;
            _stream = stream;
            _logger = logger;
        }

        public async Task<RtspResponse> RequestAsync(string method, string url, int cseq, DigestState auth,
            (string, string)[]? extraHeaders, CancellationToken ct)
        {
            var first = await SendRequestAsync(method, url, cseq, auth, extraHeaders, ct).ConfigureAwait(false);
            if (first.Status != 401 || !auth.CanAuthenticate) return first;

            // One retry with credentials after the challenge.
            auth.Challenge(first.Headers);
            return await SendRequestAsync(method, url, cseq, auth, extraHeaders, ct).ConfigureAwait(false);
        }

        private async Task<RtspResponse> SendRequestAsync(string method, string url, int cseq, DigestState auth,
            (string, string)[]? extraHeaders, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n");
            sb.Append("CSeq: ").Append(cseq).Append("\r\n");
            sb.Append("User-Agent: OpenIPC.Viewer\r\n");
            var authHeader = auth.BuildHeader(method, url);
            if (authHeader is not null) sb.Append("Authorization: ").Append(authHeader).Append("\r\n");
            if (extraHeaders is not null)
                foreach (var (k, v) in extraHeaders)
                    sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("\r\n");

            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }

            return await ReadResponseAsync(ct).ConfigureAwait(false);
        }

        private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
        {
            var headerText = await ReadUntilAsync("\r\n\r\n", ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var status = 0;
            if (lines.Length > 0)
            {
                // "RTSP/1.0 200 OK"
                var parts = lines[0].Split(' ', 3);
                if (parts.Length >= 2) int.TryParse(parts[1], out status);
            }
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            var body = "";
            if (headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var cl) && cl > 0)
                body = await ReadExactAsync(cl, ct).ConfigureAwait(false);

            return new RtspResponse(status, headers, body);
        }

        private readonly List<byte> _accum = new();

        private async Task<string> ReadUntilAsync(string terminator, CancellationToken ct)
        {
            var termBytes = Encoding.ASCII.GetBytes(terminator);
            while (true)
            {
                var idx = IndexOf(_accum, termBytes);
                if (idx >= 0)
                {
                    var headerLen = idx + termBytes.Length;
                    var text = Encoding.ASCII.GetString(_accum.GetRange(0, headerLen).ToArray());
                    _accum.RemoveRange(0, headerLen);
                    return text;
                }
                var n = await _stream.ReadAsync(_readBuf, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("RTSP connection closed");
                for (var i = 0; i < n; i++) _accum.Add(_readBuf[i]);
            }
        }

        private async Task<string> ReadExactAsync(int count, CancellationToken ct)
        {
            while (_accum.Count < count)
            {
                var n = await _stream.ReadAsync(_readBuf, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("RTSP connection closed");
                for (var i = 0; i < n; i++) _accum.Add(_readBuf[i]);
            }
            var text = Encoding.ASCII.GetString(_accum.GetRange(0, count).ToArray());
            _accum.RemoveRange(0, count);
            return text;
        }

        private static int IndexOf(List<byte> haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Count - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        // Switch to streaming mode: keepalive + drain incoming so the socket
        // buffer never stalls our writes.
        public void Activate(byte rtpChannel, bool aLaw, int sampleRate, string sessionId, string baseUrl, DigestState auth, int cseq)
        {
            _rtpChannel = rtpChannel;
            ALaw = aLaw;
            SampleRate = sampleRate;
            _bg = new CancellationTokenSource();
            _ = DrainAsync(_bg.Token);
            _ = KeepAliveAsync(sessionId, baseUrl, auth, cseq, _bg.Token);
        }

        public async Task SendRtpAsync(byte[] rtpPacket, CancellationToken ct)
        {
            // Interleaved frame: '$' | channel | 2-byte length | RTP.
            var frame = new byte[4 + rtpPacket.Length];
            frame[0] = 0x24;
            frame[1] = _rtpChannel;
            frame[2] = (byte)(rtpPacket.Length >> 8);
            frame[3] = (byte)rtpPacket.Length;
            Buffer.BlockCopy(rtpPacket, 0, frame, 4, rtpPacket.Length);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await _stream.WriteAsync(frame, ct).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            var buf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await _stream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0) break; // camera closed
                }
            }
            catch (Exception) { /* shutting down */ }
        }

        private async Task KeepAliveAsync(string sessionId, string baseUrl, DigestState auth, int cseq, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                    var sb = new StringBuilder();
                    sb.Append("GET_PARAMETER ").Append(baseUrl).Append(" RTSP/1.0\r\n");
                    sb.Append("CSeq: ").Append(++cseq).Append("\r\n");
                    sb.Append("Session: ").Append(sessionId).Append("\r\n");
                    var authHeader = auth.BuildHeader("GET_PARAMETER", baseUrl);
                    if (authHeader is not null) sb.Append("Authorization: ").Append(authHeader).Append("\r\n");
                    sb.Append("\r\n");
                    var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); }
                    finally { _writeLock.Release(); }
                    // Response is consumed by the drain loop.
                }
            }
            catch (Exception) { /* shutting down */ }
        }

        public async ValueTask DisposeAsync()
        {
            _bg?.Cancel();
            try { await Task.Delay(50).ConfigureAwait(false); } catch { }
            _bg?.Dispose();
            _writeLock.Dispose();
            _stream.Dispose();
            _tcp.Dispose();
        }
    }

    private readonly record struct RtspResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);
}
