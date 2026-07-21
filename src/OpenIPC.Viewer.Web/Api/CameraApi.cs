using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// Camera CRUD (§20.3). All paths sit under /api/v1 so the auth guard requires a
// bearer token; mutations additionally pass the Origin check. Writes go through
// CameraDirectoryService (credentials land in the secrets store, never the DB
// row / API), and each mutation writes an audit line. The backend is resolved
// per-request and may be absent (503).
public static class CameraApi
{
    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cameras", async (HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<ICameraRepository>();
            if (repo is null)
                return BackendUnavailable();

            if (ctx.Deny(WebPermission.ViewLive) is { } denied)
                return denied;

            var cameras = await repo.GetAllAsync(ct);
            var groupNames = await LoadGroupNamesAsync(ctx, ct);
            // A restricted user simply never learns the others exist.
            var dtos = cameras
                .Where(c => ctx.CanSeeCamera(c.Id.ToString()))
                .Select(c => CameraDto.From(c, GroupName(c, groupNames)))
                .ToList();
            return Results.Json(dtos);
        });

        app.MapPost("/api/v1/cameras", async (CameraWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null)
                return BackendUnavailable();
            if (body is null)
                return ValidationError(new List<string> { "missing body" });
            if (!body.TryValidate(out var valid, out var errors))
                return ValidationError(errors);

            var id = await dir.AddAsync(valid.ToNew(), ct);
            Audit(ctx, "camera.create", id);

            var created = await dir.GetAsync(id, ct);
            var dto = created is null ? null : CameraDto.From(created, null);
            return Results.Created($"/api/v1/cameras/{id}", dto);
        });

        app.MapPut("/api/v1/cameras/{id}", async (string id, CameraWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null)
                return BackendUnavailable();
            if (!TryParseId(id, out var cameraId))
                return ValidationError(new List<string> { "invalid camera id" });
            if (body is null)
                return ValidationError(new List<string> { "missing body" });
            if (!body.TryValidate(out var valid, out var errors))
                return ValidationError(errors);

            try
            {
                await dir.UpdateAsync(cameraId, valid.ToUpdate(), ct);
            }
            catch (InvalidOperationException)
            {
                return NotFound();
            }

            Audit(ctx, "camera.update", cameraId);
            var updated = await dir.GetAsync(cameraId, ct);
            return updated is null ? NotFound() : Results.Json(CameraDto.From(updated, null));
        });

        app.MapDelete("/api/v1/cameras/{id}", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null)
                return BackendUnavailable();
            if (!TryParseId(id, out var cameraId))
                return ValidationError(new List<string> { "invalid camera id" });

            var existing = await dir.GetAsync(cameraId, ct);
            if (existing is null)
                return NotFound();

            await dir.RemoveAsync(cameraId, ct);
            Audit(ctx, "camera.delete", cameraId);
            return Results.NoContent();
        });
    }

    private static async System.Threading.Tasks.Task<Dictionary<int, string>> LoadGroupNamesAsync(
        HttpContext ctx, CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        var groups = ctx.RequestServices.GetService<IGroupRepository>();
        if (groups is not null)
        {
            foreach (var g in await groups.GetAllAsync(ct))
                names[g.Id.Value] = g.Name;
        }
        return names;
    }

    private static string? GroupName(Camera c, IReadOnlyDictionary<int, string> names) =>
        c.GroupId is { } gid && names.TryGetValue(gid.Value, out var n) ? n : null;

    private static bool TryParseId(string id, out CameraId cameraId)
    {
        if (Guid.TryParse(id, out var guid))
        {
            cameraId = new CameraId(guid);
            return true;
        }
        cameraId = default;
        return false;
    }
}
