using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// A connected SSH session to one camera. One session backs the terminal, the
/// file manager and the SSH Majestic transport. Connection-scoped — created per
/// use via <see cref="ISshSessionFactory"/>, not a DI singleton.
/// </summary>
/// <remarks>
/// File transfer uses SCP (not SFTP): OpenIPC's busybox ships scp far more
/// reliably than an sftp-server (phase-13 §13.4). Listing is done by parsing
/// <c>ls -la</c> output rather than SFTP for the same reason.
/// </remarks>
public interface ISshSession : IAsyncDisposable
{
    /// <summary>Opens the transport and authenticates. Verifies the host key (TOFU).</summary>
    Task ConnectAsync(SshEndpoint endpoint, CancellationToken ct);

    /// <summary>Opens an interactive shell (terminal) at the given window size.</summary>
    Task<ISshShell> OpenShellAsync(uint columns, uint rows, CancellationToken ct);

    /// <summary>Lists a remote directory by parsing <c>ls -la</c>.</summary>
    IAsyncEnumerable<RemoteEntry> ListAsync(string path, CancellationToken ct);

    /// <summary>Streams a remote file to a local path (progress in bytes).</summary>
    Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Streams a local file to a remote path (progress in bytes).</summary>
    Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Deletes a remote file. Root-level paths are rejected (see <see cref="RemotePathGuard"/>).</summary>
    Task DeleteAsync(string remotePath, CancellationToken ct);

    /// <summary>Creates a remote directory (<c>mkdir -p</c>).</summary>
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct);

    /// <summary>Runs a one-shot command and captures its output and exit code.</summary>
    Task<CommandResult> ExecAsync(string command, CancellationToken ct);
}

/// <summary>Creates fresh <see cref="ISshSession"/> instances. Registered as a DI singleton.</summary>
public interface ISshSessionFactory
{
    ISshSession Create();
}
