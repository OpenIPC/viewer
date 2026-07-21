using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Web.Api;
using OpenIPC.Viewer.Web.Auth;

namespace OpenIPC.Viewer.Web;

// The embedded ASP.NET Core (Kestrel) host: process lifecycle, safe bind
// defaults, the auth pipeline, the /api/v1 surface, and the embedded React SPA.
//
// The backend is composed by the caller (Desktop head / --server-only) and is
// optional: without it the host still boots and serves health/version, and the
// data endpoints answer 503. The Phase 20 server-rendered Razor UI it used to
// carry was removed once the SPA reached parity — the API is now the only
// contract, and the SPA is just its first client.
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
        WebServerOptions options,
        WebAuthOptions? authOptions = null,
        Action<IServiceCollection>? configureBackend = null,
        CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        ConfigureAuthServices(builder, authOptions ?? new WebAuthOptions());
        configureBackend?.Invoke(builder.Services);

        var app = builder.Build();
        app.Urls.Add(options.Url);
        MapEndpoints(app, options);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenIPC.Web");
        await MigrateAsync(app, logger, ct);
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

    // Runs schema migrations when a backend is composed. No-op when the host was
    // started without one (e.g. auth-only runs / tests).
    private static async Task MigrateAsync(WebApplication app, ILogger logger, CancellationToken ct)
    {
        var migrator = app.Services.GetService<IMigrationRunner>();
        if (migrator is null)
            return;

        logger.LogInformation("Running database migrations…");
        await migrator.MigrateAsync(ct);
    }

    private static void ConfigureAuthServices(WebApplicationBuilder builder, WebAuthOptions authOptions)
    {
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<WebUserStore>();
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

    private static void MapEndpoints(WebApplication app, WebServerOptions options)
    {
        // Honour a reverse-proxy's X-Forwarded-* so request.Host/Scheme reflect
        // the public origin — otherwise the Origin/CSRF check compares against
        // the internal host and rejects every proxied mutation. Only loopback
        // (default) plus explicitly trusted proxy IPs are believed; a directly
        // exposed server ignores spoofed headers. Must run first.
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost,
        };
        foreach (var proxy in options.TrustedProxies)
        {
            if (IPAddress.TryParse(proxy, out var addr))
                forwarded.KnownProxies.Add(addr);
        }
        app.UseForwardedHeaders(forwarded);
        app.UseWebSockets();

        // A minimal security-header floor on every response.
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            // Not "no-referrer" — that makes Chrome send "Origin: null" on same-site
            // form POSTs, which then trips our own Origin check. This still withholds
            // the referrer cross-origin while keeping the Origin header intact.
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next(context);
        });

        // Serve the built React SPA (Vite output embedded in this assembly under
        // wwwroot/). Hashed assets under /assets are immutable; the entry
        // index.html is the client-routing fallback registered below. No auth
        // here: the bundles are public, the API behind them is guarded. Null when
        // the front-end wasn't built (glob empty) — then the host serves the API
        // only, which is all a headless/API deployment needs.
        var spa = TryCreateSpaFileProvider();
        if (spa is not null)
            app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });

        app.UseRateLimiter();
        // Origin check on mutations + bearer-token guard over protected API paths.
        // (No UseAntiforgery: the Razor UI it served is gone, and the JSON API is
        // covered by the Origin check plus SameSite=Strict on the session cookie.)
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
        app.MapCameraEndpoints();
        app.MapGroupEndpoints();
        app.MapLayoutEndpoints();
        app.MapLiveEndpoints();
        app.MapPtzEndpoints();
        app.MapDiscoveryEndpoints();
        app.MapSystemEndpoints();
        app.MapUserEndpoints();

        // Client-side routes (/, /cameras, /grid, …) resolve to the SPA entry;
        // real endpoints and static assets are matched before this. Without an
        // embedded index.html (front-end not built) this 404s and the host is an
        // API-only server.
        if (spa is not null)
        {
            app.MapFallback(async ctx =>
            {
                // An unmatched API path is a 404, not a page: otherwise a typo'd
                // or removed endpoint answers "200 text/html" and a client only
                // discovers the mistake when JSON.parse chokes on a doctype.
                if (IsApiPath(ctx.Request.Path))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync(new { error = "not_found" });
                    return;
                }

                var index = spa.GetFileInfo("index.html");
                if (!index.Exists)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(index);
            });
        }
    }

    // Paths that belong to the server, never to the client router.
    private static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/healthz");

    // The embedded SPA served at web root. Returns null when the wwwroot glob was
    // empty at build time (no manifest), so a backend-only build still runs.
    private static IFileProvider? TryCreateSpaFileProvider()
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(WebServer).Assembly, "wwwroot");
        }
        catch (InvalidOperationException)
        {
            // No embedded files manifest — the front-end wasn't built into wwwroot.
            return null;
        }
    }
}
