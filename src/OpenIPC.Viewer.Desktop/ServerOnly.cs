using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Web;
using OpenIPC.Viewer.Web.Auth;
using OpenIPC.Viewer.Web.Backend;

namespace OpenIPC.Viewer.Desktop;

// Headless entry for `--server-only`: runs the embedded web server without the
// Avalonia GUI (Phase 20 §20.1). Same distributable as the desktop app, just a
// different launch mode — no offscreen render surface needed since we simply
// never start Avalonia.
//
// Flags:
//   --server-only        run headless as a web server
//   --port <n>           listen port (default WebServerOptions.DefaultPort)
//   --lan                bind 0.0.0.0 (reachable from the LAN); default is
//                        localhost-only
//
// The admin password is read from the OPENIPC_WEB_ADMIN_PASSWORD environment
// variable; when unset, a random one is generated and logged on startup.
internal static class ServerOnly
{
    public const string Flag = "--server-only";
    private const string AdminPasswordEnv = "OPENIPC_WEB_ADMIN_PASSWORD";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var authOptions = new WebAuthOptions
        {
            AdminPassword = Environment.GetEnvironmentVariable(AdminPasswordEnv),
        };
        // Same on-disk database as the desktop app — the web server reads/writes
        // the very same cameras.
        var dbPath = Path.Combine(AppPaths.AppDataDir.FullName, "openipc-viewer.db");
        try
        {
            WebServer.RunAsync(options, authOptions, ConfigureBackend).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"server-only failed: {ex}");
            return 1;
        }

        void ConfigureBackend(IServiceCollection services)
        {
            // Platform trio (IFileSystem / ISecretsStore / …) then the lean
            // web backend (persistence + CameraDirectoryService).
            Composition.AddPlatformServices(services);
            services.AddWebBackend(dbPath);
        }
    }

    private static WebServerOptions ParseOptions(string[] args)
    {
        var port = WebServerOptions.DefaultPort;
        var lan = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--lan", StringComparison.OrdinalIgnoreCase))
            {
                lan = true;
            }
            else if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
                     && i + 1 < args.Length
                     && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                     && parsed is > 0 and <= 65535)
            {
                port = parsed;
                i++;
            }
        }

        return new WebServerOptions { Port = port, BindLan = lan };
    }
}
