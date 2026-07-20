using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OpenIPC.Viewer.Web.Auth;

// Auth wiring for the API: the login/logout/me endpoints, a bearer-token guard
// over protected paths, and an Origin check on mutations. Bearer tokens (not
// cookies) already blunt CSRF — the Origin check is defense-in-depth for the
// browser client. Behind a reverse-proxy the Origin/host comparison relies on
// forwarded headers (wired in §20.7).
public static class AuthApi
{
    // Stashes the authenticated identity for the endpoint to read.
    public const string IdentityItemKey = "openipc.web.identity";

    // Browser navigations can't set an Authorization header, so the same session
    // token also rides in an HttpOnly cookie for the server-rendered UI. API
    // clients keep using the bearer header; either is accepted.
    public const string SessionCookieName = "openipc_session";

    public static WebIdentity? GetIdentity(this HttpContext ctx) =>
        ctx.Items.TryGetValue(IdentityItemKey, out var value) ? value as WebIdentity : null;

    // Reject cross-origin mutations, then require a valid bearer token on
    // protected API paths. Registered before the endpoints so it can short-circuit.
    public static void UseWebAuth(this WebApplication app)
    {
        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            if (IsMutation(ctx.Request.Method) && !OriginAllowed(ctx.Request))
            {
                await WriteError(ctx, StatusCodes.Status403Forbidden, "bad_origin");
                return;
            }

            if (RequiresAuth(ctx.Request.Path))
            {
                var store = ctx.RequestServices.GetRequiredService<SessionStore>();
                var identity = TokenFrom(ctx) is { } token ? store.Validate(token) : null;
                if (identity is null)
                {
                    await WriteError(ctx, StatusCodes.Status401Unauthorized, "unauthorized");
                    return;
                }

                ctx.Items[IdentityItemKey] = identity;
            }

            await next(ctx);
        });
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/auth");

        group.MapPost("/login", async (
            LoginRequest request,
            IWebAuthProvider provider,
            SessionStore store,
            CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrEmpty(request.User))
                return Results.BadRequest(new { error = "missing_credentials" });

            var identity = await provider.ValidateCredentialsAsync(request.User, request.Password ?? "", ct);
            if (identity is null)
                return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var (token, expiresAt) = store.Create(identity);
            return Results.Json(new
            {
                token,
                user = identity.Name,
                roles = identity.Roles,
                expiresAt,
            });
        }).RequireRateLimiting(LoginRateLimitPolicy);

        group.MapPost("/logout", (HttpContext ctx, SessionStore store) =>
        {
            if (BearerToken(ctx) is { } token)
                store.Revoke(token);
            return Results.NoContent();
        });

        group.MapGet("/me", (HttpContext ctx) =>
        {
            var identity = ctx.GetIdentity();
            return identity is null
                ? Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(new { user = identity.Name, roles = identity.Roles });
        });
    }

    public const string LoginRateLimitPolicy = "login";

    private static bool RequiresAuth(PathString path)
    {
        if (!path.StartsWithSegments("/api/v1", out var rest))
            return false;
        // Public within the API surface: version discovery and the login endpoint.
        if (rest.Equals("/version", StringComparison.OrdinalIgnoreCase))
            return false;
        if (rest.Equals("/auth/login", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    // Same-origin only when an Origin header is present. Native clients (curl,
    // the desktop app) send no Origin and are allowed — CSRF is a browser threat.
    private static bool OriginAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BearerToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? header[scheme.Length..].Trim()
            : null;
    }

    // The session token from either transport: bearer header (API) or the
    // HttpOnly cookie (server-rendered UI navigations).
    public static string? TokenFrom(HttpContext ctx) =>
        BearerToken(ctx) ?? CookieToken(ctx);

    public static string? CookieToken(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(SessionCookieName, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    public static void SetSessionCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,   // set once TLS is terminated (reverse-proxy)
            Path = "/",
        });

    public static void ClearSessionCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(SessionCookieName);

    private static Task WriteError(HttpContext ctx, int statusCode, string error)
    {
        ctx.Response.StatusCode = statusCode;
        return ctx.Response.WriteAsJsonAsync(new { error });
    }
}

public sealed record LoginRequest(string User, string? Password);
