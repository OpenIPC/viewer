using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video.Recording;

// Phase 6.2 strategy: shell out to ffmpeg.exe rather than driving FFmpeg.AutoGen.
// Zero decode CPU (-c copy), trivially killable, naturally rotates segments via
// -f segment, and a crash in the recorder can't take down the live decoder.
//
// FFmpeg path resolution (first hit wins):
//   1. Explicit override (constructor / appsettings "Recording:FfmpegPath")
//   2. Per-RID bundled binary in runtimes/{rid}/native/ (Phase 8 fills these
//      for linux-x64 / osx-x64 / osx-arm64; today only win-x64 has artifacts)
//   3. "ffmpeg" — falls through to OS PATH lookup (apt/brew/PATH on *nix)
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
        var (rid, exe) = RuntimeIds.Current();
        if (rid is not null)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", exe);
            if (File.Exists(bundled)) return bundled;
        }
        return "ffmpeg";
    }
}
