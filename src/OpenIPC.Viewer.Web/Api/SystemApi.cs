using System.IO;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web.Api;

// System/admin endpoints: status, config export/import, session revocation.
// All under /api/v1, so the auth guard and the Origin check on mutations apply.
public static class SystemApi
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Read-only status for the SPA System page: version + live counts.
        // Counts fall back to 0 without a backend.
        app.MapGet("/api/v1/system", async (HttpContext ctx, CancellationToken ct) =>
        {
            var cameras = ctx.RequestServices.GetService<ICameraRepository>();
            var groups = ctx.RequestServices.GetService<IGroupRepository>();
            var sessions = ctx.RequestServices.GetRequiredService<SessionStore>();
            var hub = ctx.RequestServices.GetService<LiveStreamHub>();
            return Results.Json(new
            {
                version = WebServer.Version,
                cameras = cameras is null ? 0 : (await cameras.GetAllAsync(ct)).Count,
                groups = groups is null ? 0 : (await groups.GetAllAsync(ct)).Count,
                sessions = sessions.ActiveCount,
                streams = hub?.ActiveStreamCount ?? 0,
            });
        });

        // Download a browser-safe config backup (passphrase null → no camera
        // passwords or secrets are ever written).
        app.MapGet("/api/v1/config/export", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var backup = ctx.RequestServices.GetService<IConfigBackupService>();
            if (backup is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var json = await backup.ExportAsync(credentialPassphrase: null, ct);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Results.File(bytes, "application/json", "openipc-config.json");
        });

        // Multipart upload of a backup file, answering with the applied counts.
        // (Was a form-post + redirect for the Razor UI; the SPA reads the JSON.)
        app.MapPost("/api/v1/config/import", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var backup = ctx.RequestServices.GetService<IConfigBackupService>();
            if (backup is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!ctx.Request.HasFormContentType)
                return ApiHelpers.ValidationError("expected a multipart upload");

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return ApiHelpers.ValidationError("missing file");

            string json;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
                json = await reader.ReadToEndAsync(ct);

            try
            {
                var result = await backup.ImportAsync(json, ct);
                ApiHelpers.Audit(ctx, "config.import", $"+{result.CamerasAdded}/~{result.CamerasUpdated}");
                return Results.Json(new { added = result.CamerasAdded, updated = result.CamerasUpdated });
            }
            catch
            {
                // Malformed or foreign backup — the service's own error detail is
                // not browser-safe, so answer with a plain code.
                return ApiHelpers.ValidationError("import_failed");
            }
        });

        // Kills every session including the caller's; the client then drops its
        // local auth state and returns to the login screen.
        app.MapPost("/api/v1/sessions/revoke-all", (HttpContext ctx, SessionStore store) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var count = store.RevokeAll();
            ApiHelpers.Audit(ctx, "sessions.revoke-all", count);
            AuthApi.ClearSessionCookie(ctx);
            return Results.Json(new { revoked = count });
        });
    }
}
