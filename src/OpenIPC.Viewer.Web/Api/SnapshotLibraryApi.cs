using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// The kept snapshots: a still saved into the shared library instead of just
// handed to the browser (SnapshotApi does that).
//
// Storage is deliberately identical to the desktop's SnapshotService —
// <snapshots>/<cameraId>/yyyy-MM-dd_HH-mm-ss.jpg, thumbnail in .thumbs/<id>.jpg,
// one row in the same table — so a still taken from a phone shows up in the
// desktop browser and vice versa. We can't call SnapshotService itself: it
// depends on LiveStreamCoordinator and the Skia thumbnailer, both in the Video
// layer the server head doesn't compose (no FFmpeg natives). Same split as
// WebRecorder, and the same rule: match the layout exactly or the two heads
// quietly build separate archives.
//
// The thumbnail is made with ffmpeg, which this head already shells out to for
// snapshots and recording — one existing dependency instead of a new imaging
// one, and it writes where the desktop gallery already looks.
public static class SnapshotLibraryApi
{
    private const int DefaultPageSize = 60;
    private const int MaxPageSize = 200;
    private const int ThumbMaxDim = 320;
    private static readonly TimeSpan ThumbTimeout = TimeSpan.FromSeconds(10);

    public static void MapSnapshotLibraryEndpoints(this WebApplication app)
    {
        // Take a still and keep it. ViewArchive rather than Manage: this adds to
        // the archive the caller can already read, and the picture is one they
        // are already allowed to watch live. Deleting still needs Manage, like
        // recordings.
        app.MapPost("/api/v1/cameras/{id}/snapshots", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            if (ctx.DenyCamera(id) is { } hidden)
                return hidden;

            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            var repo = ctx.RequestServices.GetService<ISnapshotRepository>();
            var fs = ctx.RequestServices.GetService<IFileSystem>();
            if (dir is null || repo is null || fs is null)
                return BackendUnavailable();
            if (!Guid.TryParse(id, out var guid))
                return ValidationError("invalid camera id");

            var camera = await dir.GetAsync(new CameraId(guid), ct);
            if (camera is null)
                return NotFound();

            var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
            var logger = Log(ctx);
            var (jpeg, source) = await SnapshotApi.CaptureAsync(ctx, camera, credentials, logger, ct);
            if (jpeg is null || jpeg.Length == 0)
                return Results.Json(new { error = "snapshot_failed" }, statusCode: StatusCodes.Status502BadGateway);

            var snapshotId = SnapshotId.New();
            var takenAt = DateTime.UtcNow;
            var folder = Path.Combine(fs.SnapshotsDir.FullName, camera.Id.ToString());
            Directory.CreateDirectory(folder);
            var name = takenAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".jpg";
            var path = EnsureUnique(Path.Combine(folder, name));
            await File.WriteAllBytesAsync(path, jpeg, ct);

            var thumbPath = await TryWriteThumbAsync(fs, snapshotId, path, logger, ct);
            var (width, height) = JpegSize(jpeg);

            var snapshot = new Snapshot(
                snapshotId, camera.Id, takenAt, path, thumbPath, width, height, source, SnapshotKind.Manual);
            await repo.AddAsync(snapshot, ct);
            Audit(ctx, "snapshot.save", $"{id}/{snapshotId}");

            return Results.Json(Describe(snapshot, camera.Name));
        });

        app.MapGet("/api/v1/snapshots", async (
            string? cameraId, string? from, string? to, int? offset, int? limit,
            HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<ISnapshotRepository>();
            if (repo is null)
                return BackendUnavailable();

            CameraId? filter = null;
            if (!string.IsNullOrEmpty(cameraId))
            {
                if (!Guid.TryParse(cameraId, out var guid))
                    return ValidationError("invalid camera id");
                filter = new CameraId(guid);
            }
            if (!TryParseRange(from, to, out var since, out var until))
                return ValidationError("from/to must be ISO-8601 timestamps");

            var names = await LoadCameraNamesAsync(ctx, ct);
            // The repository's own limit is a fetch cap, not the page: filtering
            // by the caller's camera subset happens here, so ask for enough rows
            // to page through and slice afterwards.
            var all = (await repo.ListAsync(filter, since, until, MaxFetch, ct))
                .Where(s => ctx.CanSeeCamera(s.CameraId.ToString()))
                .ToList();

            var take = limit is > 0 and <= MaxPageSize ? limit.Value : DefaultPageSize;
            var skip = offset is > 0 ? offset.Value : 0;
            return Results.Json(new
            {
                total = all.Count,
                offset = skip,
                limit = take,
                items = all.Skip(skip).Take(take)
                    .Select(s => Describe(s, names.TryGetValue(s.CameraId, out var n) ? n : null))
                    .ToList(),
            });
        });

        // The image itself. Only paths that came out of the index are served —
        // the id is looked up and the path read off the row, so no request can
        // point at an arbitrary file.
        app.MapGet("/api/v1/snapshots/{id}/image", async (
            string id, bool? thumb, bool? download, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var (snapshot, error) = await ResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            // Fall back to the full image when a thumbnail was never made (an
            // older row, or ffmpeg wasn't there) — a gallery with holes in it is
            // worse than a gallery that decodes a few big files.
            var path = thumb == true && !string.IsNullOrEmpty(snapshot!.ThumbPath) && File.Exists(snapshot.ThumbPath)
                ? snapshot.ThumbPath!
                : snapshot!.Path;
            if (!File.Exists(path))
                return Results.Json(new { error = "file_missing" }, statusCode: StatusCodes.Status410Gone);

            var stream = File.OpenRead(path);
            return download == true
                ? Results.File(stream, "image/jpeg", Path.GetFileName(snapshot.Path))
                : Results.File(stream, "image/jpeg");
        });

        app.MapDelete("/api/v1/snapshots/{id}", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var (snapshot, error) = await ResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            var repo = ctx.RequestServices.GetRequiredService<ISnapshotRepository>();
            // Row first: a file that outlives its row is invisible clutter, a row
            // that outlives its file is a broken thumbnail in everyone's gallery.
            await repo.RemoveAsync(snapshot!.Id, ct);
            TryDelete(snapshot.Path, ctx);
            if (!string.IsNullOrEmpty(snapshot.ThumbPath))
                TryDelete(snapshot.ThumbPath!, ctx);
            Audit(ctx, "snapshot.delete", id);
            return Results.NoContent();
        });
    }

    // How many rows to pull before the caller's camera subset is applied. Well
    // past a usable page count, and the response says `total` either way.
    private const int MaxFetch = 5000;

    private static async Task<(Snapshot? Snapshot, IResult? Error)> ResolveAsync(
        HttpContext ctx, string id, CancellationToken ct)
    {
        var repo = ctx.RequestServices.GetService<ISnapshotRepository>();
        if (repo is null)
            return (null, BackendUnavailable());
        if (!Guid.TryParse(id, out var guid))
            return (null, ValidationError("invalid snapshot id"));

        var snapshot = await repo.GetAsync(new SnapshotId(guid), ct);
        if (snapshot is null)
            return (null, NotFound());
        // A snapshot of a camera outside the caller's subset doesn't exist for
        // them — same rule as the live and recorded surfaces.
        if (!ctx.CanSeeCamera(snapshot.CameraId.ToString()))
            return (null, NotFound());
        return (snapshot, null);
    }

    private static object Describe(Snapshot s, string? cameraName) => new
    {
        id = s.Id.ToString(),
        cameraId = s.CameraId.ToString(),
        cameraName,
        takenAt = s.TakenAt,
        width = s.Width,
        height = s.Height,
        hasThumb = !string.IsNullOrEmpty(s.ThumbPath),
        source = s.Source.ToString(),
    };

    // Downscale with ffmpeg into the same .thumbs/<id>.jpg the desktop gallery
    // reads. Failure is not fatal: the row simply carries no thumbnail, exactly
    // as SnapshotService treats a thumbnailer that throws.
    private static async Task<string?> TryWriteThumbAsync(
        IFileSystem fs, SnapshotId id, string sourcePath, ILogger logger, CancellationToken ct)
    {
        var thumbDir = Path.Combine(fs.SnapshotsDir.FullName, ".thumbs");
        var thumbPath = Path.Combine(thumbDir, id + ".jpg");
        Process? proc = null;
        try
        {
            Directory.CreateDirectory(thumbDir);
            var psi = new ProcessStartInfo(LiveFfmpeg.ResolveExecutable())
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-y", "-i", sourcePath,
                         // Long edge to ThumbMaxDim, short edge computed and kept
                         // even (mjpeg wants even dimensions).
                         "-vf", $"scale=w={ThumbMaxDim}:h={ThumbMaxDim}:force_original_aspect_ratio=decrease",
                         "-q:v", "5", thumbPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ThumbTimeout);
            proc = Process.Start(psi);
            if (proc is null)
                return null;
            _ = LiveFfmpeg.DrainStderrAsync(proc, logger);
            await proc.WaitForExitAsync(timeout.Token);
            return proc.ExitCode == 0 && File.Exists(thumbPath) ? thumbPath : null;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Snapshot thumbnail failed for {Path}", sourcePath);
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

    // Pixel dimensions straight out of the JPEG's frame header. The desktop
    // stores these to prove a capture really was full-resolution, and reading
    // the two bytes is cheaper than decoding the image — which this head has no
    // library to do anyway.
    internal static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        // SOI, then marker segments: 0xFF <marker> <len:2> <payload>. The SOFn
        // markers carry the size; everything else is skipped by its length.
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = jpeg[i + 1];
            // Standalone markers (no length field).
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            // SOF0..SOF15, minus the DHT/JPG/DAC markers interleaved in that range.
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return (width, height);
            }
            if (length <= 0)
                break;
            i += 2 + length;
        }
        return (0, 0);
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path, HttpContext ctx)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log(ctx).LogWarning(ex, "Deleting snapshot file {Path} failed", path);
        }
    }

    private static async Task<Dictionary<CameraId, string>> LoadCameraNamesAsync(HttpContext ctx, CancellationToken ct)
    {
        var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
        if (dir is null)
            return new Dictionary<CameraId, string>();
        var cameras = await dir.ListAsync(ct);
        return cameras.ToDictionary(c => c.Id, c => c.Name);
    }

    private static bool TryParseRange(string? from, string? to, out DateTime? since, out DateTime? until)
    {
        since = null;
        until = null;
        if (!string.IsNullOrEmpty(from))
        {
            if (!DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s))
                return false;
            since = s;
        }
        if (!string.IsNullOrEmpty(to))
        {
            if (!DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
                return false;
            until = u;
        }
        return true;
    }

    private static ILogger Log(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Snapshot");
}
