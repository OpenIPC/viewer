using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// An interactive shell channel (PTY) over an SSH session. The terminal UI
/// (Phase 13.3) reads <see cref="DataReceived"/> bytes into its VT buffer and
/// writes keystrokes via <see cref="SendAsync"/>. Kept byte-oriented so the
/// Core contract carries no SSH.NET types.
/// </summary>
public interface ISshShell : IAsyncDisposable
{
    /// <summary>Raised on the SSH receive thread with a chunk of terminal output.</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>Sends raw input (typically keystrokes) to the remote shell.</summary>
    Task SendAsync(string data, CancellationToken ct);

    /// <summary>
    /// Best-effort PTY resize. Not all servers/transports honour a mid-session
    /// window change; the shell is created at the initial size passed to
    /// <see cref="ISshSession.OpenShellAsync"/>.
    /// </summary>
    void Resize(uint columns, uint rows);
}
