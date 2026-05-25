using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video;

public sealed class FfmpegVideoEngine : IVideoEngine
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHwDecoderFactory? _hwFactory;

    public FfmpegVideoEngine(ILoggerFactory loggerFactory, IHwDecoderFactory? hwFactory = null)
    {
        _loggerFactory = loggerFactory;
        _hwFactory = hwFactory;
    }

    public IVideoSession CreateSession(VideoSessionOptions options)
    {
        IVideoSession Create() => new FfmpegVideoSession(options, _hwFactory, _loggerFactory.CreateLogger<FfmpegVideoSession>());

        if (!options.AutoReconnect)
            return Create();

        return new AutoReconnectingVideoSession(Create, _loggerFactory.CreateLogger<AutoReconnectingVideoSession>());
    }
}
