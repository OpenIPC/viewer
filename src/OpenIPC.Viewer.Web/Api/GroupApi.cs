using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.Web.Api;

// Camera-group CRUD (§20.3). Same auth/Origin guards as the camera endpoints.
// Deleting a group still referenced by cameras is rejected by the DB FK — we
// surface that as 409 rather than a 500.
public static class GroupApi
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/groups", async (HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<IGroupRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var groups = await repo.GetAllAsync(ct);
            return Results.Json(groups.Select(GroupDto.From).ToList());
        });

        app.MapPost("/api/v1/groups", async (GroupWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<IGroupRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var name = body?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return ApiHelpers.ValidationError("name is required");

            var id = await repo.AddAsync(name, sortOrder: 0, ct);
            ApiHelpers.Audit(ctx, "group.create", id.Value);
            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/v1/groups/{id.Value}", created is null ? null : GroupDto.From(created));
        });

        app.MapPut("/api/v1/groups/{id:int}", async (int id, GroupWriteRequest? body, HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<IGroupRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();
            var name = body?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return ApiHelpers.ValidationError("name is required");

            var gid = new GroupId(id);
            if (await repo.GetAsync(gid, ct) is null)
                return ApiHelpers.NotFound();

            await repo.RenameAsync(gid, name, ct);
            ApiHelpers.Audit(ctx, "group.rename", id);
            var updated = await repo.GetAsync(gid, ct);
            return updated is null ? ApiHelpers.NotFound() : Results.Json(GroupDto.From(updated));
        });

        app.MapDelete("/api/v1/groups/{id:int}", async (int id, HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<IGroupRepository>();
            if (repo is null)
                return ApiHelpers.BackendUnavailable();

            var gid = new GroupId(id);
            if (await repo.GetAsync(gid, ct) is null)
                return ApiHelpers.NotFound();

            try
            {
                await repo.RemoveAsync(gid, ct);
            }
            catch (Exception)
            {
                // The schema references Groups(Id) without ON DELETE, so SQLite
                // rejects removing a group that cameras still point at.
                return Results.Json(new { error = "group_in_use" }, statusCode: StatusCodes.Status409Conflict);
            }

            ApiHelpers.Audit(ctx, "group.delete", id);
            return Results.NoContent();
        });
    }
}

public sealed record GroupDto(int Id, string Name, int SortOrder)
{
    public static GroupDto From(CameraGroup g) => new(g.Id.Value, g.Name, g.SortOrder);
}

public sealed record GroupWriteRequest(string? Name);
