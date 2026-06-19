using System;

namespace OpenIPC.Viewer.Core.Ssh;

public enum RemoteEntryKind
{
    File,
    Directory,
    SymbolicLink,
    Other,
}

/// <summary>One entry in a remote directory listing (parsed from <c>ls -la</c>).</summary>
public sealed record RemoteEntry(
    string Name,
    RemoteEntryKind Kind,
    long Size,
    DateTimeOffset? Modified,
    string? Permissions)
{
    public bool IsDirectory => Kind == RemoteEntryKind.Directory;
}
