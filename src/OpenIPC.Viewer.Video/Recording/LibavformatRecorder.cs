using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Video.Recording;

// Cross-platform IRecorder that drives recording in-process via libavformat
// (FFmpeg.AutoGen). Used on Android where Process.Start("ffmpeg") doesn't
// work; desktop sticks with FfmpegSubprocessRecorder for crash isolation
// + per-segment rotation (this recorder doesn't rotate yet).
public sealed class LibavformatRecorder : IRecorder
{
    private readonly ILoggerFactory _loggerFactory;

    public LibavformatRecorder(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public Task<IRecordingSession> StartAsync(RecordingOptions options, CancellationToken ct)
    {
        var session = new LibavformatRecordingSession(options, _loggerFactory.CreateLogger<LibavformatRecordingSession>());
        session.Start();
        return Task.FromResult<IRecordingSession>(session);
    }
}
