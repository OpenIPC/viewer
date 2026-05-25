using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video;

public sealed class FfmpegVideoEngine : IVideoEngine
{
    private readonly ILoggerFactory _loggerFactory;

    public FfmpegVideoEngine(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IVideoSession CreateSession(VideoSessionOptions options)
    {
        IVideoSession Create() => new FfmpegVideoSession(options, _loggerFactory.CreateLogger<FfmpegVideoSession>());

        if (!options.AutoReconnect)
            return Create();

        return new AutoReconnectingVideoSession(Create, _loggerFactory.CreateLogger<AutoReconnectingVideoSession>());
    }
}
