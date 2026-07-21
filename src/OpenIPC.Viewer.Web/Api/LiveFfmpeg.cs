using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        var args = new List<string> { "-rtsp_transport", "tcp", "-fflags", "nobuffer", "-i", rtspUrl, "-an" };
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
