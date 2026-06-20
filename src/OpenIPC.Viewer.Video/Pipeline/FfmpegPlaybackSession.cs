using System;
using System.Buffers;
using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Pipeline;

// Plays a recorded file (Phase 16). Mirrors FfmpegVideoSession's decode/emit
// path but for local files: presentation is paced off the file's own PTS so it
// plays at real-time speed, and it adds transport (play/pause), a probed
// Duration, an observable Position, and keyframe Seek.
//
// Software decode only — a single local playback stream doesn't need the HW
// path the live grid relies on, and software decode keeps seeking simple
// (no hwframe transfer / get_format wiring). HW playback decode is a later add.
internal sealed class FfmpegPlaybackSession : IPlaybackSession
{
    private const long NoSeek = long.MinValue;

    private readonly PlaybackOptions _options;
    private readonly ILogger<FfmpegPlaybackSession> _logger;

    private readonly Subject<VideoFrame> _frames = new();
    private readonly Subject<SessionState> _stateChanged = new();
    private readonly Subject<SessionTelemetry> _telemetry = new();
    private readonly Subject<TimeSpan> _positionChanged = new();

    private readonly object _stateLock = new();
    private readonly object _snapshotLock = new();

    // Transport gate: signaled = playing, reset = paused. The Run loop parks on
    // it before reading the next packet, so a paused player burns no CPU while
    // holding its last frame.
    private readonly ManualResetEventSlim _playGate = new(false);
    private volatile bool _paused = true;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private SessionState _state = SessionState.Idle;
    private string? _lastError;

    // Latest-wins seek request in TimeSpan.Ticks, or NoSeek. The decode loop
    // drains it before each packet read; SeekAsync just overwrites it.
    private long _pendingSeekTicks = NoSeek;

    private long _durationTicks;
    private long _positionTicks;

    private string? _codecName;
    private int _width;
    private int _height;
    private int _framesDecoded;

    private byte[]? _snapshotBgra;
    private int _snapshotWidth;
    private int _snapshotHeight;
    private int _snapshotStride;

    public FfmpegPlaybackSession(PlaybackOptions options, ILogger<FfmpegPlaybackSession> logger)
    {
        _options = options;
        _logger = logger;
    }

    public SessionState State { get { lock (_stateLock) return _state; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }

    public IObservable<VideoFrame> Frames => _frames;
    // File playback has no live-audio monitoring path (Phase 17 is RTSP only);
    // expose an empty stream so the IVideoSession contract is satisfied.
    public IObservable<AudioFrame> AudioFrames { get; } = System.Reactive.Linq.Observable.Empty<AudioFrame>();
    public IObservable<SessionState> StateChanged => _stateChanged;
    public IObservable<SessionTelemetry> Telemetry => _telemetry;
    public IObservable<TimeSpan> PositionChanged => _positionChanged;

    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));
    public bool IsPaused => _paused;

    public Task StartAsync(CancellationToken ct)
    {
        if (_thread is not null)
            throw new InvalidOperationException("Session already started");

        FfmpegRuntime.EnsureInitialized();
        SetState(SessionState.Connecting);

        _paused = false;
        _playGate.Set();
        _cts = new CancellationTokenSource();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "playback",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public void Play()
    {
        if (_thread is null) return;
        _paused = false;
        _playGate.Set();
        SetState(SessionState.Playing);
    }

    public void Pause()
    {
        if (_thread is null) return;
        _paused = true;
        _playGate.Reset();
        SetState(SessionState.Paused);
    }

    // Smart Pause (Phase 12) maps onto transport pause for a file player.
    public void PauseDecode() => Pause();
    public void Resume() => Play();

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        if (_thread is null) return Task.CompletedTask;
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        var dur = Duration;
        if (dur > TimeSpan.Zero && clamped > dur) clamped = dur;
        // Latest-wins: overwrite any not-yet-processed request, then unpark the
        // loop so it acts on the seek even while paused (scrubbing shows frames).
        Interlocked.Exchange(ref _pendingSeekTicks, clamped.Ticks);
        _playGate.Set();
        return Task.CompletedTask;
    }

    public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct)
    {
        byte[]? bgra;
        int w, h, stride;
        lock (_snapshotLock)
        {
            if (_snapshotBgra is null)
                throw new InvalidOperationException("No frame available yet");
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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _playGate.Set();
        if (_thread is { IsAlive: true })
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _frames.OnCompleted();
        _stateChanged.OnCompleted();
        _telemetry.OnCompleted();
        _positionChanged.OnCompleted();
        _cts?.Dispose();
        _playGate.Dispose();
    }

    private unsafe void Run()
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        var videoStreamIndex = -1;
        var swsSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        double timeBase = 0;

        // Real-time presentation clock. Origin is (wall, media) of the first
        // frame after start/seek/resume; subsequent frames sleep until their
        // media offset elapses on the wall clock.
        var clock = Stopwatch.StartNew();
        var haveClock = false;
        double wallOriginSec = 0, mediaOriginSec = 0;

        try
        {
            fmtCtx = ffmpeg.avformat_alloc_context();
            var ret = ffmpeg.avformat_open_input(&fmtCtx, _options.FilePath, null, null);
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
                throw new InvalidOperationException("No video stream in file");

            var stream = fmtCtx->streams[videoStreamIndex];
            timeBase = ffmpeg.av_q2d(stream->time_base);
            SetDuration(fmtCtx, stream);

            var codecpar = stream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"No decoder for codec id {codecpar->codec_id}");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ret = ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar);
            FfmpegError.ThrowIfError(ret, "avcodec_parameters_to_context");

            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            FfmpegError.ThrowIfError(ret, "avcodec_open2");

            _width = codecCtx->width;
            _height = codecCtx->height;
            _codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name);

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();

            SetState(SessionState.Playing);
            var ct = _cts!.Token;

            while (!ct.IsCancellationRequested)
            {
                // Park while paused (still serves seeks: SeekAsync sets the gate).
                if (_paused && Interlocked.Read(ref _pendingSeekTicks) == NoSeek)
                {
                    try { _playGate.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                    if (ct.IsCancellationRequested) break;
                    haveClock = false; // rebase clock after a pause
                }

                var seek = Interlocked.Exchange(ref _pendingSeekTicks, NoSeek);
                long seekTargetPts = ffmpeg.AV_NOPTS_VALUE;
                if (seek != NoSeek)
                {
                    var target = TimeSpan.FromTicks(seek);
                    var ts = (long)(target.TotalSeconds / timeBase);
                    ret = ffmpeg.avformat_seek_file(fmtCtx, videoStreamIndex, long.MinValue, ts, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (ret < 0)
                        _logger.LogWarning("avformat_seek_file failed: {Err}", FfmpegError.Describe(ret));
                    ffmpeg.avcodec_flush_buffers(codecCtx);
                    haveClock = false;
                    seekTargetPts = ts; // decode-and-drop until we reach the requested point
                    // After a seek we present one frame even if paused, so the UI
                    // updates to the new position. Re-park happens next iteration.
                }

                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        // Hold at end-of-file: freeze on the last frame and park
                        // until a seek (or dispose) arrives.
                        SetPosition(Duration);
                        _paused = true;
                        _playGate.Reset();
                        SetState(SessionState.Paused);
                        continue;
                    }
                    _logger.LogWarning("av_read_frame failed: {Err}", FfmpegError.Describe(ret));
                    break;
                }

                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

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

                    var pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                        ? frame->best_effort_timestamp
                        : frame->pts;

                    // Drop frames between the seek keyframe and the requested
                    // point so we land precisely on the target.
                    if (seekTargetPts != ffmpeg.AV_NOPTS_VALUE && pts != ffmpeg.AV_NOPTS_VALUE && pts < seekTargetPts)
                    {
                        ffmpeg.av_frame_unref(frame);
                        continue;
                    }
                    var justSeeked = seekTargetPts != ffmpeg.AV_NOPTS_VALUE;
                    seekTargetPts = ffmpeg.AV_NOPTS_VALUE;

                    var mediaSec = pts != ffmpeg.AV_NOPTS_VALUE ? pts * timeBase : mediaOriginSec;

                    var framePixFmt = (AVPixelFormat)frame->format;
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

                    PaceTo(clock, ref haveClock, ref wallOriginSec, ref mediaOriginSec, mediaSec, ct);
                    if (ct.IsCancellationRequested) break;

                    EmitFrame(sws, frame, mediaSec);
                    SetPosition(TimeSpan.FromSeconds(mediaSec));
                    ffmpeg.av_frame_unref(frame);

                    // A seek while paused presents exactly one frame, then re-parks.
                    if (justSeeked && _paused)
                    {
                        _playGate.Reset();
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playback session loop failed");
            SetState(SessionState.Failed, ex.Message);
            return;
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (frame != null) { var p = frame; ffmpeg.av_frame_free(&p); }
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var p = codecCtx; ffmpeg.avcodec_free_context(&p); }
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
        }

        SetState(SessionState.Idle);
    }

    private unsafe void SetDuration(AVFormatContext* fmtCtx, AVStream* stream)
    {
        // Prefer the container duration (AV_TIME_BASE = microseconds); fall back
        // to the stream's own duration in its time base.
        long ticks = 0;
        if (fmtCtx->duration > 0 && fmtCtx->duration != ffmpeg.AV_NOPTS_VALUE)
            ticks = TimeSpan.FromSeconds(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE).Ticks;
        else if (stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
            ticks = TimeSpan.FromSeconds(stream->duration * ffmpeg.av_q2d(stream->time_base)).Ticks;
        Interlocked.Exchange(ref _durationTicks, ticks);
    }

    private void PaceTo(Stopwatch clock, ref bool haveClock, ref double wallOriginSec, ref double mediaOriginSec, double mediaSec, CancellationToken ct)
    {
        if (!haveClock)
        {
            wallOriginSec = clock.Elapsed.TotalSeconds;
            mediaOriginSec = mediaSec;
            haveClock = true;
            return;
        }

        var targetWall = wallOriginSec + (mediaSec - mediaOriginSec);
        while (!ct.IsCancellationRequested)
        {
            var remaining = targetWall - clock.Elapsed.TotalSeconds;
            if (remaining <= 0.001) break;
            // Bail out early if a seek arrives so scrubbing stays responsive.
            if (Interlocked.Read(ref _pendingSeekTicks) != NoSeek) break;
            var ms = (int)Math.Min(remaining * 1000, 50);
            if (ms > 0) Thread.Sleep(ms);
        }
    }

    private unsafe void EmitFrame(SwsContext* sws, AVFrame* frame, double mediaSec)
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

            var ptsMicros = (long)(mediaSec * 1_000_000);
            var vf = new VideoFrame(bgra, _width, _height, stride, ptsMicros, DateTime.UtcNow);
            try
            {
                _frames.OnNext(vf);
                Interlocked.Increment(ref _framesDecoded);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw in frame OnNext");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bgra);
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

    private void SetPosition(TimeSpan position)
    {
        Interlocked.Exchange(ref _positionTicks, position.Ticks);
        _positionChanged.OnNext(position);
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
}
