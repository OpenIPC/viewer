using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// Windows audio renderer via WASAPI shared mode (Phase 17.2). Not the legacy
// waveOut — this is the modern endpoint API. We open the stream with
// AUTOCONVERTPCM so the audio engine resamples our fixed 48 kHz/stereo/S16 feed
// to whatever the device mix format is; that keeps the FFmpeg side simple and
// uniform across cameras.
//
// Threading: Write() enqueues into a small ring buffer (drop-oldest on overflow
// — for a live monitor, low latency beats gap-free playback). A dedicated render
// thread polls the endpoint and tops up its buffer from the ring, writing
// silence when starved so the device clock keeps ticking.
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : IAudioOutput
{
    private const int ShareModeShared = 0;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const int ClsCtxAll = 0x17;
    private const ushort WaveFormatPcm = 1;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration100Ns = 2_000_000; // 200 ms

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly ILogger<WasapiAudioOutput> _logger;
    private readonly object _gate = new();

    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private ByteRing? _ring;
    private uint _bufferFrames;
    private int _blockAlign;
    private int _sampleRate;
    private int _channels;
    private bool _available = true;

    public WasapiAudioOutput(ILogger<WasapiAudioOutput> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => _available;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_client is not null && sampleRate == _sampleRate && channels == _channels)
                return; // already running this format

            StopLocked();
            try
            {
                InitLocked(sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WASAPI init failed; audio output unavailable");
                _available = false;
                StopLocked();
            }
        }
    }

    private void InitLocked(int sampleRate, int channels)
    {
        var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
            ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID unavailable");
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;
        try
        {
            // eRender = 0, eConsole = 0.
            Check(enumerator.GetDefaultAudioEndpoint(0, 0, out var device), "GetDefaultAudioEndpoint");
            try
            {
                var iid = IID_IAudioClient;
                Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var clientObj), "Activate(IAudioClient)");
                var client = (IAudioClient)clientObj;

                _blockAlign = channels * 2; // S16
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = WaveFormatPcm,
                    nChannels = (ushort)channels,
                    nSamplesPerSec = (uint)sampleRate,
                    wBitsPerSample = 16,
                    nBlockAlign = (ushort)_blockAlign,
                    nAvgBytesPerSec = (uint)(sampleRate * _blockAlign),
                    cbSize = 0,
                };

                var pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
                try
                {
                    Marshal.StructureToPtr(fmt, pFmt, false);
                    Check(client.Initialize(
                        ShareModeShared,
                        StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                        BufferDuration100Ns, 0, pFmt, IntPtr.Zero), "IAudioClient.Initialize");
                }
                finally
                {
                    Marshal.FreeHGlobal(pFmt);
                }

                Check(client.GetBufferSize(out _bufferFrames), "GetBufferSize");
                var renderIid = IID_IAudioRenderClient;
                Check(client.GetService(ref renderIid, out var renderObj), "GetService(IAudioRenderClient)");

                _client = client;
                _render = (IAudioRenderClient)renderObj;
                _sampleRate = sampleRate;
                _channels = channels;
                _ring = new ByteRing(sampleRate * _blockAlign / 2); // ~0.5 s
                _available = true;

                Check(client.Start(), "IAudioClient.Start");

                _cts = new CancellationTokenSource();
                _renderThread = new Thread(() => RenderLoop(_cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "wasapi-render",
                };
                _renderThread.Start();

                _logger.LogInformation("WASAPI started: {Rate}Hz {Ch}ch, buffer {Frames} frames", sampleRate, channels, _bufferFrames);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
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
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        if (_renderThread is { IsAlive: true })
            _renderThread.Join(TimeSpan.FromMilliseconds(500));
        _renderThread = null;
        _cts?.Dispose();
        _cts = null;

        try { _client?.Stop(); } catch { /* tearing down */ }
        if (_render is not null) { Marshal.ReleaseComObject(_render); _render = null; }
        if (_client is not null) { Marshal.ReleaseComObject(_client); _client = null; }
        _ring = null;
        _sampleRate = 0;
        _channels = 0;
    }

    private void RenderLoop(CancellationToken ct)
    {
        var client = _client;
        var render = _render;
        var ring = _ring;
        if (client is null || render is null || ring is null) return;

        var blockAlign = _blockAlign;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (client.GetCurrentPadding(out var padding) < 0) break;
                var framesAvail = _bufferFrames - padding;
                if (framesAvail == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (render.GetBuffer(framesAvail, out var pData) < 0) break;

                var bytesWanted = (int)framesAvail * blockAlign;
                var pulled = ring.Read(pData, bytesWanted);
                uint flags = 0;
                if (pulled == 0)
                {
                    flags = BufferFlagsSilent;
                }
                else if (pulled < bytesWanted)
                {
                    // Zero-fill the tail so we never emit stale memory.
                    var silence = new byte[bytesWanted - pulled];
                    Marshal.Copy(silence, 0, pData + pulled, silence.Length);
                }

                render.ReleaseBuffer(framesAvail, flags);
                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WASAPI render loop hiccup");
                break;
            }
        }
    }

    public void Dispose() => Stop();

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    // Fixed-capacity circular byte buffer; drops oldest data on overflow.
    private sealed class ByteRing
    {
        private readonly byte[] _buf;
        private readonly object _lock = new();
        private int _head; // read position
        private int _count;

        public ByteRing(int capacity) => _buf = new byte[Math.Max(capacity, 4096)];

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                var cap = _buf.Length;
                var len = data.Length;
                if (len >= cap)
                {
                    // Keep only the freshest tail.
                    data = data[(len - cap)..];
                    len = cap;
                }

                // Drop oldest to make room.
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
                if (first < len)
                    data[first..].CopyTo(_buf.AsSpan(0));
                _count += len;
            }
        }

        // Copies up to maxBytes into the unmanaged destination; returns bytes copied.
        public int Read(IntPtr dest, int maxBytes)
        {
            lock (_lock)
            {
                var n = Math.Min(maxBytes, _count);
                if (n == 0) return 0;
                var first = Math.Min(n, _buf.Length - _head);
                Marshal.Copy(_buf, _head, dest, first);
                if (first < n)
                    Marshal.Copy(_buf, 0, dest + first, n - first);
                _head = (_head + n) % _buf.Length;
                _count -= n;
                return n;
            }
        }
    }
}

// --- COM interop -----------------------------------------------------------

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long phnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
    [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}
