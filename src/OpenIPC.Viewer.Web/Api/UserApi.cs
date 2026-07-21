using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Web.Auth;
using static OpenIPC.Viewer.Web.Api.ApiHelpers;

namespace OpenIPC.Viewer.Web.Api;

// Managing who may sign in to the web console. Manage-only, and password hashes
// never cross the wire — the API speaks permissions and camera subsets.
//
// Deleting or demoting yourself is allowed (an admin may hand over and step
// down); the bootstrap admin account always remains as the way back in.
public static class UserApi
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/users", (HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetRequiredService<WebUserStore>();
            return Results.Json(new
            {
                // False when the host runs without a roster path: the UI then
                // explains that only the bootstrap admin exists.
                available = store.IsAvailable,
                users = store.Users.Select(Describe).ToList(),
            });
        });

        app.MapPost("/api/v1/users", (WebUserWriteRequest? body, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetRequiredService<WebUserStore>();
            if (!store.IsAvailable)
                return RosterUnavailable();
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return ValidationError("name is required");
            if (string.IsNullOrEmpty(body.Password))
                return ValidationError("password is required");
            if (!TryParsePermissions(body.Permissions, out var permissions, out var error))
                return ValidationError(error!);

            try
            {
                var created = store.Add(body.Name.Trim(), body.Password, permissions, body.Cameras);
                Audit(ctx, "user.create", created.Name);
                return Results.Json(Describe(created));
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new { error = "user_exists" }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPut("/api/v1/users/{name}", (string name, WebUserWriteRequest? body, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetRequiredService<WebUserStore>();
            if (!store.IsAvailable)
                return RosterUnavailable();
            if (body is null)
                return ValidationError("missing body");

            WebPermission? permissions = null;
            if (body.Permissions is not null)
            {
                if (!TryParsePermissions(body.Permissions, out var parsed, out var error))
                    return ValidationError(error!);
                permissions = parsed;
            }

            // An explicit null camera list means "all cameras"; omitting the
            // field leaves the current subset alone. JSON can't tell those apart,
            // so the client sets allCameras to ask for the reset.
            var updated = store.Update(name, body.Password, permissions, body.Cameras, body.AllCameras == true);
            if (updated is null)
                return NotFound();

            Audit(ctx, "user.update", updated.Name);
            return Results.Json(Describe(updated));
        });

        app.MapDelete("/api/v1/users/{name}", (string name, HttpContext ctx) =>
        {
            if (ctx.Deny(WebPermission.Manage) is { } denied)
                return denied;
            var store = ctx.RequestServices.GetRequiredService<WebUserStore>();
            if (!store.IsAvailable)
                return RosterUnavailable();
            if (!store.Remove(name))
                return NotFound();

            Audit(ctx, "user.delete", name);
            return Results.NoContent();
        });
    }

    private static IResult RosterUnavailable() =>
        Results.Json(new { error = "roster_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static object Describe(WebUserRecord user) => new
    {
        name = user.Name,
        role = WebRoles.ForPermissions(user.Permissions),
        permissions = Names(user.Permissions),
        cameras = user.Cameras,
    };

    private static List<string> Names(WebPermission permissions) =>
        Enum.GetValues<WebPermission>()
            .Where(p => p != WebPermission.None && p != WebPermission.All && permissions.HasFlag(p))
            .Select(p => p.ToString())
            .ToList();

    private static bool TryParsePermissions(
        IReadOnlyList<string>? names, out WebPermission permissions, out string? error)
    {
        permissions = WebPermission.None;
        error = null;
        if (names is null || names.Count == 0)
        {
            error = "permissions are required";
            return false;
        }
        foreach (var name in names)
        {
            if (!Enum.TryParse<WebPermission>(name, ignoreCase: true, out var parsed))
            {
                error = $"unknown permission '{name}'";
                return false;
            }
            permissions |= parsed;
        }
        return true;
    }
}

// Cameras: a list restricts the user to it, omitted keeps what they had, and
// AllCameras=true lifts the restriction entirely.
internal sealed record WebUserWriteRequest(
    string? Name,
    string? Password,
    IReadOnlyList<string>? Permissions,
    List<string>? Cameras,
    bool? AllCameras);
