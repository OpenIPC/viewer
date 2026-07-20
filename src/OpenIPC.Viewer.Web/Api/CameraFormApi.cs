using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// Server-rendered form-post handlers for the camera editor UI. Plain HTML forms
// post here (distinct paths from the page routes to avoid the Blazor endpoint
// also matching POST); we validate, call the directory service, and redirect.
// Origin-checked via UseWebAuth; antiforgery disabled (same rationale as login).
public static class CameraFormApi
{
    public static void MapCameraFormEndpoints(this WebApplication app)
    {
        app.MapPost("/app/cameras/create", async (HttpContext ctx, CancellationToken ct) =>
        {
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null) return BackendUnavailable();

            var form = await ctx.Request.ReadFormAsync(ct);
            if (!FromForm(form).TryValidate(out var valid, out _))
                return Results.Redirect("/app/cameras/new?error=1");

            var id = await dir.AddAsync(valid.ToNew(), ct);
            Audit(ctx, "camera.create", id);
            return Results.Redirect("/app/cameras");
        }).DisableAntiforgery();

        app.MapPost("/app/cameras/{id}/update", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null) return BackendUnavailable();
            if (!Guid.TryParse(id, out var guid)) return ValidationError("invalid camera id");

            var form = await ctx.Request.ReadFormAsync(ct);
            if (!FromForm(form).TryValidate(out var valid, out _))
                return Results.Redirect($"/app/cameras/{id}/edit?error=1");

            try { await dir.UpdateAsync(new CameraId(guid), valid.ToUpdate(), ct); }
            catch (InvalidOperationException) { return NotFound(); }

            Audit(ctx, "camera.update", new CameraId(guid));
            return Results.Redirect("/app/cameras");
        }).DisableAntiforgery();

        app.MapPost("/app/cameras/{id}/delete", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var dir = ctx.RequestServices.GetService<CameraDirectoryService>();
            if (dir is null) return BackendUnavailable();
            if (!Guid.TryParse(id, out var guid)) return ValidationError("invalid camera id");

            var cameraId = new CameraId(guid);
            if (await dir.GetAsync(cameraId, ct) is not null)
            {
                await dir.RemoveAsync(cameraId, ct);
                Audit(ctx, "camera.delete", cameraId);
            }
            return Results.Redirect("/app/cameras");
        }).DisableAntiforgery();
    }

    private static CameraWriteRequest FromForm(IFormCollection f) => new(
        Name: f["name"],
        Host: f["host"],
        HttpPort: ParseInt(f["httpPort"]),
        OnvifPort: ParseInt(f["onvifPort"]),
        RtspMain: f["rtspMain"],
        RtspSub: NullIfEmpty(f["rtspSub"]),
        GroupId: ParseInt(f["groupId"]),
        Username: NullIfEmpty(f["username"]),
        Password: NullIfEmpty(f["password"]),
        StreamQuality: NullIfEmpty(f["streamQuality"]));

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
