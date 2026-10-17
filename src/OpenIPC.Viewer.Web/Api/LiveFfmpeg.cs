using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenIPC.Viewer.Web.Api;

// Spawns and manages the ffmpeg process that remuxes/transcodes a camera's RTSP
// into fragmented MP4 on stdout. Shared by the fan-out hub.
internal static class LiveFfmpeg
{
    public static Process Start(string rtspUrl, bool transcode)
    {
        var psi = new ProcessStartInfo(Resolve())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // RTSP socket I/O timeout (µs) so a silent camera ends the process
        // instead of holding a connection open forever.
        var args = new List<string> { "-rtsp_transport", "tcp", RtspTimeoutFlag(), RtspTimeoutMicros, "-fflags", "nobuffer", "-i", rtspUrl };

        // Audio rides along in the same stream instead of getting its own ffmpeg
        // session: AAC at 64 kbps is around a percent of the video bitrate, so
        // carrying it for every viewer is cheaper than a second process for the
        // one who unmutes — and nobody's picture restarts when they do. Tiles
        // start muted; the browser needs that for autoplay anyway.
        //
        // Always re-encoded rather than copied: cameras emit G.711/PCM/Opus more
        // often than AAC, and only AAC plays in MSE. With no audio track on the
        // input ffmpeg simply ignores these.
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "64k", "-ac", "1" });
        if (transcode)
        {
            // Software H.264 (libopenh264 — the LGPL build has no libx264). Output
            // is Constrained Baseline, which every MSE browser accepts. Only used
            // for codecs the browser can't play directly (e.g. H.265).
            args.AddRange(new[] { "-c:v", "libopenh264", "-b:v", "2M", "-g", "30", "-pix_fmt", "yuv420p" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "copy" });
        }
        args.AddRange(new[] { "-f", "mp4", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-flush_packets", "1", "pipe:1" });

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg");
    }

    public static async Task DrainStderrAsync(Process proc, ILogger logger)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                logger.LogTrace("ffmpeg: {Line}", line);
        }
        catch { /* process gone */ }
    }

    public static void Kill(Process? proc)
    {
        try
        {
            if (proc is { HasExited: false })
                proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        proc?.Dispose();
    }

    // Same bundled-then-PATH resolution as FfmpegSubprocessRecorder.
    // Also used by the snapshot endpoint, which spawns its own one-shot ffmpeg.
    public static string ResolveExecutable() => Resolve();

    public const string RtspTimeoutMicros = "10000000";

    private static string? _rtspTimeoutFlag;

    // "-timeout" is the RTSP socket timeout from FFmpeg 5 on; in 4.x (the apt
    // package on Ubuntu 22.04 / Debian 11) it means "listen for a connection"
    // and the socket timeout is still "-stimeout". Probed once per process.
    public static string RtspTimeoutFlag() => _rtspTimeoutFlag ??= ProbeMajorVersion(Resolve()) is int major and < 5 ? "-stimeout" : "-timeout";

    private static int? ProbeMajorVersion(string ffmpeg)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(ffmpeg)
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
            // "ffmpeg version 4.4.2-0ubuntu0.22.04.1", "ffmpeg version n7.1-…";
            // git snapshots ("N-112233-g…") don't match and count as current.
            var m = Regex.Match(stdout.Result, @"^ffmpeg version n?(\d+)\.\d+",
                RegexOptions.Multiline);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Resolve()
    {
        string? rid = null;
        var exe = "ffmpeg";
        if (OperatingSystem.IsWindows()) { rid = "win-x64"; exe = "ffmpeg.exe"; }
        else if (OperatingSystem.IsLinux()) { rid = "linux-x64"; }
        else if (OperatingSystem.IsMacOS()) { rid = "osx-x64"; }

        if (rid is not null)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", exe);
            if (File.Exists(bundled))
                return bundled;
        }
        return "ffmpeg";
    }
}
