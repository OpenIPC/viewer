using System.IO;
using System.Runtime.Versioning;
using OpenIPC.Viewer.App;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.iOS.Platform;

// iOS sandboxes everything under the per-app Documents folder
// (Environment.SpecialFolder.LocalApplicationData on .NET maps to that).
// Subfolders for recordings/snapshots — they show up in Files app because
// Info.plist sets UIFileSharingEnabled + LSSupportsOpeningDocumentsInPlace.
[SupportedOSPlatform("ios")]
public sealed class IosFileSystem : IFileSystem
{
    public DirectoryInfo AppDataDir { get; } = AppPaths.AppDataDir;
    public DirectoryInfo RecordingsDir { get; } = PlatformPaths.EnsureDir(Path.Combine(AppPaths.AppDataDir.FullName, "Recordings"));
    public DirectoryInfo SnapshotsDir { get; } = PlatformPaths.EnsureDir(Path.Combine(AppPaths.AppDataDir.FullName, "Snapshots"));
}
