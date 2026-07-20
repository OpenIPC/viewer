using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenIPC.Viewer.Web.Auth;
using OpenIPC.Viewer.Web.Localization;

namespace OpenIPC.Viewer.Web.Api;

// Form-post endpoints for the server-rendered UI. Plain HTML forms (no JS) post
// here; we validate, mint a session, and set the HttpOnly cookie the pages read.
// Antiforgery is disabled on these because the Origin check (UseWebAuth) already
// blocks cross-site posts, and login is rate-limited.
public static class UiApi
{
    public static void MapUiEndpoints(this WebApplication app)
    {
        app.MapPost("/app/auth/login", async (HttpContext ctx, IWebAuthProvider provider, SessionStore store, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = form["user"].ToString();
            var password = form["password"].ToString();

            var identity = await provider.ValidateCredentialsAsync(user, password, ct);
            if (identity is null)
                return Results.Redirect("/app/login?error=1");

            var (token, _) = store.Create(identity);
            AuthApi.SetSessionCookie(ctx, token);
            return Results.Redirect("/app/cameras");
        }).DisableAntiforgery().RequireRateLimiting(AuthApi.LoginRateLimitPolicy);

        app.MapPost("/app/auth/logout", (HttpContext ctx, SessionStore store) =>
        {
            if (AuthApi.CookieToken(ctx) is { } token)
                store.Revoke(token);
            AuthApi.ClearSessionCookie(ctx);
            return Results.Redirect("/app/login");
        }).DisableAntiforgery();

        // UI language switch: persist the choice in a cookie, return to the page.
        app.MapGet("/app/lang/{code}", (string code, HttpContext ctx) =>
        {
            if (code is "ru" or "en")
            {
                ctx.Response.Cookies.Append(WebLocalizer.LanguageCookie, code, new CookieOptions
                {
                    Path = "/",
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromDays(365),
                });
            }
            var back = ctx.Request.Headers.Referer.ToString();
            return Results.Redirect(string.IsNullOrEmpty(back) ? "/app/cameras" : back);
        });
    }
}
