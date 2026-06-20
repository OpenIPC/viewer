using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop.Audio;

// Windows microphone capture via WASAPI shared mode (Phase 17.6), the input
// counterpart to WasapiAudioOutput. Opens with AUTOCONVERTPCM so the engine
// resamples the device mix format down to the requested 8 kHz mono S16 that the
// G.711 backchannel wants — no manual resampler needed. A capture thread drains
// completed packets and raises FrameCaptured.
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput
{
    private const int ShareModeShared = 0;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const int ClsCtxAll = 0x17;
    private const ushort WaveFormatPcm = 1;
    private const long BufferDuration100Ns = 2_000_000; // 200 ms

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly ILogger<WasapiAudioInput> _logger;
    private readonly object _gate = new();

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _blockAlign;
    private int _sampleRate;
    private int _channels;
    private bool _available = true;

    public WasapiAudioInput(ILogger<WasapiAudioInput> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => _available;

    public event Action<byte[]>? FrameCaptured;

    public void Start(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (_client is not null && sampleRate == _sampleRate && channels == _channels)
                return;

            StopLocked();
            try
            {
                InitLocked(sampleRate, channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WASAPI capture init failed; mic unavailable");
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
            // eCapture = 1, eConsole = 0.
            Check(enumerator.GetDefaultAudioEndpoint(1, 0, out var device), "GetDefaultAudioEndpoint(capture)");
            try
            {
                var iid = IID_IAudioClient;
                Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var clientObj), "Activate(IAudioClient)");
                var client = (IAudioClient)clientObj;

                _blockAlign = channels * 2;
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
                        BufferDuration100Ns, 0, pFmt, IntPtr.Zero), "IAudioClient.Initialize(capture)");
                }
                finally
                {
                    Marshal.FreeHGlobal(pFmt);
                }

                var captureIid = IID_IAudioCaptureClient;
                Check(client.GetService(ref captureIid, out var captureObj), "GetService(IAudioCaptureClient)");

                _client = client;
                _capture = (IAudioCaptureClient)captureObj;
                _sampleRate = sampleRate;
                _channels = channels;
                _available = true;

                Check(client.Start(), "IAudioClient.Start(capture)");

                _cts = new CancellationTokenSource();
                _thread = new Thread(() => CaptureLoop(_cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "wasapi-capture",
                };
                _thread.Start();

                _logger.LogInformation("WASAPI capture started: {Rate}Hz {Ch}ch", sampleRate, channels);
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
        if (_thread is { IsAlive: true })
            _thread.Join(TimeSpan.FromMilliseconds(500));
        _thread = null;
        _cts?.Dispose();
        _cts = null;

        try { _client?.Stop(); } catch { /* tearing down */ }
        if (_capture is not null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        if (_client is not null) { Marshal.ReleaseComObject(_client); _client = null; }
        _sampleRate = 0;
        _channels = 0;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var capture = _capture;
        if (capture is null) return;
        var blockAlign = _blockAlign;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (capture.GetNextPacketSize(out var packetFrames) < 0) break;
                if (packetFrames == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (capture.GetBuffer(out var pData, out var numFrames, out var flags, out _, out _) < 0) break;
                if (numFrames > 0)
                {
                    var bytes = (int)numFrames * blockAlign;
                    var buf = new byte[bytes];
                    // SILENT flag → the buffer holds no real data; emit zeros so the
                    // backchannel keeps a steady cadence.
                    if ((flags & 0x2) == 0)
                        Marshal.Copy(pData, buf, 0, bytes);
                    FrameCaptured?.Invoke(buf);
                }
                capture.ReleaseBuffer(numFrames);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WASAPI capture loop hiccup");
                break;
            }
        }
    }

    public void Dispose() => Stop();

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
}
