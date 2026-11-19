namespace OpenIPC.Viewer.Core.Video;

// Where to look for the ffmpeg CLI on macOS, which ships no bundled binary.
// Homebrew's keg-only ffmpeg@7 isn't on PATH, and a Finder-launched app
// doesn't see /opt/homebrew/bin either. Shared by the Video subprocess
// recorder/exporter and the web live remuxer, which don't reference each other.
public static class FfmpegExecutablePaths
{
    public static readonly string[] MacOs =
    {
        "/opt/homebrew/opt/ffmpeg@7/bin/ffmpeg", // Apple Silicon Homebrew
        "/usr/local/opt/ffmpeg@7/bin/ffmpeg",    // Intel Homebrew
        "/opt/homebrew/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
    };
}
