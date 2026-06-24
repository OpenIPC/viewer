using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video;

public sealed class FfmpegVideoEngine : IVideoEngine, IPlaybackEngine
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHwDecoderFactory? _hwFactory;

    public FfmpegVideoEngine(ILoggerFactory loggerFactory, IHwDecoderFactory? hwFactory = null)
    {
        _loggerFactory = loggerFactory;
        _hwFactory = hwFactory;

        // Route FFmpeg's native log lines into our logger. Singleton, so this
        // subscribes exactly once for the app's lifetime.
        var nativeLog = loggerFactory.CreateLogger("FFmpeg.Native");
        FfmpegRuntime.NativeLog += (level, message) =>
        {
            // AV_LOG_ERROR=16, AV_LOG_WARNING=24 — lower is more severe.
            if (level <= 16) nativeLog.LogWarning("{Message}", message);
            else nativeLog.LogInformation("{Message}", message);
        };
    }

    public IVideoSession CreateSession(VideoSessionOptions options)
    {
        IVideoSession Create() => new FfmpegVideoSession(options, _hwFactory, _loggerFactory.CreateLogger<FfmpegVideoSession>());

        if (!options.AutoReconnect)
            return Create();

        return new AutoReconnectingVideoSession(Create, _loggerFactory.CreateLogger<AutoReconnectingVideoSession>());
    }

    public IPlaybackSession OpenFile(PlaybackOptions options) =>
        new FfmpegPlaybackSession(options, _loggerFactory.CreateLogger<FfmpegPlaybackSession>());
}
