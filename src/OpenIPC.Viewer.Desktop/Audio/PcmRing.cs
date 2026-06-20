using System;
using System.Runtime.InteropServices;

namespace OpenIPC.Viewer.Desktop.Audio;

// Fixed-capacity circular PCM buffer shared by the ALSA / CoreAudio renderers
// (Phase 17.2). Drop-oldest on overflow — for a live monitor low latency beats
// gap-free playback. Lock-based; Write from the decode thread, Read from the
// playback thread/callback.
internal sealed class PcmRing
{
    private readonly byte[] _buf;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public PcmRing(int capacity) => _buf = new byte[Math.Max(capacity, 4096)];

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var cap = _buf.Length;
            var len = data.Length;
            if (len >= cap)
            {
                data = data[(len - cap)..];
                len = cap;
            }
            var free = cap - _count;
            if (len > free)
            {
                var drop = len - free;
                _head = (_head + drop) % cap;
                _count -= drop;
            }
            var tail = (_head + _count) % cap;
            var first = Math.Min(len, cap - tail);
            data[..first].CopyTo(_buf.AsSpan(tail));
            if (first < len) data[first..].CopyTo(_buf.AsSpan(0));
            _count += len;
        }
    }

    // Drain up to maxBytes into a managed buffer; returns bytes copied.
    public int Read(byte[] dest, int maxBytes)
    {
        lock (_lock)
        {
            var n = Math.Min(maxBytes, _count);
            if (n == 0) return 0;
            var first = Math.Min(n, _buf.Length - _head);
            Buffer.BlockCopy(_buf, _head, dest, 0, first);
            if (first < n) Buffer.BlockCopy(_buf, 0, dest, first, n - first);
            _head = (_head + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    // Drain up to maxBytes into unmanaged memory; returns bytes copied.
    public int Read(IntPtr dest, int maxBytes)
    {
        lock (_lock)
        {
            var n = Math.Min(maxBytes, _count);
            if (n == 0) return 0;
            var first = Math.Min(n, _buf.Length - _head);
            Marshal.Copy(_buf, _head, dest, first);
            if (first < n) Marshal.Copy(_buf, 0, dest + first, n - first);
            _head = (_head + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }
}
