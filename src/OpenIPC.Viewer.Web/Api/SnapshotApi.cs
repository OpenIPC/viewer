using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// A still from a camera, on demand.
//
// Two sources, in the desktop's order of preference: Majestic's own
// /image.jpg (the camera encodes it, so it costs us nothing and comes at
// sensor resolution), falling back to one short ffmpeg pull off the RTSP
// stream for cameras that aren't Majestic or have the HTTP server off.
//
// Nothing is stored: this hands the browser the bytes, which is what "take a
// snapshot" means from a web client. The shared snapshot library the desktop
// keeps (indexed, thumbnailed, browsable) is a separate slice — it needs the
// image/thumbnail stack the server head doesn't compose yet.
public static class SnapshotApi
{
    private static readonly TimeSpan MajesticTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(15);

    public static void MapSnapshotEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cameras/{id}/snapshot", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            // Seeing a still is seeing the camera — same permission as live.
            if (ctx.Deny(WebPermission.ViewLive) is { } denied)
                return denied;
            if (ctx.DenyCamera(id) is { } hidden)
                return hidden;

            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null)
                return BackendUnavailable();
            if (!Guid.TryParse(id, out var guid))
                return ValidationError("invalid camera id");

            var camera = await dir.GetAsync(new CameraId(guid), ct);
            if (camera is null)
                return NotFound();

            var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Snapshot");

            var (jpeg, _) = await CaptureAsync(ctx, camera, credentials, logger, ct);

            if (jpeg is null || jpeg.Length == 0)
                return Results.Json(new { error = "snapshot_failed" }, statusCode: StatusCodes.Status502BadGateway);

            // No caching: a still is a point in time, and the next request means
            // the viewer wants a fresh one.
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(jpeg, "image/jpeg");
        });
    }

    // One capture path for both callers — the transient still above and the
    // kept one in SnapshotLibraryApi — so the library never ends up with a
    // different (worse) picture than the preview the viewer just approved. The
    // source travels with the bytes because the row records it: an HD-always
    // snapshot must never claim it came off the substream.
    internal static async Task<(byte[]? Jpeg, SnapshotSource Source)> CaptureAsync(
        HttpContext ctx, Camera camera, CameraCredentials? credentials, ILogger logger, CancellationToken ct)
    {
        var majestic = await TryMajesticAsync(ctx, camera, credentials, logger, ct);
        if (majestic is { Length: > 0 })
            return (majestic, SnapshotSource.HttpSnapshot);

        // ffmpeg pulls one keyframe off the mainstream — the same thing the
        // desktop calls OpenedStream.
        return (await TryFfmpegAsync(camera, credentials, logger, ct), SnapshotSource.OpenedStream);
    }

    private static async Task<byte[]?> TryMajesticAsync(
        HttpContext ctx, Camera camera, CameraCredentials? credentials, ILogger logger, CancellationToken ct)
    {
        var majestic = ctx.RequestServices.GetService<IMajesticClient>();
        if (majestic is null || !camera.IsMajestic)
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MajesticTimeout);
            var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, credentials);
            return await majestic.SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Majestic snapshot of {Host} failed — falling back to ffmpeg", camera.Host);
            return null;
        }
    }

    // One frame off the mainstream. Writes to stdout so nothing lands on disk,
    // and dies with the request if the camera stops answering mid-pull.
    private static async Task<byte[]?> TryFfmpegAsync(
        Camera camera, CameraCredentials? credentials, ILogger logger, CancellationToken ct)
    {
        var url = BuildRtspUrl(camera.RtspMainUri, credentials);
        var psi = new ProcessStartInfo(LiveFfmpeg.ResolveExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-rtsp_transport", "tcp", "-i", url,
                     "-frames:v", "1", "-q:v", "3",
                     "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FfmpegTimeout);
            proc = Process.Start(psi);
            if (proc is null)
                return null;

            _ = LiveFfmpeg.DrainStderrAsync(proc, logger);
            using var buffer = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ffmpeg snapshot of {Host} failed", camera.Host);
            return null;
        }
        finally
        {
            try
            {
                if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            proc?.Dispose();
        }
    }

    // Credentials live in the secrets store, not in the stored URI.
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
}
