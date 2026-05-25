using System;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen;

namespace OpenIPC.Viewer.Video.Pipeline;

internal static class FfmpegRuntime
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        ffmpeg.RootPath = ResolveNativeDir();
        // Touch one function to surface load errors early.
        _ = ffmpeg.av_version_info();
        ffmpeg.avformat_network_init();
    }

    private static string ResolveNativeDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var (rid, _) = RuntimeIds.Current();
        if (rid is not null)
        {
            var candidate = Path.Combine(baseDir, "runtimes", rid, "native");
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Fall back to "" — FFmpeg.AutoGen then uses the platform loader path
        // (apt/brew-installed libs on Linux/macOS, DLLs next to the exe on Windows).
        return "";
    }
}
