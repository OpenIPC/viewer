using Microsoft.AspNetCore.Http;

namespace OpenIPC.Viewer.Web.Auth;

// Authorization at the endpoint, one line per handler.
//
// The auth middleware only proves WHO is calling; what they may do is decided
// here, because it depends on the endpoint. Handlers call Deny(...) first and
// return the result if it's non-null:
//
//     if (ctx.Deny(WebPermission.Manage) is { } denied) return denied;
//
// 403 (not 404) for a permission failure: the caller is authenticated and the
// resource exists, they just aren't allowed. Camera scoping is the exception —
// see DenyCamera.
public static class PermissionGuard
{
    public static IResult? Deny(this HttpContext ctx, WebPermission permission)
    {
        var identity = ctx.GetIdentity();
        if (identity is not null && identity.Can(permission))
            return null;

        return Results.Json(
            new { error = "forbidden", required = permission.ToString() },
            statusCode: StatusCodes.Status403Forbidden);
    }

    // A camera outside the caller's subset answers 404, not 403: to that user it
    // does not exist, and a 403 would confirm the id belongs to a real camera.
    public static IResult? DenyCamera(this HttpContext ctx, string cameraId)
    {
        var identity = ctx.GetIdentity();
        if (identity is null || !identity.CanSee(cameraId))
            return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        return null;
    }

    public static bool CanSeeCamera(this HttpContext ctx, string cameraId) =>
        ctx.GetIdentity() is { } identity && identity.CanSee(cameraId);
}
