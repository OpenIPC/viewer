using System;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Unix remote-path helpers for the file manager (Phase 13.4). Always uses
/// forward slashes regardless of the host OS — these are paths on the camera.
/// </summary>
public static class RemotePath
{
    public static string Combine(string directory, string name)
    {
        name = name.Trim('/');
        if (string.IsNullOrEmpty(directory) || directory == "/")
            return "/" + name;
        return directory.TrimEnd('/') + "/" + name;
    }

    public static string Parent(string path)
    {
        var p = (path ?? "/").TrimEnd('/');
        if (p.Length == 0)
            return "/";
        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    /// <summary>The last path segment (file/dir name); "/" for the root.</summary>
    public static string Name(string path)
    {
        var p = (path ?? "/").TrimEnd('/');
        if (p.Length == 0)
            return "/";
        var idx = p.LastIndexOf('/');
        return idx < 0 ? p : p[(idx + 1)..];
    }
}
