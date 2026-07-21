using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web.Api;

// Named Live-grid layouts (Phase 19.1 model, exposed to the web in Phase 21).
// A layout owns its grid size and an ordered, compact list of camera tiles
// (position = index). Same auth/Origin guards as the other API surface; backend
// may be absent (503). Deferred from Phase 20 §20.3 — the repo was already wired.
public static class LayoutApi
{
    // Grid size is the side N of an NxN grid (N cells per row; N*N total), matching
    // the desktop model. 1..5 → 1, 4, 9, 16, 25 cells. Anything else is coerced.
    private static readonly int[] AllowedSizes = { 1, 2, 3, 4, 5 };

    public static void MapLayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/layouts", async (HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<ILayoutRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();

            var layouts = await repo.GetAllAsync(ct);
            var list = new List<LayoutDto>(layouts.Count);
            foreach (var l in layouts)
                list.Add(LayoutDto.From(l, await repo.GetTilesAsync(l.Id, ct)));
            return Results.Json(list);
        });

        app.MapPost("/api/v1/layouts", async (LayoutWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<ILayoutRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var name = body?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return ApiHelpers.ValidationError("name is required");

            var id = await repo.AddAsync(name, NormalizeSize(body!.GridSize), sortOrder: 0, ct);
            ApiHelpers.Audit(ctx, "layout.create", id.Value);
            var created = (await repo.GetAllAsync(ct)).FirstOrDefault(x => x.Id == id);
            return Results.Created($"/api/v1/layouts/{id.Value}",
                created is null ? null : LayoutDto.From(created, Array.Empty<CameraId>()));
        });

        app.MapPut("/api/v1/layouts/{id:int}", async (int id, LayoutWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<ILayoutRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var lid = new LayoutId(id);
            if (await FindAsync(repo, lid, ct) is null)
                return ApiHelpers.NotFound();

            if (!string.IsNullOrWhiteSpace(body?.Name))
                await repo.RenameAsync(lid, body.Name.Trim(), ct);
            if (body?.GridSize is { } gs)
                await repo.SetGridSizeAsync(lid, NormalizeSize(gs), ct);

            ApiHelpers.Audit(ctx, "layout.update", id);
            var updated = await FindAsync(repo, lid, ct);
            return updated is null
                ? ApiHelpers.NotFound()
                : Results.Json(LayoutDto.From(updated, await repo.GetTilesAsync(lid, ct)));
        });

        app.MapDelete("/api/v1/layouts/{id:int}", async (int id, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<ILayoutRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var lid = new LayoutId(id);
            if (await FindAsync(repo, lid, ct) is null)
                return ApiHelpers.NotFound();

            await repo.RemoveAsync(lid, ct);
            ApiHelpers.Audit(ctx, "layout.delete", id);
            return Results.NoContent();
        });

        // Replace a layout's tile membership with an ordered, compact list of
        // camera ids (position = index). Unknown ids are rejected; the repo drops
        // ids for cameras that no longer exist on read.
        app.MapPut("/api/v1/layouts/{id:int}/tiles", async (int id, LayoutTilesRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var repo = ctx.RequestServices.GetService<ILayoutRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var lid = new LayoutId(id);
            if (await FindAsync(repo, lid, ct) is null)
                return ApiHelpers.NotFound();

            var ids = new List<CameraId>();
            foreach (var s in body?.Cameras ?? Array.Empty<string>())
            {
                if (!Guid.TryParse(s, out var guid))
                    return ApiHelpers.ValidationError($"invalid camera id: {s}");
                ids.Add(new CameraId(guid));
            }

            await repo.SetTilesAsync(lid, ids, ct);
            ApiHelpers.Audit(ctx, "layout.tiles", $"{id}:{ids.Count}");
            var layout = await FindAsync(repo, lid, ct);
            return layout is null
                ? ApiHelpers.NotFound()
                : Results.Json(LayoutDto.From(layout, await repo.GetTilesAsync(lid, ct)));
        });
    }

    private static async System.Threading.Tasks.Task<GridLayout?> FindAsync(
        ILayoutRepository repo, LayoutId id, CancellationToken ct) =>
        (await repo.GetAllAsync(ct)).FirstOrDefault(x => x.Id == id);

    private static int NormalizeSize(int? size) =>
        size is { } s && Array.IndexOf(AllowedSizes, s) >= 0 ? s : 2;
}

public sealed record LayoutDto(int Id, string Name, int GridSize, int SortOrder, IReadOnlyList<string> Tiles)
{
    public static LayoutDto From(GridLayout l, IReadOnlyList<CameraId> tiles) =>
        new(l.Id.Value, l.Name, l.GridSize, l.SortOrder, tiles.Select(c => c.Value.ToString()).ToList());
}

public sealed record LayoutWriteRequest(string? Name, int? GridSize);

public sealed record LayoutTilesRequest(IReadOnlyList<string>? Cameras);
