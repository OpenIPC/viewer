using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video.Recording;

// In-process clip export via FFmpeg.AutoGen libavformat (Phase 16.5) — the
// mobile counterpart to FfmpegSubprocessClipExporter, since Android/iOS can't
// spawn ffmpeg.exe. Stream-copy remux of [Start, End): seek BACKWARD to the
// keyframe at/before Start (GOP-accurate, same as `ffmpeg -ss … -c copy`),
// copy packets until End, and let the muxer shift timestamps to zero.
//
// Precise (frame-accurate) export needs a decode+re-encode pass and is not
// implemented in-process yet; the request falls back to the fast stream copy.
public sealed class LibavformatClipExporter : IClipExporter
{
    private readonly ILogger<LibavformatClipExporter> _logger;

    public LibavformatClipExporter(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<LibavformatClipExporter>();

    public Task ExportAsync(ClipExportRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var dur = ClipBounds.Duration(request.Start, request.End);
        if (dur <= TimeSpan.Zero)
            throw new ArgumentException("Export range is empty", nameof(request));

        if (request.Precise)
            _logger.LogInformation("Precise export not supported in-process; using stream copy.");

        // libav calls block — run off the caller's thread.
        return Task.Run(() => Run(request, dur, progress, ct), ct);
    }

    private unsafe void Run(ClipExportRequest request, TimeSpan dur, IProgress<double>? progress, CancellationToken ct)
    {
        FfmpegRuntime.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);

        AVFormatContext* inputCtx = null;
        AVFormatContext* outputCtx = null;
        AVPacket* packet = null;
        var streamMap = Array.Empty<int>();

        try
        {
            // --- Input ---
            var ret = ffmpeg.avformat_open_input(&inputCtx, request.SourcePath, null, null);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");
            ret = ffmpeg.avformat_find_stream_info(inputCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            // --- Output (plain mp4; moov-at-end is the most compatible) ---
            AVFormatContext* outRaw = null;
            ret = ffmpeg.avformat_alloc_output_context2(&outRaw, null, "mp4", request.DestinationPath);
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
                if (outStream == null) throw new InvalidOperationException("avformat_new_stream returned null");
                ret = ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar);
                FfmpegError.ThrowIfError(ret, "avcodec_parameters_copy");
                outStream->codecpar->codec_tag = 0;
                streamMap[i] = (int)outStream->index;
            }

            if ((outputCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&outputCtx->pb, request.DestinationPath, ffmpeg.AVIO_FLAG_WRITE);
                FfmpegError.ThrowIfError(ret, "avio_open");
            }

            // Seek to the keyframe at/before Start (stream_index -1 → AV_TIME_BASE units).
            var startTs = (long)(request.Start.TotalSeconds * ffmpeg.AV_TIME_BASE);
            var seekRet = ffmpeg.av_seek_frame(inputCtx, -1, startTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekRet < 0)
                _logger.LogWarning("Clip seek failed ({Err}); exporting from start.", FfmpegError.Describe(seekRet));

            // Let the muxer rebase the first timestamp to zero so the clip starts
            // at 0 (mirrors `-avoid_negative_ts make_zero`). 2 = AVFMT_AVOID_NEG_TS_MAKE_ZERO.
            outputCtx->avoid_negative_ts = 2;

            ret = ffmpeg.avformat_write_header(outputCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_write_header");

            var endSec = request.End.TotalSeconds;
            var startSec = request.Start.TotalSeconds;
            var durSec = dur.TotalSeconds;

            packet = ffmpeg.av_packet_alloc();
            while (!ct.IsCancellationRequested)
            {
                ret = ffmpeg.av_read_frame(inputCtx, packet);
                if (ret < 0) break; // EOF or error → done

                var inIdx = packet->stream_index;
                if (inIdx >= streamMap.Length || streamMap[inIdx] < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var inStream = inputCtx->streams[inIdx];
                var refTs = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                if (refTs != ffmpeg.AV_NOPTS_VALUE)
                {
                    var ptsSec = refTs * ffmpeg.av_q2d(inStream->time_base);
                    if (ptsSec > endSec) { ffmpeg.av_packet_unref(packet); break; } // past the out-point
                    if (durSec > 0)
                        progress?.Report(Math.Clamp((ptsSec - startSec) / durSec, 0, 1));
                }

                var outStream = outputCtx->streams[streamMap[inIdx]];
                packet->stream_index = streamMap[inIdx];
                ffmpeg.av_packet_rescale_ts(packet, inStream->time_base, outStream->time_base);
                packet->pos = -1;

                ret = ffmpeg.av_interleaved_write_frame(outputCtx, packet);
                if (ret < 0)
                {
                    _logger.LogWarning("av_interleaved_write_frame failed: {Err}", FfmpegError.Describe(ret));
                    break;
                }
                // av_interleaved_write_frame unrefs the packet internally.
            }

            ct.ThrowIfCancellationRequested();

            var trailerRet = ffmpeg.av_write_trailer(outputCtx);
            if (trailerRet < 0)
                _logger.LogWarning("av_write_trailer failed: {Err}", FfmpegError.Describe(trailerRet));

            progress?.Report(1.0);
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
        }
    }
}
