using System;
using System.IO;

namespace OpenIPC.Viewer.App;

public static class AppPaths
{
    public static DirectoryInfo AppDataDir { get; } = ResolveAppData();

    public static DirectoryInfo LogsDir { get; } = EnsureDir(Path.Combine(AppDataDir.FullName, "logs"));

    private static DirectoryInfo ResolveAppData()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return EnsureDir(Path.Combine(root, "OpenIPC.Viewer"));
    }

    private static DirectoryInfo EnsureDir(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            dir.Create();
        return dir;
    }
}
