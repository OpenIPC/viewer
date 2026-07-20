using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web;

// The embedded ASP.NET Core (Kestrel) host.
//
// Phase 20 slice A (foundation): process lifecycle + health/version endpoints
// and safe bind defaults only. The camera/API/live-video surface, auth, and the
// shared backend composition land in later slices (§20.2–20.5). Keeping this
// slice backend-free is deliberate — it proves the headless host boots and
// serves before the data layer is wired in.
public static class WebServer
{
    // Version string surfaced by /healthz and /api/v1/version. Prefers the
    // git-derived InformationalVersion (see Directory.Build.targets), falling
    // back to the assembly version.
    public static string Version =>
        typeof(WebServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WebServer).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    // Runs the server until the process is stopped (Ctrl-C / SIGTERM via the
    // default host lifetime) or the supplied token is cancelled — the latter is
    // how the in-process desktop host (a later slice) will stop it.
    public static async Task RunAsync(
        WebServerOptions options, WebAuthOptions? authOptions = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        ConfigureAuthServices(builder, authOptions ?? new WebAuthOptions());

        var app = builder.Build();
        app.Urls.Add(options.Url);
        MapEndpoints(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web");
        logger.LogInformation(
            "OpenIPC Viewer web server listening on {Url} ({Scope})",
            options.Url, options.BindLan ? "LAN" : "localhost only");
        if (options.BindLan)
        {
            logger.LogWarning(
                "LAN bind is on: the server is reachable from the local network without TLS. " +
                "Expose it to a domain only behind a trusted reverse-proxy with HTTPS.");
        }

        if (ct.CanBeCanceled)
            ct.Register(() => _ = app.StopAsync());

        await app.RunAsync();
    }

    private static void ConfigureAuthServices(WebApplicationBuilder builder, WebAuthOptions authOptions)
    {
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<IWebAuthProvider, PasswordAuthProvider>();

        // Per-IP fixed window on the login endpoint — blunts credential stuffing
        // without touching the rest of the API.
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(AuthApi.LoginRateLimitPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromMinutes(1),
                        PermitLimit = 5,
                        QueueLimit = 0,
                    }));
        });
    }

    private static void MapEndpoints(WebApplication app)
    {
        // A minimal security-header floor on every response.
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });

        app.UseRateLimiter();
        // Origin check on mutations + bearer-token guard over protected API paths.
        app.UseWebAuth();

        // Liveness probe — touches no backend, so it answers even before the
        // data layer exists. Public (no auth) for --server-only smoke checks and,
        // later, container/service health.
        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            version = Version,
        }));

        app.MapGet("/api/v1/version", () => Results.Json(new
        {
            product = "OpenIPC Viewer",
            version = Version,
        }));

        // First protected endpoint — proves the auth pipeline end-to-end until
        // the real camera API lands. Requires a valid bearer token.
        app.MapGet("/api/v1/ping", (HttpContext ctx) =>
        {
            var identity = ctx.GetIdentity();
            return Results.Json(new { pong = true, user = identity?.Name });
        });

        app.MapAuthEndpoints();

        app.MapGet("/", () => Results.Content(PlaceholderPage, "text/html; charset=utf-8"));
    }

    private const string PlaceholderPage =
        "<!doctype html><meta charset=\"utf-8\"><title>OpenIPC Viewer</title>" +
        "<body style=\"font-family:system-ui,sans-serif;background:#0d1117;color:#c9d1d9;padding:2rem;line-height:1.5\">" +
        "<h1 style=\"margin:0 0 .5rem\">OpenIPC Viewer — web server</h1>" +
        "<p>Phase 20 · slice A. The camera monitor and API arrive in later slices.</p>" +
        "<p>Health: <a style=\"color:#58a6ff\" href=\"/healthz\">/healthz</a> · " +
        "<a style=\"color:#58a6ff\" href=\"/api/v1/version\">/api/v1/version</a></p>" +
        "</body>";
}
