using System;
using System.Buffers;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Pipeline;

internal sealed class FfmpegVideoSession : IVideoSession
{
    private readonly VideoSessionOptions _options;
    private readonly IHwDecoderFactory? _hwFactory;
    private readonly ILogger<FfmpegVideoSession> _logger;

    private readonly Subject<VideoFrame> _frames = new();
    private readonly Subject<AudioFrame> _audioFrames = new();
    private readonly Subject<SessionState> _stateChanged = new();
    private readonly Subject<SessionTelemetry> _telemetry = new();

    // Audio output is normalized to this format for every camera (Phase 17.1):
    // signed-16 interleaved, stereo, 48 kHz. swresample does the heavy lifting;
    // the native sink (WASAPI etc.) only converts to the device mix format.
    private const int AudioOutSampleRate = 48000;
    private const int AudioOutChannels = 2;

    private readonly object _stateLock = new();
    private readonly object _snapshotLock = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private SessionState _state = SessionState.Idle;
    private string? _lastError;

    // Smart Pause gate (Phase 12.1). Signaled = decoding; reset = the Run loop
    // parks before av_read_frame so a hidden tile burns no CPU. The native
    // context stays alive for an instant resume.
    private readonly ManualResetEventSlim _decodeGate = new(true);
    private volatile bool _paused;

    // Audio decode toggle (Phase 17). Read by the decode loop, which lazily sets
    // up / tears down the audio decoder when this flips. Initialized from options
    // so the single-camera page (EnableAudio=true) starts with audio ready.
    private volatile bool _audioEnabled;

    private int _framesDecoded;
    private DateTime _lastFpsTick;
    private int _framesSinceFpsTick;
    private long _bytesSinceFpsTick;
    private string? _codecName;
    private int _width;
    private int _height;

    // Held for the lifetime of the codec context to keep the unmanaged
    // function pointer alive — FFmpeg invokes it via the AVCodecContext.
    private AVCodecContext_get_format? _getFormatDelegate;
    private AVPixelFormat _selectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;

    // Snapshot of the most recently decoded frame, kept around for SnapshotAsync.
    private byte[]? _snapshotBgra;
    private int _snapshotWidth;
    private int _snapshotHeight;
    private int _snapshotStride;

    public FfmpegVideoSession(VideoSessionOptions options, IHwDecoderFactory? hwFactory, ILogger<FfmpegVideoSession> logger)
    {
        _options = options;
        _hwFactory = hwFactory;
        _logger = logger;
        _audioEnabled = options.EnableAudio;
    }

    public void SetAudioEnabled(bool enabled) => _audioEnabled = enabled;

    public SessionState State
    {
        get { lock (_stateLock) return _state; }
    }

    public string? LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public IObservable<VideoFrame> Frames => _frames;
    public IObservable<AudioFrame> AudioFrames => _audioFrames;
    public IObservable<SessionState> StateChanged => _stateChanged;
    public IObservable<SessionTelemetry> Telemetry => _telemetry;

    public Task StartAsync(CancellationToken ct)
    {
        if (_thread is not null)
            throw new InvalidOperationException("Session already started");

        FfmpegRuntime.EnsureInitialized();
        SetState(SessionState.Connecting);

        _cts = new CancellationTokenSource();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"rtsp-{_options.RtspUri.Host}",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct)
    {
        byte[]? bgra;
        int w, h, stride;
        lock (_snapshotLock)
        {
            // No frame decoded yet (still connecting). Return empty rather than
            // throw — callers poll this while a fresh stream warms up, and an
            // exception per poll floods the debugger and is pure control flow.
            if (_snapshotBgra is null)
                return Task.FromResult(Array.Empty<byte>());
            bgra = (byte[])_snapshotBgra.Clone();
            w = _snapshotWidth;
            h = _snapshotHeight;
            stride = _snapshotStride;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), stride * h);

        using var image = SKImage.FromBitmap(bitmap);
        var skFormat = format == SnapshotFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var data = image.Encode(skFormat, quality: 92);
        return Task.FromResult(data.ToArray());
    }

    public void PauseDecode()
    {
        if (_thread is null || _paused) return;
        _paused = true;
        _decodeGate.Reset();
        SetState(SessionState.Paused);
    }

    public void Resume()
    {
        if (_thread is null || !_paused) return;
        _paused = false;
        _decodeGate.Set();
        SetState(SessionState.Playing);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        // Unblock the decode gate so a paused thread observes cancellation and
        // exits instead of parking forever.
        _decodeGate.Set();
        if (_thread is { IsAlive: true })
        {
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        _frames.OnCompleted();
        _audioFrames.OnCompleted();
        _stateChanged.OnCompleted();
        _telemetry.OnCompleted();
        _cts?.Dispose();
        _decodeGate.Dispose();
    }

    private unsafe void Run()
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVFrame* swFrame = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        AVDictionary* opts = null;
        AVBufferRef* hwDeviceCtx = null;
        AVCodecContext* audioCtx = null;
        AVFrame* audioFrame = null;
        SwrContext* swr = null;
        var videoStreamIndex = -1;
        var audioStreamIndex = -1;
        var audioProbedNoTrack = false;
        var swsSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        var hwActive = false;

        try
        {
            BuildOpts(&opts);
            fmtCtx = ffmpeg.avformat_alloc_context();
            var url = BuildUrlWithCredentials(_options.RtspUri, _options.Credentials);

            var ret = ffmpeg.avformat_open_input(&fmtCtx, url, null, &opts);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream in input");

            var codecpar = fmtCtx->streams[videoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"No decoder available for codec id {codecpar->codec_id}");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ret = ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar);
            FfmpegError.ThrowIfError(ret, "avcodec_parameters_to_context");

            // HW accel decision: pure policy in Resolve, native wiring inline.
            // Any failure here logs and continues with software decode.
            var resolved = HwAccelSelector.Resolve(_options.HwAccel, _hwFactory, _logger);
            if (resolved != HwAccelHint.None)
                hwActive = TryEnableHw(codecCtx, resolved, &hwDeviceCtx);

            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0 && hwActive)
            {
                // HW open failed: tear down HW state and retry as software.
                _logger.LogWarning("HW-accelerated avcodec_open2 failed: {Err}; retrying software", FfmpegError.Describe(ret));
                TearDownHw(codecCtx, &hwDeviceCtx);
                hwActive = false;
                ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            }
            FfmpegError.ThrowIfError(ret, "avcodec_open2");

            _width = codecCtx->width;
            _height = codecCtx->height;
            var baseName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
            _codecName = hwActive ? $"{baseName} ({resolved})" : baseName;
            if (hwActive)
                _logger.LogInformation("HW decode active: {Codec} via {Hint}", baseName, resolved);

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (hwActive) swFrame = ffmpeg.av_frame_alloc();

            SetState(SessionState.Playing);
            _lastFpsTick = DateTime.UtcNow;

            var ct = _cts!.Token;
            while (!ct.IsCancellationRequested)
            {
                // Smart Pause: park here (no av_read_frame, no decode) while the
                // tile is hidden. Cancellation during pause throws and unwinds to
                // the clean Idle exit below.
                if (_paused)
                {
                    try { _decodeGate.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                }

                // Lazily spin the audio decoder up/down to track SetAudioEnabled.
                // Done on the decode thread only, so audioCtx/swr are never raced.
                // A camera with no audio track is probed once and left alone.
                if (_audioEnabled && audioStreamIndex < 0 && !audioProbedNoTrack)
                {
                    audioStreamIndex = SetupAudio(fmtCtx, &audioCtx, &swr, &audioFrame);
                    if (audioStreamIndex < 0)
                    {
                        audioProbedNoTrack = true;
                        _logger.LogInformation("No audio track for {Host}", _options.RtspUri.Host);
                    }
                }
                else if (!_audioEnabled && audioStreamIndex >= 0)
                {
                    if (swr != null) { var p = swr; ffmpeg.swr_free(&p); swr = null; }
                    if (audioFrame != null) { var p = audioFrame; ffmpeg.av_frame_free(&p); audioFrame = null; }
                    if (audioCtx != null) { var p = audioCtx; ffmpeg.avcodec_free_context(&p); audioCtx = null; }
                    audioStreamIndex = -1;
                    audioProbedNoTrack = false; // a later re-enable should retry setup
                }

                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        _logger.LogInformation("RTSP stream EOF");
                        break;
                    }
                    _logger.LogWarning("av_read_frame failed: {Err}", FfmpegError.Describe(ret));
                    break;
                }

                if (packet->stream_index == audioStreamIndex)
                {
                    DecodeAudio(audioCtx, swr, audioFrame, packet, ct);
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                // Demux-level byte count → video bitrate in MaybePublishTelemetry.
                Interlocked.Add(ref _bytesSinceFpsTick, packet->size);

                ret = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    _logger.LogWarning("avcodec_send_packet failed: {Err}", FfmpegError.Describe(ret));
                    continue;
                }

                while (!ct.IsCancellationRequested)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    if (ret < 0)
                    {
                        _logger.LogWarning("avcodec_receive_frame failed: {Err}", FfmpegError.Describe(ret));
                        break;
                    }

                    AVFrame* presentable = frame;
                    if (hwActive && frame->hw_frames_ctx != null)
                    {
                        var transferRet = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                        if (transferRet < 0)
                        {
                            _logger.LogWarning("av_hwframe_transfer_data failed: {Err}", FfmpegError.Describe(transferRet));
                            ffmpeg.av_frame_unref(frame);
                            continue;
                        }
                        presentable = swFrame;
                    }

                    // Lazy sws init — the actual sw pixfmt is only known once
                    // the first frame arrives (matters for HW path where the
                    // codecCtx->pix_fmt is the HW pixfmt, not the sw one).
                    var framePixFmt = (AVPixelFormat)presentable->format;
                    if (sws == null || framePixFmt != swsSrcPixFmt)
                    {
                        if (sws != null) ffmpeg.sws_freeContext(sws);
                        sws = ffmpeg.sws_getContext(
                            _width, _height, framePixFmt,
                            _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            ffmpeg.SWS_BILINEAR, null, null, null);
                        if (sws == null)
                            throw new InvalidOperationException($"sws_getContext returned null for {framePixFmt}");
                        swsSrcPixFmt = framePixFmt;
                    }

                    EmitFrame(sws, presentable);
                    ffmpeg.av_frame_unref(frame);
                    if (presentable == swFrame) ffmpeg.av_frame_unref(swFrame);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video session loop failed");
            SetState(SessionState.Failed, ex.Message);
            return;
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (swr != null) { var p = swr; ffmpeg.swr_free(&p); }
            if (frame != null) { var p = frame; ffmpeg.av_frame_free(&p); }
            if (swFrame != null) { var p = swFrame; ffmpeg.av_frame_free(&p); }
            if (audioFrame != null) { var p = audioFrame; ffmpeg.av_frame_free(&p); }
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var p = codecCtx; ffmpeg.avcodec_free_context(&p); }
            if (audioCtx != null) { var p = audioCtx; ffmpeg.avcodec_free_context(&p); }
            if (hwDeviceCtx != null) { var p = hwDeviceCtx; ffmpeg.av_buffer_unref(&p); }
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
            if (opts != null) ffmpeg.av_dict_free(&opts);
        }

        SetState(SessionState.Idle);
    }

    private unsafe bool TryEnableHw(AVCodecContext* ctx, HwAccelHint hint, AVBufferRef** outDeviceCtx)
    {
        var (deviceType, hwPixFmt) = HwAccelSelector.MapToFfmpeg(hint);
        AVBufferRef* device = null;
        var ret = ffmpeg.av_hwdevice_ctx_create(&device, deviceType, null, null, 0);
        if (ret < 0)
        {
            _logger.LogWarning("av_hwdevice_ctx_create({Type}) failed: {Err}", deviceType, FfmpegError.Describe(ret));
            return false;
        }

        ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
        *outDeviceCtx = device;

        // Closure over hwPixFmt — keep the delegate rooted via instance field
        // so the unmanaged function pointer remains valid for the codec's life.
        _selectedHwPixFmt = hwPixFmt;
        _getFormatDelegate = GetFormatCallback;
        ctx->get_format = new AVCodecContext_get_format_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatDelegate),
        };
        return true;
    }

    private unsafe AVPixelFormat GetFormatCallback(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        // First pass: pick the HW pixfmt if the decoder offers it.
        for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == _selectedHwPixFmt) return *p;

        // Fallback: decoder didn't offer the HW format (typical on Android
        // emulators — no MediaCodec passthrough). Return the first offered
        // (software) format. Returning AV_PIX_FMT_NONE makes the decoder
        // reject every subsequent packet with AVERROR_INVALIDDATA.
        var fallback = *fmts;
        _logger.LogWarning("get_format: HW pixfmt {Fmt} not offered; falling back to software {Sw}",
            _selectedHwPixFmt, fallback);
        return fallback;
    }

    private static unsafe void TearDownHw(AVCodecContext* ctx, AVBufferRef** deviceCtx)
    {
        if (ctx->hw_device_ctx != null)
        {
            var p = ctx->hw_device_ctx;
            ffmpeg.av_buffer_unref(&p);
            ctx->hw_device_ctx = null;
        }
        ctx->get_format = default;
        if (*deviceCtx != null)
        {
            ffmpeg.av_buffer_unref(deviceCtx);
        }
    }

    private unsafe void EmitFrame(SwsContext* sws, AVFrame* frame)
    {
        var stride = _width * 4;
        var bufSize = stride * _height;
        var bgra = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            fixed (byte* dst = bgra)
            {
                var dstData = new byte_ptr4 { [0] = dst };
                var dstLinesize = new int4 { [0] = stride };
                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, _height, dstData, dstLinesize);
            }

            UpdateSnapshotBuffer(bgra, stride);

            var vf = new VideoFrame(bgra, _width, _height, stride, frame->pts, DateTime.UtcNow);

            // Synchronous delivery: subscribers must finish (incl. UI Marshal.Copy) before
            // OnNext returns. After that we return the buffer to the pool. The natural
            // backpressure is that a slow subscriber blocks the decoder thread —
            // intentional per architecture §6.3 (drop, don't buffer).
            try
            {
                _frames.OnNext(vf);
                Interlocked.Increment(ref _framesDecoded);
                Interlocked.Increment(ref _framesSinceFpsTick);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw in frame OnNext");
            }

            MaybePublishTelemetry();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bgra);
        }
    }

    // Finds the first audio stream, opens its decoder and a swresample context
    // that normalizes to S16/48 kHz/stereo. Returns the stream index, or -1 if
    // there is no audio or setup fails (caller treats both as "no audio").
    private unsafe int SetupAudio(AVFormatContext* fmtCtx, AVCodecContext** outCtx, SwrContext** outSwr, AVFrame** outFrame)
    {
        var idx = -1;
        for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
        {
            if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return -1;

        var codecpar = fmtCtx->streams[idx]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            _logger.LogWarning("No audio decoder for codec id {Id}", codecpar->codec_id);
            return -1;
        }

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        var ret = ffmpeg.avcodec_parameters_to_context(ctx, codecpar);
        if (ret < 0)
        {
            _logger.LogWarning("audio avcodec_parameters_to_context failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        ret = ffmpeg.avcodec_open2(ctx, codec, null);
        if (ret < 0)
        {
            _logger.LogWarning("audio avcodec_open2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        // Input layout: trust the decoder's if it knows it, else assume mono.
        AVChannelLayout defaultIn = default;
        AVChannelLayout* inLayout;
        if (ctx->ch_layout.nb_channels > 0)
        {
            inLayout = &ctx->ch_layout;
        }
        else
        {
            ffmpeg.av_channel_layout_default(&defaultIn, 1);
            inLayout = &defaultIn;
        }

        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, AudioOutChannels);

        SwrContext* swr = null;
        ret = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioOutSampleRate,
            inLayout, ctx->sample_fmt, ctx->sample_rate,
            0, null);
        if (ret < 0 || swr == null)
        {
            _logger.LogWarning("swr_alloc_set_opts2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.av_channel_layout_uninit(&outLayout);
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        ret = ffmpeg.swr_init(swr);
        ffmpeg.av_channel_layout_uninit(&outLayout);
        if (ret < 0)
        {
            _logger.LogWarning("swr_init failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.swr_free(&swr);
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        var baseName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
        _logger.LogInformation("Audio decode active: {Codec} {Rate}Hz {Ch}ch → 48kHz stereo S16",
            baseName, ctx->sample_rate, ctx->ch_layout.nb_channels);

        *outCtx = ctx;
        *outSwr = swr;
        *outFrame = ffmpeg.av_frame_alloc();
        return idx;
    }

    private unsafe void DecodeAudio(AVCodecContext* ctx, SwrContext* swr, AVFrame* frame, AVPacket* packet, CancellationToken ct)
    {
        if (ctx == null || swr == null || frame == null) return;

        var ret = ffmpeg.avcodec_send_packet(ctx, packet);
        if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            _logger.LogDebug("audio send_packet failed: {Err}", FfmpegError.Describe(ret));
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            ret = ffmpeg.avcodec_receive_frame(ctx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                _logger.LogDebug("audio receive_frame failed: {Err}", FfmpegError.Describe(ret));
                break;
            }

            EmitAudioFrame(swr, frame, ctx->sample_rate);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private unsafe void EmitAudioFrame(SwrContext* swr, AVFrame* frame, int inRate)
    {
        if (inRate <= 0) return;

        // Worst-case output sample count: queued resampler delay + this frame,
        // rescaled to the output rate.
        var delay = ffmpeg.swr_get_delay(swr, inRate);
        var maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, AudioOutSampleRate, inRate, AVRounding.AV_ROUND_UP);
        if (maxOut <= 0) return;

        var pcm = new byte[maxOut * AudioOutChannels * 2]; // S16 = 2 bytes/sample
        int produced;
        fixed (byte* dst = pcm)
        {
            var outPlane = dst;
            produced = ffmpeg.swr_convert(swr, &outPlane, maxOut, frame->extended_data, frame->nb_samples);
        }
        if (produced <= 0) return;

        var bytes = produced * AudioOutChannels * 2;
        var payload = pcm;
        if (bytes != pcm.Length)
        {
            payload = new byte[bytes];
            Buffer.BlockCopy(pcm, 0, payload, 0, bytes);
        }

        var af = new AudioFrame(payload, AudioOutSampleRate, AudioOutChannels, 0);
        try
        {
            _audioFrames.OnNext(af);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscriber threw in audio OnNext");
        }
    }

    private void UpdateSnapshotBuffer(byte[] sourceBgra, int stride)
    {
        var size = stride * _height;
        lock (_snapshotLock)
        {
            if (_snapshotBgra is null || _snapshotBgra.Length < size)
                _snapshotBgra = new byte[size];
            Buffer.BlockCopy(sourceBgra, 0, _snapshotBgra, 0, size);
            _snapshotWidth = _width;
            _snapshotHeight = _height;
            _snapshotStride = stride;
        }
    }

    private void MaybePublishTelemetry()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastFpsTick;
        if (elapsed.TotalSeconds < 1)
            return;

        var sinceLast = Interlocked.Exchange(ref _framesSinceFpsTick, 0);
        var fps = sinceLast / elapsed.TotalSeconds;
        var bytes = Interlocked.Exchange(ref _bytesSinceFpsTick, 0);
        var bitrateKbps = bytes * 8.0 / 1000.0 / elapsed.TotalSeconds;
        _lastFpsTick = now;

        _telemetry.OnNext(new SessionTelemetry(
            FramesDecoded: _framesDecoded,
            FramesDropped: 0, // synchronous delivery — no internal drops yet; Phase 3 grid adds backpressure counters
            Fps: fps,
            AverageLatency: TimeSpan.Zero,
            Codec: _codecName,
            Width: _width,
            Height: _height,
            CapturedAt: now,
            BitrateKbps: bitrateKbps));
    }

    private void SetState(SessionState newState, string? error = null)
    {
        lock (_stateLock)
        {
            _state = newState;
            _lastError = error;
        }
        _stateChanged.OnNext(newState);
    }

    private unsafe void BuildOpts(AVDictionary** opts)
    {
        var transport = _options.Transport switch
        {
            RtspTransport.Tcp => "tcp",
            RtspTransport.Udp => "udp",
            _ => "tcp",
        };
        ffmpeg.av_dict_set(opts, "rtsp_transport", transport, 0);
        ffmpeg.av_dict_set(opts, "stimeout", "5000000", 0);          // 5s socket timeout (µs)
        ffmpeg.av_dict_set(opts, "max_delay", "200000", 0);          // 200ms reorder window
        ffmpeg.av_dict_set(opts, "buffer_size", "1048576", 0);
        ffmpeg.av_dict_set(opts, "reorder_queue_size", "0", 0);
        ffmpeg.av_dict_set(opts, "fflags", "nobuffer", 0);
    }

    private static string BuildUrlWithCredentials(Uri uri, CameraCredentials? creds)
    {
        if (creds is null || !string.IsNullOrEmpty(uri.UserInfo))
            return uri.ToString();

        var builder = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString(creds.Username),
            Password = Uri.EscapeDataString(creds.Password),
        };
        return builder.Uri.ToString();
    }
}
