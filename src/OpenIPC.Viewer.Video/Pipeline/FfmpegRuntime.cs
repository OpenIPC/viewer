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

        var nativeDir = ResolveNativeDir();
        ffmpeg.RootPath = nativeDir;

        try
        {
            // Touch one function so the P/Invoke loader resolves the native
            // libs early — anything missing surfaces here instead of mid-stream.
            _ = ffmpeg.av_version_info();
            ffmpeg.avformat_network_init();
        }
        catch (DllNotFoundException ex)
        {
            // Most-likely cause on Android: the APK shipped without FFmpeg
            // shared libs because tools/build-ffmpeg-android.sh wasn't run for
            // the device's ABI (arm64-v8a / x86_64). Re-throw with a hint so
            // the failure reads as a setup problem, not a code bug.
            var (rid, _) = RuntimeIds.Current();
            var probe = string.IsNullOrEmpty(nativeDir)
                ? "(loader path)"
                : nativeDir;
            throw new FfmpegNativeLibsMissingException(
                $"FFmpeg native libraries are missing for runtime '{rid ?? "unknown"}'. " +
                $"Probed: {probe}. " +
                $"Android: run tools/build-ffmpeg-android.sh (WSL/Linux/macOS) — builds " +
                $"arm64-v8a + x86_64 from upstream n7.1. " +
                $"Desktop: run tools/fetch-ffmpeg.ps1 (Windows) or install ffmpeg via " +
                $"apt/brew. Inner: {ex.Message}", ex);
        }
        catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException dll)
        {
            var (rid, _) = RuntimeIds.Current();
            throw new FfmpegNativeLibsMissingException(
                $"FFmpeg native libraries are missing for runtime '{rid ?? "unknown"}'. " +
                $"Run tools/build-ffmpeg-android.sh (Android) or tools/fetch-ffmpeg.ps1 " +
                $"(Desktop). Inner: {dll.Message}", ex);
        }
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
        // (apt/brew-installed libs on Linux/macOS, DLLs next to the exe on Windows,
        // and the bundled .so files added via <AndroidNativeLibrary> on Android).
        return "";
    }
}

public sealed class FfmpegNativeLibsMissingException : Exception
{
    public FfmpegNativeLibsMissingException(string message, Exception inner)
        : base(message, inner) { }
}
