using System;
using System.Globalization;
using System.Linq;
using OpenIPC.Viewer.Web;

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
internal static class ServerOnly
{
    public const string Flag = "--server-only";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        try
        {
            WebServer.RunAsync(options).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"server-only failed: {ex}");
            return 1;
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
