using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Pinned SSH host-key fingerprints (TOFU), kept apart from the secrets store —
/// host keys aren't secrets, and a dedicated known-hosts store makes "forget
/// all" (after a fleet reflash) a single operation (phase-13 §13.3 settings).
/// </summary>
public interface ISshHostKeyStore
{
    /// <summary>Pinned SHA256 fingerprint for host:port, or null if none.</summary>
    Task<string?> GetAsync(string host, int port, CancellationToken ct);

    /// <summary>Pins (or re-pins) the fingerprint for host:port.</summary>
    Task SetAsync(string host, int port, string fingerprint, CancellationToken ct);

    /// <summary>Forgets every pinned host key.</summary>
    Task ClearAsync(CancellationToken ct);
}
