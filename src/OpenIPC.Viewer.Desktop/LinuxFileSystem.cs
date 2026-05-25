using System;
using System.IO;
using System.Runtime.Versioning;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Desktop;

// XDG paths per https://specifications.freedesktop.org/basedir-spec/.
// Recordings/Snapshots prefer the user-dirs spec ($XDG_VIDEOS_DIR /
// $XDG_PICTURES_DIR) if set; otherwise fall back to subfolders of AppData.
// We deliberately don't parse ~/.config/user-dirs.dirs — only honour the env
// vars (set by login session managers on most distros).
[SupportedOSPlatform("linux")]
public sealed class LinuxFileSystem : IFileSystem
{
    public DirectoryInfo AppDataDir { get; } = AppPaths.AppDataDir;
    public DirectoryInfo RecordingsDir { get; }
    public DirectoryInfo SnapshotsDir { get; }

    public LinuxFileSystem()
    {
        RecordingsDir = ResolveUserDir("XDG_VIDEOS_DIR", "OpenIPC")
                        ?? PlatformPaths.EnsureDir(Path.Combine(AppDataDir.FullName, "recordings"));
        SnapshotsDir = ResolveUserDir("XDG_PICTURES_DIR", "OpenIPC")
                       ?? PlatformPaths.EnsureDir(Path.Combine(AppDataDir.FullName, "snapshots"));
    }

    private static DirectoryInfo? ResolveUserDir(string envVar, string subFolder)
    {
        var root = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(root))
            return null;
        return PlatformPaths.EnsureDir(Path.Combine(root, subFolder));
    }
}
