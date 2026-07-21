using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Recording;
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
        app.MapGet("/api/v1/recordings", async (string? cameraId, int? limit, HttpContext ctx, CancellationToken ct) =>
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

            var names = await LoadCameraNamesAsync(ctx, ct);
            var recordings = (await repo.ListAsync(filter, ct))
                // Same rule as everywhere else: a camera outside the caller's
                // subset doesn't exist for them, and neither does its archive.
                .Where(r => ctx.CanSeeCamera(r.CameraId.ToString()))
                .OrderByDescending(r => r.StartedAt)
                .Take(limit is > 0 and <= 500 ? limit.Value : 200)
                .Select(r => Describe(r, names))
                .ToList();

            return Results.Json(recordings);
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
