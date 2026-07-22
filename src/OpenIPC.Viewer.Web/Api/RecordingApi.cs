using System;
using System.Collections.Generic;
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
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// The recordings the desktop head wrote, listed and played back in the browser.
//
// Playback is a plain ranged file response, not a stream we re-encode: the
// recorder writes fragmented MP4 with the camera's own H.264, which every
// browser plays natively, and `enableRangeProcessing` gives the <video> element
// real seeking for free. H.265 recordings are the exception — the DTO says so
// and the UI offers a download instead of pretending it will play.
//
// Only files that are indexed in the database can be served: the id is looked
// up and the path comes from the row, so no request can reach an arbitrary file.
public static class RecordingApi
{
    public static void MapRecordingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/recordings", async (
            string? cameraId, string? from, string? to, int? offset, int? limit,
            HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<IRecordingRepository>();
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
            var matching = (await repo.ListAsync(filter, ct))
                // Same rule as everywhere else: a camera outside the caller's
                // subset doesn't exist for them, and neither does its archive.
                .Where(r => ctx.CanSeeCamera(r.CameraId.ToString()))
                .Where(r => InRange(r.StartedAt, since, until))
                .OrderByDescending(r => r.StartedAt)
                .ToList();

            // Paged rather than capped: the old 200-row ceiling silently hid the
            // rest of the archive, which is worse than making the client ask for
            // the next page. Total travels with the page so the UI can say where
            // it is.
            var take = limit is > 0 and <= MaxPageSize ? limit.Value : DefaultPageSize;
            var skip = offset is > 0 ? offset.Value : 0;
            return Results.Json(new
            {
                total = matching.Count,
                offset = skip,
                limit = take,
                items = matching.Skip(skip).Take(take).Select(r => Describe(r, names)).ToList(),
            });
        });

        app.MapGet("/api/v1/recordings/{id}/stream", async (
            string id, bool? download, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var (recording, error) = await ResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            var path = recording!.FilePath;
            if (!File.Exists(path))
                return Results.Json(new { error = "file_missing" }, statusCode: StatusCodes.Status410Gone);

            var stream = File.OpenRead(path);
            return download == true
                ? Results.File(stream, "video/mp4", Path.GetFileName(path), enableRangeProcessing: true)
                : Results.File(stream, "video/mp4", enableRangeProcessing: true);
        });

        // Just the timestamps, for drawing a calendar.
        //
        // Aggregation happens in the browser, not here: which day a recording
        // belongs to depends on the VIEWER's time zone, and a web client may sit
        // in a different one than the server. Core's CalendarActivity does the
        // same job for the desktop, where the two are the same machine — its own
        // comment warns that mixing zones is the classic bug, so the boundary is
        // drawn where the answer is unambiguous.
        app.MapGet("/api/v1/recordings/calendar", async (
            string? cameraId, string? from, string? to, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<IRecordingRepository>();
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

            var days = (await repo.ListAsync(filter, ct))
                .Where(r => ctx.CanSeeCamera(r.CameraId.ToString()))
                .Where(r => InRange(r.StartedAt, since, until))
                .OrderBy(r => r.StartedAt)
                .Take(CalendarPointCap)
                .Select(r => new { startedAt = r.StartedAt, sizeBytes = r.SizeBytes })
                .ToList();

            return Results.Json(days);
        });

        // Start / stop, plus who is recording right now so the UI can show it
        // without polling every camera.
        app.MapGet("/api/v1/recordings/active", (HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.ViewArchive) is { } denied)
                return denied;
            var recorder = ctx.RequestServices.GetService<WebRecorder>();
            if (recorder is null)
                return BackendUnavailable();
            return Results.Json(recorder.ActiveCameraIds.Where(ctx.CanSeeCamera).ToList());
        });

        app.MapPost("/api/v1/cameras/{id}/recording/start", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            // Recording writes to the host's disk until someone stops it, so it
            // sits with the other install-changing operations rather than with
            // plain viewing.
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            if (ctx.DenyCamera(id) is { } hidden)
                return hidden;

            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            var recorder = ctx.RequestServices.GetService<WebRecorder>();
            if (dir is null || recorder is null)
                return BackendUnavailable();
            if (!Guid.TryParse(id, out var guid))
                return ValidationError("invalid camera id");

            var camera = await dir.GetAsync(new CameraId(guid), ct);
            if (camera is null)
                return NotFound();

            var credentials = await dir.GetCredentialsAsync(camera.Id, ct);
            var started = await recorder.StartAsync(camera, BuildRtspUrl(camera.RtspMainUri, credentials), ct);
            if (started is null)
                return Results.Json(new { error = "already_recording" }, statusCode: StatusCodes.Status409Conflict);

            Audit(ctx, "recording.start", started.Id);
            return Results.Json(new { id = started.Id.ToString(), startedAt = started.StartedAt });
        });

        app.MapPost("/api/v1/cameras/{id}/recording/stop", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            if (ctx.DenyCamera(id) is { } hidden)
                return hidden;

            var recorder = ctx.RequestServices.GetService<WebRecorder>();
            if (recorder is null)
                return BackendUnavailable();

            var wasRecording = recorder.IsRecording(id);
            var stopped = await recorder.StopAsync(id, ct);
            if (stopped is null)
            {
                // Recording stopped either way; the distinction is whether the
                // camera actually gave us anything to keep.
                return wasRecording
                    ? Results.Json(new { error = "nothing_recorded" }, statusCode: StatusCodes.Status502BadGateway)
                    : Results.Json(new { error = "not_recording" }, statusCode: StatusCodes.Status409Conflict);
            }

            Audit(ctx, "recording.stop", stopped.Id);
            return Results.Json(new { id = stopped.Id.ToString(), sizeBytes = stopped.SizeBytes });
        });

        // Cut a fragment out of a recording and hand it back as a file.
        //
        // Nothing is stored: the clip is what the person asked for, and keeping a
        // copy on the server would grow the archive behind their back. Output is
        // fragmented MP4 straight down the response, because a pipe can't be
        // rewound to write a normal trailer.
        app.MapGet("/api/v1/recordings/{id}/export", async (
            string id, double? start, double? end, bool? precise, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Export) is { } denied)
                return denied;
            var (recording, error) = await ResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;
            if (!File.Exists(recording!.FilePath))
                return Results.Json(new { error = "file_missing" }, statusCode: StatusCodes.Status410Gone);

            var from = Math.Max(0, start ?? 0);
            var to = end ?? 0;
            if (to <= from)
                return ValidationError("end must be greater than start");
            var duration = Math.Min(to - from, MaxExportSeconds);

            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Export");
            var name = Path.GetFileNameWithoutExtension(recording.FilePath)
                + $"_{(int)from}-{(int)(from + duration)}.mp4";

            var clip = await ClipAsync(recording.FilePath, from, duration, precise == true, logger, ct);
            if (clip is null || clip.Length == 0)
                return Results.Json(new { error = "export_failed" }, statusCode: StatusCodes.Status502BadGateway);

            Audit(ctx, "recording.export", $"{recording.Id} {(int)from}+{(int)duration}s");
            return Results.File(clip, "video/mp4", name);
        });

        app.MapDelete("/api/v1/recordings/{id}", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            // Deleting footage is destructive and irreversible — management only,
            // never a plain archive viewer.
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var (recording, error) = await ResolveAsync(ctx, id, ct);
            if (error is not null)
                return error;

            var repo = ctx.RequestServices.GetRequiredService<IRecordingRepository>();
            // Drop the row first: a file we failed to delete is a leftover, but a
            // row pointing at a deleted file is a broken entry in every client.
            await repo.RemoveAsync(recording!.Id, ct);
            try
            {
                if (File.Exists(recording.FilePath)) File.Delete(recording.FilePath);
            }
            catch (IOException) { /* locked by an in-flight write — the row is gone either way */ }

            Audit(ctx, "recording.delete", recording.Id);
            return Results.NoContent();
        });
    }

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    // An hour is plenty for "send me this bit"; without a ceiling a stray request
    // could ask the server to re-encode an entire day.
    private const double MaxExportSeconds = 3600;

    // Same two ffmpeg shapes the desktop's clip exporter uses: a fast stream copy
    // that can only start on a keyframe (so the cut lands at or before the asked
    // point), or a re-encode when the person wants the exact frame.
    private static async Task<byte[]?> ClipAsync(
        string sourcePath, double start, double duration, bool precise, ILogger logger, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(LiveFfmpeg.ResolveExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var startSec = start.ToString("0.###", CultureInfo.InvariantCulture);
        var durSec = duration.ToString("0.###", CultureInfo.InvariantCulture);
        void Arg(params string[] args) { foreach (var a in args) psi.ArgumentList.Add(a); }

        Arg("-hide_banner");
        if (precise)
        {
            // Seek after opening so the re-encode starts on the exact frame.
            Arg("-i", sourcePath, "-ss", startSec, "-t", durSec,
                "-c:v", "libopenh264", "-b:v", "2M", "-c:a", "copy");
        }
        else
        {
            Arg("-ss", startSec, "-i", sourcePath, "-t", durSec, "-c", "copy");
        }
        // empty_moov/frag_keyframe: the header can't be rewritten on a pipe.
        Arg("-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1");

        System.Diagnostics.Process? proc = null;
        try
        {
            proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;
            _ = LiveFfmpeg.DrainStderrAsync(proc, logger);
            using var buffer = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            await proc.WaitForExitAsync(ct);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Clip export of {Path} failed", sourcePath);
            return null;
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            proc?.Dispose();
        }
    }

    // A month of a busy install is still small; the cap only stops a pathological
    // range from serialising the entire archive.
    private const int CalendarPointCap = 5000;

    private static bool TryParseRange(string? from, string? to, out DateTime? since, out DateTime? until)
    {
        since = until = null;
        if (!string.IsNullOrEmpty(from))
        {
            if (!DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return false;
            since = parsed;
        }
        if (!string.IsNullOrEmpty(to))
        {
            if (!DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return false;
            until = parsed;
        }
        return true;
    }

    // Inclusive lower bound, exclusive upper — so a caller can ask for one day
    // with [midnight, next midnight) and not double-count the boundary.
    private static bool InRange(DateTime startedAtUtc, DateTime? since, DateTime? until) =>
        (since is null || startedAtUtc >= since) && (until is null || startedAtUtc < until);

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

    private static async Task<(Recording? Recording, IResult? Error)> ResolveAsync(
        HttpContext ctx, string id, CancellationToken ct)
    {
        var repo = ctx.RequestServices.GetService<IRecordingRepository>();
        if (repo is null)
            return (null, BackendUnavailable());
        if (!Guid.TryParse(id, out var guid))
            return (null, ValidationError("invalid recording id"));

        // The repository has no get-by-id, so find it in the indexed set. Fine at
        // self-host scale, and it keeps the Core contract untouched.
        var all = await repo.ListAsync(null, ct);
        var recording = all.FirstOrDefault(r => r.Id.Value == guid);
        if (recording is null || !ctx.CanSeeCamera(recording.CameraId.ToString()))
            return (null, NotFound());

        return (recording, null);
    }

    private static async Task<Dictionary<string, string>> LoadCameraNamesAsync(HttpContext ctx, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cameras = ctx.RequestServices.GetService<ICameraRepository>();
        if (cameras is not null)
        {
            foreach (var camera in await cameras.GetAllAsync(ct))
                names[camera.Id.ToString()] = camera.Name;
        }
        return names;
    }

    private static object Describe(Recording r, IReadOnlyDictionary<string, string> cameraNames)
    {
        var cameraId = r.CameraId.ToString();
        return new
        {
            id = r.Id.ToString(),
            cameraId,
            cameraName = cameraNames.TryGetValue(cameraId, out var name) ? name : null,
            fileName = Path.GetFileName(r.FilePath),
            startedAt = r.StartedAt,
            endedAt = r.EndedAt,
            durationSeconds = r.EndedAt is { } end ? (int)(end - r.StartedAt).TotalSeconds : (int?)null,
            sizeBytes = r.SizeBytes,
            codec = r.Codec,
            hasMotion = r.HasMotion,
            // H.265 in an MP4 plays in Safari and nowhere else worth relying on;
            // tell the client rather than letting the <video> fail silently.
            playable = !(r.Codec?.Contains("hevc", StringComparison.OrdinalIgnoreCase) == true
                || r.Codec?.Contains("265", StringComparison.Ordinal) == true),
        };
    }
}
