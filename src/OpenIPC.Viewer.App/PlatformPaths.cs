using System;
using System.IO;

namespace OpenIPC.Viewer.App;

// Single source of truth for the app's per-user data root. Used by AppPaths
// (pre-DI, for Serilog) and by the platform IFileSystem impls in Desktop.
// Splitting the platform branches here keeps both call sites consistent.
public static class PlatformPaths
{
    private const string AppFolderName = "OpenIPC.Viewer";
    private const string XdgFolderName = "openipc-viewer";

    public static DirectoryInfo ResolveAppData()
    {
        var path = OperatingSystem.IsWindows() ? WindowsAppData()
                 : OperatingSystem.IsMacOS()   ? MacOsAppData()
                 : OperatingSystem.IsLinux()   ? LinuxAppData()
                 : throw new PlatformNotSupportedException();
        return EnsureDir(path);
    }

    public static DirectoryInfo EnsureDir(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            dir.Create();
        return dir;
    }

    private static string WindowsAppData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    private static string MacOsAppData()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", AppFolderName);
    }

    private static string LinuxAppData()
    {
        // XDG Base Directory: $XDG_DATA_HOME, default ~/.local/share
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dataHome = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(dataHome, XdgFolderName);
    }
}
