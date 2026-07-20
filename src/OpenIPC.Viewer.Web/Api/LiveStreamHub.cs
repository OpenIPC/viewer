using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Web.Api;

// Fan-out for live video: one ffmpeg session per (camera, mode) shared by all
// viewers, instead of a process + RTSP connection each. A grid of the same
// camera, or several people watching, cost one remux.
//
// MSE needs the fMP4 init segment (ftyp+moov) before any media, and every media
// fragment (moof+mdat) must arrive whole. So the hub parses ffmpeg's output into
// boxes: it caches the init, then broadcasts complete fragments. A late joiner
// gets the cached init, then whole fragments from the next boundary — never a
// partial one.
public sealed class LiveStreamHub
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, CameraStream> _streams = new(StringComparer.Ordinal);

    public LiveStreamHub(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    // Number of distinct shared ffmpeg sessions currently running.
    public int ActiveStreamCount
    {
        get { lock (_gate) return _streams.Count; }
    }

    public LiveSubscription Subscribe(string cameraId, bool transcode, string rtspUrl)
    {
        lock (_gate)
        {
            var key = $"{cameraId}|{(transcode ? "t" : "c")}";
            if (!_streams.TryGetValue(key, out var stream))
            {
                stream = new CameraStream(
                    rtspUrl, transcode,
                    _loggerFactory.CreateLogger("OpenIPC.Web.Live"),
                    onEmpty: () => { lock (_gate) _streams.Remove(key); });
                _streams[key] = stream;
                stream.Start();
            }
            return stream.AddSubscriber();
        }
    }

    private sealed class CameraStream
    {
        private readonly string _rtspUrl;
        private readonly bool _transcode;
        private readonly ILogger _logger;
        private readonly Action _onEmpty;
        private readonly object _lock = new();
        private readonly HashSet<Channel<byte[]>> _subs = new();
        private byte[]? _init;
        private Process? _proc;
        private CancellationTokenSource? _cts;
        private bool _stopped;

        public CameraStream(string rtspUrl, bool transcode, ILogger logger, Action onEmpty)
        {
            _rtspUrl = rtspUrl;
            _transcode = transcode;
            _logger = logger;
            _onEmpty = onEmpty;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _proc = LiveFfmpeg.Start(_rtspUrl, _transcode);
            _ = LiveFfmpeg.DrainStderrAsync(_proc, _logger);
            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        public LiveSubscription AddSubscriber()
        {
            // Bounded + drop-oldest: a slow viewer drops fragments (brief gap) but
            // never stalls the shared reader or the other viewers.
            var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            lock (_lock)
            {
                _subs.Add(ch);
                if (_init is not null)
                    ch.Writer.TryWrite(_init);
            }
            return new LiveSubscription(ch.Reader, () => Remove(ch));
        }

        private void Remove(Channel<byte[]> ch)
        {
            bool empty;
            lock (_lock)
            {
                _subs.Remove(ch);
                ch.Writer.TryComplete();
                empty = _subs.Count == 0;
            }
            if (empty)
                Stop();
        }

        private void Stop()
        {
            lock (_lock)
            {
                if (_stopped) return;
                _stopped = true;
            }
            try { _cts?.Cancel(); } catch { /* best effort */ }
            LiveFfmpeg.Kill(_proc);
            _onEmpty();
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                var stdout = _proc!.StandardOutput.BaseStream;
                var initParts = new List<byte[]>();
                var initDone = false;
                MemoryStream? fragment = null;

                while (!ct.IsCancellationRequested)
                {
                    var box = await ReadBoxAsync(stdout, ct).ConfigureAwait(false);
                    if (box is null)
                        break;

                    if (!initDone)
                    {
                        if (box.Type == "moof")
                        {
                            PublishInit(initParts);
                            initDone = true;
                            fragment = new MemoryStream();
                            fragment.Write(box.Bytes);
                        }
                        else
                        {
                            initParts.Add(box.Bytes);
                        }
                    }
                    else if (box.Type == "moof")
                    {
                        fragment = new MemoryStream();
                        fragment.Write(box.Bytes);
                    }
                    else
                    {
                        fragment?.Write(box.Bytes);
                        if (box.Type == "mdat" && fragment is not null)
                        {
                            Broadcast(fragment.ToArray());
                            fragment = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "live read loop ended"); }
            finally { Stop(); }
        }

        private void PublishInit(List<byte[]> parts)
        {
            var total = 0;
            foreach (var p in parts) total += p.Length;
            var init = new byte[total];
            var offset = 0;
            foreach (var p in parts) { Array.Copy(p, 0, init, offset, p.Length); offset += p.Length; }

            lock (_lock)
            {
                _init = init;
                foreach (var s in _subs)
                    s.Writer.TryWrite(init);
            }
        }

        private void Broadcast(byte[] fragment)
        {
            lock (_lock)
            {
                foreach (var s in _subs)
                    s.Writer.TryWrite(fragment);
            }
        }

        private sealed record Box(string Type, byte[] Bytes);

        private static async Task<Box?> ReadBoxAsync(Stream stream, CancellationToken ct)
        {
            var header = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
            if (header is null)
                return null;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);

            if (size == 1)
            {
                var ext = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
                if (ext is null) return null;
                size = BinaryPrimitives.ReadInt64BigEndian(ext);
                if (size < 16 || size > int.MaxValue) return null;
                var body = await ReadExactAsync(stream, (int)size - 16, ct).ConfigureAwait(false);
                if (body is null) return null;
                var full = new byte[size];
                Array.Copy(header, 0, full, 0, 8);
                Array.Copy(ext, 0, full, 8, 8);
                Array.Copy(body, 0, full, 16, body.Length);
                return new Box(type, full);
            }

            if (size is < 8 or > int.MaxValue)
                return null;
            var rest = await ReadExactAsync(stream, (int)size - 8, ct).ConfigureAwait(false);
            if (rest is null) return null;
            var boxBytes = new byte[size];
            Array.Copy(header, 0, boxBytes, 0, 8);
            Array.Copy(rest, 0, boxBytes, 8, rest.Length);
            return new Box(type, boxBytes);
        }

        private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
                if (read == 0)
                    return null;
                offset += read;
            }
            return buffer;
        }
    }
}

// A single viewer's tap on a shared camera stream. Reader yields the init
// segment first, then whole fragments. Dispose to leave (the shared ffmpeg stops
// when the last viewer disposes).
public sealed class LiveSubscription : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    public LiveSubscription(ChannelReader<byte[]> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<byte[]> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _dispose();
    }
}
