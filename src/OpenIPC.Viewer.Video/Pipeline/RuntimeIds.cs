using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenIPC.Viewer.Video.Pipeline;

// Shared RID detection used by FfmpegRuntime (native shared-lib path) and
// FfmpegSubprocessRecorder (ffmpeg.exe path). Single source of truth means
// adding a new arch (e.g. linux-arm64) only touches one place.
internal static class RuntimeIds
{
    public static (string? Rid, string ExeName) Current()
    {
        // Order: Android first because OperatingSystem.IsLinux() returns
        // true on Android too (kernel is Linux). Same ordering trap as in
        // PlatformPaths.ResolveAppData.
        if (OperatingSystem.IsWindows())
            return ("win-x64", "ffmpeg.exe");
        if (OperatingSystem.IsAndroid())
            return (RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "android-arm64" : "android-x64", "ffmpeg");
        if (OperatingSystem.IsMacOS())
            return (RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "osx-arm64" : "osx-x64", "ffmpeg");
        if (OperatingSystem.IsLinux())
            return ("linux-x64", "ffmpeg");
        return (null, "ffmpeg");
    }

    // The ffmpeg CLI for the subprocess recorder / clip exporter: the bundled
    // binary, else (macOS) Homebrew's — keg-only ffmpeg@7 isn't on PATH, and a
    // Finder-launched app doesn't see /opt/homebrew/bin either — else PATH.
    public static string ResolveFfmpegExe()
    {
        var (rid, exe) = Current();
        if (rid is not null)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", exe);
            if (File.Exists(bundled)) return bundled;
        }

        if (OperatingSystem.IsMacOS())
        {
            string[] brew =
            {
                "/opt/homebrew/opt/ffmpeg@7/bin/ffmpeg",
                "/usr/local/opt/ffmpeg@7/bin/ffmpeg",
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
            };
            foreach (var path in brew)
                if (File.Exists(path)) return path;
        }
        return "ffmpeg";
    }
}
