using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.Web.Api;

// Read-only camera endpoints (§20.3). Behind the auth guard (path under
// /api/v1). The backend is resolved per-request and may be absent (e.g. a host
// started without AddWebBackend) — in that case we answer 503 rather than throw.
public static class CameraApi
{
    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cameras", async (HttpContext ctx, CancellationToken ct) =>
        {
            var repo = ctx.RequestServices.GetService<ICameraRepository>();
            if (repo is null)
                return Results.Json(new { error = "backend_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var cameras = await repo.GetAllAsync(ct);

            var groupNames = new Dictionary<int, string>();
            var groups = ctx.RequestServices.GetService<IGroupRepository>();
            if (groups is not null)
            {
                foreach (var g in await groups.GetAllAsync(ct))
                    groupNames[g.Id.Value] = g.Name;
            }

            var dtos = cameras
                .Select(c =>
                {
                    var name = c.GroupId is { } gid && groupNames.TryGetValue(gid.Value, out var n) ? n : null;
                    return CameraDto.From(c, name);
                })
                .ToList();

            return Results.Json(dtos);
        });
    }
}
