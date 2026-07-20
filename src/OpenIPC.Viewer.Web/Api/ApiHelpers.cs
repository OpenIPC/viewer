using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web.Api;

// Shared result + audit helpers for the API endpoints, so cameras/groups/etc.
// answer with one consistent shape.
internal static class ApiHelpers
{
    public static IResult BackendUnavailable() =>
        Results.Json(new { error = "backend_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult NotFound() =>
        Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);

    public static IResult ValidationError(IReadOnlyList<string> errors) =>
        Results.Json(new { error = "validation", details = errors }, statusCode: StatusCodes.Status400BadRequest);

    public static IResult ValidationError(string error) => ValidationError(new[] { error });

    // One audit line per mutation: who did what to which subject, from where.
    public static void Audit(HttpContext ctx, string action, object subject)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web.Audit");
        logger.LogInformation(
            "audit {Action} subject={Subject} user={User} ip={Ip}",
            action, subject, ctx.GetIdentity()?.Name ?? "?", ctx.Connection.RemoteIpAddress);
    }
}
