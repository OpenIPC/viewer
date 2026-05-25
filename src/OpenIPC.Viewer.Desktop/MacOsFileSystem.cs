using System;
using System.IO;
using System.Runtime.Versioning;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOsFileSystem : IFileSystem
{
    public DirectoryInfo AppDataDir { get; } = AppPaths.AppDataDir;
    public DirectoryInfo RecordingsDir { get; }
    public DirectoryInfo SnapshotsDir { get; }

    public MacOsFileSystem()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RecordingsDir = PlatformPaths.EnsureDir(Path.Combine(home, "Movies", "OpenIPC"));
        SnapshotsDir = PlatformPaths.EnsureDir(Path.Combine(home, "Pictures", "OpenIPC"));
    }
}
