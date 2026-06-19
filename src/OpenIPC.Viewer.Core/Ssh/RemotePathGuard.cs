using System;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Guards against catastrophic remote deletes. Deleting the root or any
/// top-level entry (<c>/etc</c>, <c>/bin</c>, …) would brick the camera, so the
/// file manager refuses it (phase-13 §13.4, after dashboard v0.1.2). Paths
/// deeper than the root level — <c>/etc/majestic.yaml</c>, <c>/tmp/clip.mp4</c> —
/// are allowed.
/// </summary>
public static class RemotePathGuard
{
    public static bool IsProtected(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var p = path.Trim().Replace('\\', '/');
        while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal))
            p = p[..^1];

        if (p is "/" or "." or "..")
            return true;

        // Relative paths are never root-level targets; the file manager always
        // passes absolute paths, so this only relaxes the guard for callers that
        // pass a bare name.
        if (!p.StartsWith("/", StringComparison.Ordinal))
            return false;

        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 1;
    }
}
