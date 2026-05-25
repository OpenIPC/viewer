using System.IO;
using System.Runtime.Versioning;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileSystem : IFileSystem
{
    public DirectoryInfo AppDataDir { get; } = AppPaths.AppDataDir;
    public DirectoryInfo RecordingsDir { get; } = PlatformPaths.EnsureDir(Path.Combine(AppPaths.AppDataDir.FullName, "recordings"));
    public DirectoryInfo SnapshotsDir { get; } = PlatformPaths.EnsureDir(Path.Combine(AppPaths.AppDataDir.FullName, "snapshots"));
}
