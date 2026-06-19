using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.Core.Majestic;

/// <summary>
/// SSH transport for the Majestic config file (Phase 13.5) — an alternative to
/// <see cref="IMajesticClient"/> for cameras whose HTTP API is disabled or
/// unreachable. Reads/writes the raw <c>majestic.yaml</c>; the typed/HTTP path
/// stays the default when it's available.
/// </summary>
public interface IMajesticSshConfigClient
{
    /// <summary>Absolute path to the config file on the device.</summary>
    string ConfigPath { get; }

    /// <summary>True if the config file exists on the device.</summary>
    Task<bool> ConfigExistsAsync(SshEndpoint endpoint, CancellationToken ct);

    /// <summary>Reads the raw <c>majestic.yaml</c> contents.</summary>
    Task<string> ReadRawAsync(SshEndpoint endpoint, CancellationToken ct);

    /// <summary>
    /// Writes the raw config back (atomically via a temp file + move) and,
    /// when <paramref name="restart"/> is set, signals majestic to reload.
    /// </summary>
    Task WriteRawAsync(SshEndpoint endpoint, string rawYaml, bool restart, CancellationToken ct);
}
