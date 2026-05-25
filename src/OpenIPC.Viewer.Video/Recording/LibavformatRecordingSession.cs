using System;
using System.Globalization;
using System.IO;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video.Recording;

// In-process recording via FFmpeg.AutoGen libavformat. Mirrors the
// FfmpegSubprocessRecorder semantics ("-c copy" + fragmented mp4) but
// without spawning ffmpeg.exe — needed on Android where APK sandboxes
// can't shell out.
//
// 9c limitation: single file per session. Segment rotation lands in a
// follow-up (would need a keyframe-aligned close+reopen at segment_time).
// FilenamePattern's strftime is honoured for the one filename we create.
internal sealed class LibavformatRecordingSession : IRecordingSession
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly RecordingOptions _options;
    private readonly ILogger _logger;
    private readonly Subject<RecordingEvent> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private string? _outputPath;
    private volatile bool _stopRequested;

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public string? CurrentSegmentPath => _outputPath;
    public IObservable<RecordingEvent> Events => _events;

    public LibavformatRecordingSession(RecordingOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public void Start()
    {
        FfmpegRuntime.EnsureInitialized();
        _outputPath = ResolveOutputPath();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"rec-{_options.CameraId}",
        };
        _thread.Start();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_stopRequested) { await _stoppedTcs.Task.ConfigureAwait(false); return; }
        _stopRequested = true;
        _cts.Cancel();

        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        killCts.CancelAfter(StopGrace);
        try
        {
            await _stoppedTcs.Task.WaitAsync(killCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Recording loop did not exit within {Sec}s", StopGrace.TotalSeconds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        if (_thread is { IsAlive: true })
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        try { _events.OnCompleted(); } catch { /* already completed */ }
        _cts.Dispose();
    }

    private string ResolveOutputPath()
    {
        // FilenamePattern uses strftime tokens (matches FfmpegSubprocessRecorder).
        // We expand them in C# since libavformat doesn't apply strftime to
        // the URL argument — that was ffmpeg.exe's -strftime flag, not a
        // libavformat feature.
        var name = StrftimeApprox(_options.FilenamePattern, DateTime.UtcNow);
        return Path.Combine(_options.OutputDirectory, name);
    }

    private static string StrftimeApprox(string pattern, DateTime utc)
    {
        // Only the tokens our default patterns use, leaving anything unknown
        // alone so users see what went wrong instead of silent truncation.
        var local = utc.ToLocalTime();
        return pattern
            .Replace("%Y", local.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("%m", local.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("%d", local.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("%H", local.ToString("HH", CultureInfo.InvariantCulture))
            .Replace("%M", local.ToString("mm", CultureInfo.InvariantCulture))
            .Replace("%S", local.ToString("ss", CultureInfo.InvariantCulture));
    }

    private unsafe void Run()
    {
        AVFormatContext* inputCtx = null;
        AVFormatContext* outputCtx = null;
        AVDictionary* inputOpts = null;
        AVDictionary* outputOpts = null;
        AVPacket* packet = null;
        int[] streamMap = System.Array.Empty<int>();
        var ct = _cts.Token;
        var stopReason = RecordingStopReason.User;

        try
        {
            Directory.CreateDirectory(_options.OutputDirectory);

            // --- Input ---
            BuildInputOpts(&inputOpts);
            inputCtx = ffmpeg.avformat_alloc_context();
            var url = BuildRtspUri(_options.RtspUri, _options.Credentials);
            var ret = ffmpeg.avformat_open_input(&inputCtx, url, null, &inputOpts);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");

            ret = ffmpeg.avformat_find_stream_info(inputCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            // --- Output ---
            AVFormatContext* outRaw = null;
            ret = ffmpeg.avformat_alloc_output_context2(&outRaw, null, "mp4", _outputPath);
            FfmpegError.ThrowIfError(ret, "avformat_alloc_output_context2");
            outputCtx = outRaw;

            streamMap = new int[(int)inputCtx->nb_streams];
            for (var i = 0; i < (int)inputCtx->nb_streams; i++)
            {
                streamMap[i] = -1;
                var inStream = inputCtx->streams[i];
                var kind = inStream->codecpar->codec_type;
                if (kind != AVMediaType.AVMEDIA_TYPE_VIDEO && kind != AVMediaType.AVMEDIA_TYPE_AUDIO)
                    continue;

                var outStream = ffmpeg.avformat_new_stream(outputCtx, null);
                if (outStream == null)
                    throw new InvalidOperationException("avformat_new_stream returned null");

                ret = ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar);
                FfmpegError.ThrowIfError(ret, "avcodec_parameters_copy");
                // Clear codec_tag — input MP4-flavoured tags (e.g. avc1) don't
                // always survive a remux through mp4 output and FFmpeg refuses
                // the stream. Let the muxer pick.
                outStream->codecpar->codec_tag = 0;

                streamMap[i] = (int)outStream->index;
            }

            if ((outputCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&outputCtx->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE);
                FfmpegError.ThrowIfError(ret, "avio_open");
            }

            // Fragmented mp4 — same intent as FfmpegSubprocessRecorder
            // (kill-survivable files; moov is up front, frags self-describe).
            ffmpeg.av_dict_set(&outputOpts, "movflags", "+frag_keyframe+empty_moov+default_base_moof", 0);
            ret = ffmpeg.avformat_write_header(outputCtx, &outputOpts);
            FfmpegError.ThrowIfError(ret, "avformat_write_header");

            _events.OnNext(new RecordingEvent.Started(_outputPath!, DateTime.UtcNow));

            // --- Pump ---
            packet = ffmpeg.av_packet_alloc();
            while (!ct.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(inputCtx, packet);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        _logger.LogInformation("Recording input EOF");
                        stopReason = RecordingStopReason.ProcessExited;
                        break;
                    }
                    _logger.LogWarning("av_read_frame failed: {Err}", FfmpegError.Describe(ret));
                    stopReason = RecordingStopReason.Error;
                    break;
                }

                var inIdx = packet->stream_index;
                if (inIdx >= streamMap.Length || streamMap[inIdx] < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var inStream = inputCtx->streams[inIdx];
                var outStream = outputCtx->streams[streamMap[inIdx]];

                packet->stream_index = streamMap[inIdx];
                ffmpeg.av_packet_rescale_ts(packet, inStream->time_base, outStream->time_base);
                packet->pos = -1;

                ret = ffmpeg.av_interleaved_write_frame(outputCtx, packet);
                if (ret < 0)
                {
                    _logger.LogWarning("av_interleaved_write_frame failed: {Err}", FfmpegError.Describe(ret));
                    stopReason = RecordingStopReason.Error;
                    break;
                }
                // av_interleaved_write_frame takes ownership of packet contents
                // and unrefs internally — no av_packet_unref here.
            }

            // Trailer flushes any buffered frames + finalizes the moov atom.
            var trailerRet = ffmpeg.av_write_trailer(outputCtx);
            if (trailerRet < 0)
                _logger.LogWarning("av_write_trailer failed: {Err}", FfmpegError.Describe(trailerRet));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording loop failed");
            try { _events.OnNext(new RecordingEvent.Error(ex.Message)); } catch { }
            stopReason = RecordingStopReason.Error;
        }
        finally
        {
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (outputCtx != null)
            {
                if (outputCtx->pb != null && (outputCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    var pb = outputCtx->pb;
                    ffmpeg.avio_closep(&pb);
                }
                ffmpeg.avformat_free_context(outputCtx);
            }
            if (inputCtx != null) ffmpeg.avformat_close_input(&inputCtx);
            if (inputOpts != null) ffmpeg.av_dict_free(&inputOpts);
            if (outputOpts != null) ffmpeg.av_dict_free(&outputOpts);

            try { _events.OnNext(new RecordingEvent.Stopped(DateTime.UtcNow, stopReason)); } catch { }
            _stoppedTcs.TrySetResult();
        }
    }

    private static unsafe void BuildInputOpts(AVDictionary** opts)
    {
        ffmpeg.av_dict_set(opts, "rtsp_transport", "tcp", 0);
        ffmpeg.av_dict_set(opts, "stimeout", "5000000", 0);
        ffmpeg.av_dict_set(opts, "max_delay", "200000", 0);
    }

    private static string BuildRtspUri(Uri rtsp, CameraCredentials? creds)
    {
        if (creds is null || !string.IsNullOrEmpty(rtsp.UserInfo))
            return rtsp.ToString();
        var b = new UriBuilder(rtsp)
        {
            UserName = Uri.EscapeDataString(creds.Username),
            Password = Uri.EscapeDataString(creds.Password),
        };
        return b.Uri.ToString();
    }
}
