using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.LinkHealth;

namespace OpenIPC.Viewer.App.Services;

// Writes the route-fix script next to the app data and launches it elevated
// on request. The app itself never changes routes: on Windows the user
// approves the UAC prompt, elsewhere they run the script with sudo.
public static class RouteFixScriptFile
{
    public static string Write(string appDataDir, RouteFixPlan plan, bool russian)
    {
        var target = RouteFixScript.CurrentTarget();
        var dir = Path.Combine(appDataDir, "fixes");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RouteFixScript.FileName(target));
        var text = RouteFixScript.Build(plan, target, DateTimeOffset.Now, russian);
        // Windows PowerShell 5.1 reads a BOM-less script as ANSI and breaks on
        // non-ASCII adapter names and messages; sh must not see a BOM.
        File.WriteAllText(path, text, new UTF8Encoding(RouteFixScript.NeedsBom(target)));
        return path;
    }

    public static string Text(RouteFixPlan plan, bool russian) =>
        RouteFixScript.Build(plan, RouteFixScript.CurrentTarget(), DateTimeOffset.Now, russian);

    public static bool CanRunElevated => OperatingSystem.IsWindows();

    /// <summary>
    /// Runs the script in an elevated PowerShell (UAC prompt) and waits for it.
    /// Returns its exit code (0 done, 1 not elevated, 2 some cameras failed,
    /// 3 crashed), or null when the user declined the prompt.
    /// </summary>
    public static async Task<int?> RunElevatedAsync(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" -Pause",
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return 0;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return null;
        }
    }

    /// <summary>The log the script writes next to itself, or "" when there is none.</summary>
    public static string ReadLog(string scriptPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(scriptPath);
            if (dir is null) return string.Empty;
            var log = Path.Combine(dir, "fix-camera-routes.log");
            return File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
