using System.IO;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web.Api;

// System/admin form-post + download endpoints: config export/import and session
// revocation. Origin-checked via UseWebAuth; antiforgery disabled like the other
// form posts (Origin + SameSite cookie cover CSRF).
public static class SystemApi
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // Download a browser-safe config backup (passphrase null → no camera
        // passwords or secrets are ever written).
        app.MapGet("/app/config/export", async (HttpContext ctx, CancellationToken ct) =>
        {
            var backup = ctx.RequestServices.GetService<IConfigBackupService>();
            if (backup is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var json = await backup.ExportAsync(credentialPassphrase: null, ct);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Results.File(bytes, "application/json", "openipc-config.json");
        });

        app.MapPost("/app/config/import", async (HttpContext ctx, CancellationToken ct) =>
        {
            var backup = ctx.RequestServices.GetService<IConfigBackupService>();
            if (backup is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.Redirect("/app/system?error=1");

            string json;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
                json = await reader.ReadToEndAsync(ct);

            try
            {
                var result = await backup.ImportAsync(json, ct);
                ApiHelpers.Audit(ctx, "config.import", $"+{result.CamerasAdded}/~{result.CamerasUpdated}");
                return Results.Redirect($"/app/system?added={result.CamerasAdded}&updated={result.CamerasUpdated}");
            }
            catch
            {
                return Results.Redirect("/app/system?error=1");
            }
        }).DisableAntiforgery();

        app.MapPost("/app/sessions/revoke-all", (HttpContext ctx, SessionStore store) =>
        {
            var count = store.RevokeAll();
            ApiHelpers.Audit(ctx, "sessions.revoke-all", count);
            AuthApi.ClearSessionCookie(ctx);
            return Results.Redirect("/app/login");
        }).DisableAntiforgery();
    }
}
