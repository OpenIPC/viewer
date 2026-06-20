using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Video.Pipeline;

// avformat-based probe (Phase 16.2). Opens the container, reads stream info,
// and returns the exact duration/codec/dimensions — no decoding, no playback.
// Cross-platform via FFmpeg.AutoGen, same as the rest of the pipeline.
public sealed class FfmpegMediaProbe : IMediaProbe
{
    private readonly ILogger<FfmpegMediaProbe> _logger;

    public FfmpegMediaProbe(ILogger<FfmpegMediaProbe> logger) => _logger = logger;

    public Task<MediaInfo> ProbeAsync(string filePath, CancellationToken ct)
    {
        // Native calls block; run off the calling thread.
        return Task.Run(() => Probe(filePath), ct);
    }

    private unsafe MediaInfo Probe(string filePath)
    {
        FfmpegRuntime.EnsureInitialized();

        AVFormatContext* fmtCtx = null;
        try
        {
            var ret = ffmpeg.avformat_open_input(&fmtCtx, filePath, null, null);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            var duration = fmtCtx->duration > 0 && fmtCtx->duration != ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.FromSeconds(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;

            string? codec = null;
            int width = 0, height = 0;
            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var par = fmtCtx->streams[i]->codecpar;
                if (par->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    continue;

                width = par->width;
                height = par->height;
                if (duration == TimeSpan.Zero)
                {
                    var sd = fmtCtx->streams[i]->duration;
                    if (sd > 0 && sd != ffmpeg.AV_NOPTS_VALUE)
                        duration = TimeSpan.FromSeconds(sd * ffmpeg.av_q2d(fmtCtx->streams[i]->time_base));
                }

                var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (dec != null)
                    codec = Marshal.PtrToStringAnsi((IntPtr)dec->name);
                break;
            }

            return new MediaInfo(duration, codec, width, height);
        }
        finally
        {
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
        }
    }
}
