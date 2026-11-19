using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenIPC.Viewer.Video.Recording;

// Options whose spelling depends on which ffmpeg binary we shell out to. The
// bundled one is 7.x, but the PATH fallback can be a distro build (4.4 on
// Ubuntu 22.04, 4.3 on Debian 11).
internal static partial class FfmpegCliOptions
{
    private static readonly ConcurrentDictionary<string, string> TimeoutFlags = new(StringComparer.Ordinal);

    // "ffmpeg version 4.4.2-0ubuntu0.22.04.1", "ffmpeg version n7.1-…". Git
    // snapshots ("N-112233-g…") don't match and count as current.
    [GeneratedRegex(@"^ffmpeg version n?(\d+)\.\d+", RegexOptions.Multiline)]
    private static partial Regex VersionRegex();

    // The RTSP socket I/O timeout is "-timeout" from FFmpeg 5 on. In 4.x that
    // name means "listen for an incoming connection" and the old "-stimeout"
    // is the socket timeout, so passing the new name would turn the client
    // into a server.
    public static string RtspTimeoutFlag(string ffmpegPath) =>
        TimeoutFlags.GetOrAdd(ffmpegPath, static path => MajorVersion(path) is int major and < 5 ? "-stimeout" : "-timeout");

    private static int? MajorVersion(string ffmpegPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(ffmpegPath)
            {
                ArgumentList = { "-hide_banner", "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            var m = VersionRegex().Match(stdout.Result);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                ? v
                : null;
        }
        catch
        {
            // Missing binary or no permission: the recorder reports that itself.
            return null;
        }
    }
}
