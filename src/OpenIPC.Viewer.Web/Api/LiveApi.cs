using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.Web.Api;

// Live video over WebSocket (Phase 20 §20.4, spike). ffmpeg remuxes the camera's
// RTSP H.264 into fragmented MP4 (no re-encode) straight to stdout, and we pump
// those bytes to the browser, which plays them via Media Source Extensions.
// H.265 cameras won't play in most browsers over MSE — a MJPEG fallback and/or
// transcode is future work.
public static class LiveApi
{
    public static void MapLiveEndpoints(this WebApplication app)
    {
        app.Map("/api/v1/cameras/{id}/live", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (!Guid.TryParse(id, out var guid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var camera = await dir.GetAsync(new CameraId(guid), ct);
            if (camera is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
            var rtspUrl = BuildRtspUrl(camera.RtspMainUri, credentials);

            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Live");
            await PumpAsync(socket, rtspUrl, logger, ct);
        });
    }

    // Injects credentials into the RTSP URL for ffmpeg (they live in the secrets
    // store, not the entity's URI). Never surfaced back to any client.
    private static string BuildRtspUrl(Uri baseUri, CameraCredentials? credentials)
    {
        if (credentials is null || string.IsNullOrEmpty(credentials.Username))
            return baseUri.ToString();
        return new UriBuilder(baseUri)
        {
            UserName = credentials.Username,
            Password = credentials.Password,
        }.Uri.ToString();
    }

    private static async Task PumpAsync(WebSocket socket, string rtspUrl, ILogger logger, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            proc = StartFfmpeg(rtspUrl);
            _ = DrainStderrAsync(proc, logger);

            var stdout = proc.StandardOutput.BaseStream;
            var buffer = new byte[32 * 1024];
            int read;
            while (socket.State == WebSocketState.Open &&
                   (read = await stdout.ReadAsync(buffer, ct)) > 0)
            {
                await socket.SendAsync(
                    buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A closed socket makes SendAsync throw — that's the normal "viewer
            // navigated away" path, so keep it at debug.
            logger.LogDebug(ex, "live pump ended for {Rtsp}", Redact(rtspUrl));
        }
        finally
        {
            KillQuietly(proc);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream ended", CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
    }

    private static Process StartFfmpeg(string rtspUrl)
    {
        var psi = new ProcessStartInfo(ResolveFfmpeg())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-rtsp_transport", "tcp",
            "-fflags", "nobuffer",
            "-i", rtspUrl,
            "-an",
            "-c:v", "copy",
            "-f", "mp4",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-flush_packets", "1",
            "pipe:1",
        })
        {
            psi.ArgumentList.Add(arg);
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg");
    }

    private static async Task DrainStderrAsync(Process proc, ILogger logger)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                logger.LogTrace("ffmpeg: {Line}", line);
        }
        catch { /* process gone */ }
    }

    private static void KillQuietly(Process? proc)
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
    private static string ResolveFfmpeg()
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

    private static string Redact(string rtspUrl) =>
        Uri.TryCreate(rtspUrl, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.UserInfo)
            ? rtspUrl.Replace(u.UserInfo + "@", "***@", StringComparison.Ordinal)
            : rtspUrl;
}
