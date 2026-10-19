using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Video.Pipeline;

// Decodes a whole (short) audio file to PCM for the custom notification sound —
// mp3, ogg, m4a, flac, wav, anything this FFmpeg build reads. Reuses the live
// pipeline's FfmpegAudioDecoder, so the result is 48 kHz stereo S16. Stops at
// maxLength so picking a song doesn't load ten minutes of audio.
public sealed class FfmpegAudioClipDecoder : IAudioClipDecoder
{
    private readonly ILogger<FfmpegAudioClipDecoder> _logger;

    public FfmpegAudioClipDecoder(ILogger<FfmpegAudioClipDecoder> logger) => _logger = logger;

    public Task<PcmClip> DecodeAsync(string path, TimeSpan maxLength, CancellationToken ct) =>
        Task.Run(() => Decode(path, maxLength, ct), ct);

    private unsafe PcmClip Decode(string path, TimeSpan maxLength, CancellationToken ct)
    {
        FfmpegRuntime.EnsureInitialized();

        var maxBytes = (long)(maxLength.TotalSeconds * FfmpegAudioDecoder.OutSampleRate)
                       * FfmpegAudioDecoder.OutChannels * 2;
        using var pcm = new MemoryStream();

        AVFormatContext* fmtCtx = null;
        AVPacket* packet = null;
        FfmpegAudioDecoder? decoder = null;
        try
        {
            var ret = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");
            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            decoder = FfmpegAudioDecoder.TryOpen(fmtCtx, _logger)
                      ?? throw new InvalidDataException("The file has no audio this build can decode");

            void Collect(Core.Video.AudioFrame f)
            {
                var room = maxBytes - pcm.Length;
                if (room <= 0) return;
                pcm.Write(f.Pcm16, 0, (int)Math.Min(room, f.Pcm16.Length));
            }

            packet = ffmpeg.av_packet_alloc();
            while (!ct.IsCancellationRequested && pcm.Length < maxBytes)
            {
                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0) break; // EOF or read error — keep what we have
                if (packet->stream_index == decoder.StreamIndex)
                    decoder.Decode(packet, Collect, ct);
                ffmpeg.av_packet_unref(packet);
            }
            // Drain the decoder's buffered tail.
            if (pcm.Length < maxBytes)
                decoder.Decode(null, Collect, ct);
        }
        finally
        {
            decoder?.Dispose();
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
        }

        ct.ThrowIfCancellationRequested();
        if (pcm.Length == 0) throw new InvalidDataException("No audio could be decoded from the file");
        return new PcmClip(pcm.ToArray(), FfmpegAudioDecoder.OutSampleRate, FfmpegAudioDecoder.OutChannels);
    }
}
