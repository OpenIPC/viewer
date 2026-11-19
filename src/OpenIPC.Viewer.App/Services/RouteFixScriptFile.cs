using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.LinkHealth;

namespace OpenIPC.Viewer.App.Services;

/// <summary>A written fix script and the SHA-256 of exactly the bytes the app wrote.</summary>
public sealed record WrittenFixScript(string Path, string Sha256);

// Writes the route-fix script next to the app data and launches it elevated
// on request. The app itself never changes routes: on Windows the user
// approves the UAC prompt, elsewhere they run the script with sudo.
public static class RouteFixScriptFile
{
    public const string LogFileName = "fix-camera-routes.log";

    public static WrittenFixScript Write(string appDataDir, RouteFixPlan plan, bool russian)
    {
        var target = RouteFixScript.CurrentTarget();
        var dir = Path.Combine(appDataDir, "fixes");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RouteFixScript.FileName(target));
        // Windows PowerShell 5.1 reads a BOM-less script as ANSI and breaks on
        // non-ASCII adapter names and messages; sh must not see a BOM.
        var bytes = RouteFixScript.FileBytes(RouteFixScript.Build(plan, target, DateTimeOffset.Now, russian), target);
        File.WriteAllBytes(path, bytes);
        // Hash what we generated, never what we read back: the file sits in a
        // user-writable folder.
        return new WrittenFixScript(path, RouteFixScript.Sha256Hex(bytes));
    }

    public static string Text(RouteFixPlan plan, bool russian) =>
        RouteFixScript.Build(plan, RouteFixScript.CurrentTarget(), DateTimeOffset.Now, russian);

    public static bool CanRunElevated => OperatingSystem.IsWindows();

    public static string LogPathFor(string scriptPath) =>
        Path.Combine(Path.GetDirectoryName(scriptPath) ?? string.Empty, LogFileName);

    /// <summary>
    /// Runs the script in an elevated PowerShell (UAC prompt) and waits for it.
    /// The elevated process never opens the script as a file to execute: an
    /// inline stub reads it once, checks its SHA-256 against
    /// <see cref="WrittenFixScript.Sha256"/> and runs those bytes from memory
    /// (see <see cref="RouteFixScript.ElevatedStub"/>). Returns the exit code
    /// (0 done, 1 not elevated, 2 some cameras failed, 3 crashed, 4 the file was
    /// swapped), or null when the user declined the prompt.
    /// </summary>
    public static async Task<int?> RunElevatedAsync(WrittenFixScript script, bool russian)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var stub = RouteFixScript.ElevatedStub(script.Path, script.Sha256, LogPathFor(script.Path), russian);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "-NoProfile -EncodedCommand " + RouteFixScript.EncodeCommand(stub),
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
            var log = LogPathFor(scriptPath);
            return File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
