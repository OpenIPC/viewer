using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Video.Pipeline;

// Decodes a container's first audio stream to the one PCM format every sink
// consumes (Phase 17.1): signed-16 interleaved, stereo, 48 kHz. swresample
// does the heavy lifting; the native sink (WASAPI etc.) only converts to the
// device mix format. Shared by the live RTSP session and the file player.
// Not thread-safe — owned and driven by a single decode thread.
internal sealed unsafe class FfmpegAudioDecoder : IDisposable
{
    public const int OutSampleRate = 48000;
    public const int OutChannels = 2;

    private readonly ILogger _logger;
    private AVCodecContext* _ctx;
    private SwrContext* _swr;
    private AVFrame* _frame;

    private FfmpegAudioDecoder(int streamIndex, double timeBase, AVCodecContext* ctx, SwrContext* swr, ILogger logger)
    {
        StreamIndex = streamIndex;
        TimeBase = timeBase;
        _ctx = ctx;
        _swr = swr;
        _frame = ffmpeg.av_frame_alloc();
        _logger = logger;
    }

    public int StreamIndex { get; }

    // Seconds per PTS unit of the audio stream.
    public double TimeBase { get; }

    // Finds the first audio stream and opens its decoder + resampler. Returns
    // null if there is no audio or setup fails (callers treat both as "no audio").
    public static FfmpegAudioDecoder? TryOpen(AVFormatContext* fmtCtx, ILogger logger)
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
        if (idx < 0) return null;

        var stream = fmtCtx->streams[idx];
        var codecpar = stream->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            logger.LogWarning("No audio decoder for codec id {Id}", codecpar->codec_id);
            return null;
        }

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        var ret = ffmpeg.avcodec_parameters_to_context(ctx, codecpar);
        if (ret < 0)
        {
            logger.LogWarning("audio avcodec_parameters_to_context failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        ret = ffmpeg.avcodec_open2(ctx, codec, null);
        if (ret < 0)
        {
            logger.LogWarning("audio avcodec_open2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return null;
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
        ffmpeg.av_channel_layout_default(&outLayout, OutChannels);

        SwrContext* swr = null;
        ret = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutSampleRate,
            inLayout, ctx->sample_fmt, ctx->sample_rate,
            0, null);
        if (ret < 0 || swr == null)
        {
            logger.LogWarning("swr_alloc_set_opts2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.av_channel_layout_uninit(&outLayout);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        ret = ffmpeg.swr_init(swr);
        ffmpeg.av_channel_layout_uninit(&outLayout);
        if (ret < 0)
        {
            logger.LogWarning("swr_init failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.swr_free(&swr);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }

        var baseName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
        logger.LogInformation("Audio decode active: {Codec} {Rate}Hz {Ch}ch → 48kHz stereo S16",
            baseName, ctx->sample_rate, ctx->ch_layout.nb_channels);

        return new FfmpegAudioDecoder(idx, ffmpeg.av_q2d(stream->time_base), ctx, swr, logger);
    }

    // Decodes one packet and hands every resulting PCM chunk to emit.
    public void Decode(AVPacket* packet, Action<AudioFrame> emit, CancellationToken ct)
    {
        if (_ctx == null) return;

        var ret = ffmpeg.avcodec_send_packet(_ctx, packet);
        if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            _logger.LogDebug("audio send_packet failed: {Err}", FfmpegError.Describe(ret));
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                _logger.LogDebug("audio receive_frame failed: {Err}", FfmpegError.Describe(ret));
                break;
            }

            var pcm = Resample(_frame, _ctx->sample_rate);
            ffmpeg.av_frame_unref(_frame);
            if (pcm is null) continue;
            try
            {
                emit(new AudioFrame(pcm, OutSampleRate, OutChannels, 0));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw in audio OnNext");
            }
        }
    }

    // Drop buffered decoder state after a seek so stale audio isn't played.
    public void Flush()
    {
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
    }

    private byte[]? Resample(AVFrame* frame, int inRate)
    {
        if (inRate <= 0) return null;

        // Worst-case output sample count: queued resampler delay + this frame,
        // rescaled to the output rate.
        var delay = ffmpeg.swr_get_delay(_swr, inRate);
        var maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, OutSampleRate, inRate, AVRounding.AV_ROUND_UP);
        if (maxOut <= 0) return null;

        var pcm = new byte[maxOut * OutChannels * 2]; // S16 = 2 bytes/sample
        int produced;
        fixed (byte* dst = pcm)
        {
            var outPlane = dst;
            produced = ffmpeg.swr_convert(_swr, &outPlane, maxOut, frame->extended_data, frame->nb_samples);
        }
        if (produced <= 0) return null;

        var bytes = produced * OutChannels * 2;
        if (bytes == pcm.Length) return pcm;
        var payload = new byte[bytes];
        Buffer.BlockCopy(pcm, 0, payload, 0, bytes);
        return payload;
    }

    public void Dispose()
    {
        if (_swr != null) { var p = _swr; ffmpeg.swr_free(&p); _swr = null; }
        if (_frame != null) { var p = _frame; ffmpeg.av_frame_free(&p); _frame = null; }
        if (_ctx != null) { var p = _ctx; ffmpeg.avcodec_free_context(&p); _ctx = null; }
    }
}
