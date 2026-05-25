using System.IO;

namespace OpenIPC.Viewer.App;

public static class AppPaths
{
    public static DirectoryInfo AppDataDir { get; } = PlatformPaths.ResolveAppData();

    public static DirectoryInfo LogsDir { get; } = PlatformPaths.EnsureDir(Path.Combine(AppDataDir.FullName, "logs"));
}
