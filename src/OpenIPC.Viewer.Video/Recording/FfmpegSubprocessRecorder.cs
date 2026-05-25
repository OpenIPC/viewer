using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Video.Recording;

// Phase 6.2 strategy: shell out to ffmpeg.exe rather than driving FFmpeg.AutoGen.
// Zero decode CPU (-c copy), trivially killable, naturally rotates segments via
// -f segment, and a crash in the recorder can't take down the live decoder.
//
// FFmpeg path resolution (first hit wins):
//   1. Explicit override (constructor / appsettings "Recording:FfmpegPath")
//   2. runtimes/win-x64/native/ffmpeg.exe next to the app (bundled install)
//   3. "ffmpeg" — falls through to OS PATH lookup
public sealed class FfmpegSubprocessRecorder : IRecorder
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _ffmpegPath;

    public FfmpegSubprocessRecorder(ILoggerFactory loggerFactory, string? ffmpegPathOverride = null)
    {
        _loggerFactory = loggerFactory;
        _ffmpegPath = !string.IsNullOrWhiteSpace(ffmpegPathOverride)
            ? ffmpegPathOverride!
            : ResolveDefault();
    }

    public Task<IRecordingSession> StartAsync(RecordingOptions options, CancellationToken ct)
    {
        var session = new FfmpegRecordingSession(options, _ffmpegPath, _loggerFactory.CreateLogger<FfmpegRecordingSession>());
        session.Start();
        return Task.FromResult<IRecordingSession>(session);
    }

    private static string ResolveDefault()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }
}
