using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// macOS microphone capture via AudioQueue (Phase 17.6). AudioQueue converts the
// hardware format down to the ASBD we request (8 kHz mono S16), so no manual
// resampling. Its own internal thread invokes the input callback as buffers fill;
// we copy each out and re-enqueue.
[SupportedOSPlatform("macos")]
public sealed class CoreAudioInput : IAudioInput
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagsSignedIntPacked = 0x4 | 0x8;
    private const int FramesPerChunk = 160; // 20 ms @ 8 kHz
    private const int BufferCount = 3;

    private readonly ILogger<CoreAudioInput> _logger;
    private readonly object _gate = new();
    // Rooted so the native function pointer stays valid for the queue's life.
    private readonly AudioQueueInputCallback _callback;

    private IntPtr _queue;
    private GCHandle _self;
    private int _channels;
    private bool _available = true;

    public CoreAudioInput(ILogger<CoreAudioInput> logger)
    {
        _logger = logger;
        _callback = OnInput;
    }

    public bool IsAvailable => _available;
    public event Action<byte[]>? FrameCaptured;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
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

                _self = GCHandle.Alloc(this);
                var rc = AudioQueueNewInput(ref fmt, _callback, GCHandle.ToIntPtr(_self),
                    IntPtr.Zero, IntPtr.Zero, 0, out _queue);
                if (rc != 0) throw new InvalidOperationException($"AudioQueueNewInput failed: {rc}");

                var byteSize = (uint)(FramesPerChunk * channels * 2);
                for (var i = 0; i < BufferCount; i++)
                {
                    rc = AudioQueueAllocateBuffer(_queue, byteSize, out var buf);
                    if (rc != 0) throw new InvalidOperationException($"AudioQueueAllocateBuffer failed: {rc}");
                    AudioQueueEnqueueBuffer(_queue, buf, 0, IntPtr.Zero);
                }

                rc = AudioQueueStart(_queue, IntPtr.Zero);
                if (rc != 0) throw new InvalidOperationException($"AudioQueueStart failed: {rc}");

                _channels = channels;
                _available = true;
                _logger.LogInformation("CoreAudio capture started: {Rate}Hz {Ch}ch", sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CoreAudio capture init failed; mic unavailable");
                _available = false;
                StopLocked();
            }
        }
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
        _channels = 0;
    }

    private void OnInput(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint numPackets, IntPtr packetDescs)
    {
        try
        {
            var buf = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
            var bytes = (int)buf.mAudioDataByteSize;
            if (bytes > 0 && buf.mAudioData != IntPtr.Zero)
            {
                var chunk = new byte[bytes];
                Marshal.Copy(buf.mAudioData, chunk, 0, bytes);
                FrameCaptured?.Invoke(chunk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CoreAudio input callback failed");
        }
        finally
        {
            // Recycle the buffer so capture continues.
            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }
    }

    public void Dispose() => Stop();

    private delegate void AudioQueueInputCallback(IntPtr inUserData, IntPtr inAQ, IntPtr inBuffer,
        IntPtr inStartTime, uint inNumberPacketDescriptions, IntPtr inPacketDescs);

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
    private static extern int AudioQueueNewInput(ref AudioStreamBasicDescription inFormat,
        AudioQueueInputCallback inCallbackProc, IntPtr inUserData, IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode, uint inFlags, out IntPtr outAQ);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr inAQ, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr inAQ, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr inAQ, IntPtr inStartTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr inAQ, [MarshalAs(UnmanagedType.I1)] bool inImmediate);
}
