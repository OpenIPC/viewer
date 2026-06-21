using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// macOS audio playback via AudioQueue (Phase 17.2), the output counterpart to
// CoreAudioInput. The output callback fills each recycled buffer from the ring
// (silence when starved) and re-enqueues, so the queue clock keeps running.
[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutput : IAudioOutput
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagsSignedIntPacked = 0x4 | 0x8;
    private const int FramesPerChunk = 480; // 10 ms @ 48 kHz
    private const int BufferCount = 4;

    private readonly ILogger<CoreAudioOutput> _logger;
    private readonly object _gate = new();
    private readonly AudioQueueOutputCallback _callback;

    private IntPtr _queue;
    private GCHandle _self;
    private PcmRing? _ring;
    private int _channels;
    private int _sampleRate;
    private int _bufferBytes;
    private bool _available = true;

    public CoreAudioOutput(ILogger<CoreAudioOutput> logger)
    {
        _logger = logger;
        _callback = OnOutput;
    }

    public bool IsAvailable => _available;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_queue != IntPtr.Zero && sampleRate == _sampleRate && channels == _channels)
                return;
            StopLocked();
            try
            {
                var fmt = new AudioStreamBasicDescription
                {
                    mSampleRate = sampleRate,
                    mFormatID = FormatLinearPcm,
                    mFormatFlags = FlagsSignedIntPacked,
                    mBytesPerPacket = (uint)(2 * channels),
                    mFramesPerPacket = 1,
                    mBytesPerFrame = (uint)(2 * channels),
                    mChannelsPerFrame = (uint)channels,
                    mBitsPerChannel = 16,
                    mReserved = 0,
                };

                _channels = channels;
                _sampleRate = sampleRate;
                _bufferBytes = FramesPerChunk * channels * 2;
                _ring = new PcmRing(sampleRate * channels * 2 / 2); // ~0.5 s

                _self = GCHandle.Alloc(this);
                var rc = AudioQueueNewOutput(ref fmt, _callback, GCHandle.ToIntPtr(_self),
                    IntPtr.Zero, IntPtr.Zero, 0, out _queue);
                if (rc != 0) throw new InvalidOperationException($"AudioQueueNewOutput failed: {rc}");

                // Prime the buffers with silence, then start.
                for (var i = 0; i < BufferCount; i++)
                {
                    rc = AudioQueueAllocateBuffer(_queue, (uint)_bufferBytes, out var buf);
                    if (rc != 0) throw new InvalidOperationException($"AudioQueueAllocateBuffer failed: {rc}");
                    FillBuffer(buf);
                }

                rc = AudioQueueStart(_queue, IntPtr.Zero);
                if (rc != 0) throw new InvalidOperationException($"AudioQueueStart failed: {rc}");

                _available = true;
                _logger.LogInformation("CoreAudio playback started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CoreAudio playback init failed; audio output unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        var ring = _ring;
        if (ring is null || pcm16.IsEmpty) return;
        ring.Write(pcm16);
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_queue != IntPtr.Zero)
        {
            try { AudioQueueStop(_queue, true); } catch { }
            try { AudioQueueDispose(_queue, true); } catch { }
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        _ring = null;
        _channels = 0;
        _sampleRate = 0;
    }

    private void OnOutput(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            FillBuffer(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CoreAudio output callback failed");
        }
    }

    private void FillBuffer(IntPtr buffer)
    {
        var buf = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
        var cap = (int)buf.mAudioDataBytesCapacity;
        var got = _ring?.Read(buf.mAudioData, cap) ?? 0;
        if (got < cap)
        {
            var silence = new byte[cap - got];
            Marshal.Copy(silence, 0, buf.mAudioData + got, cap - got);
        }
        buf.mAudioDataByteSize = (uint)cap;
        Marshal.StructureToPtr(buf, buffer, false);
        AudioQueueEnqueueBuffer(queue: _queue, inBuffer: buffer, 0, IntPtr.Zero);
    }

    public void Dispose() => Stop();

    private delegate void AudioQueueOutputCallback(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint mAudioDataByteSize;
        public IntPtr mUserData;
        public uint mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public int mPacketDescriptionCount;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(ref AudioStreamBasicDescription inFormat,
        AudioQueueOutputCallback inCallbackProc, IntPtr inUserData, IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode, uint inFlags, out IntPtr outAQ);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr inAQ, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr inAQ, IntPtr inStartTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);
}
