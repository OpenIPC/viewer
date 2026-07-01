using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Ssh;

/// <summary>
/// Asks the user whether to trust a host key that differs from the pinned one
/// (strict TOFU). Implemented in the UI layer over the platform-independent
/// confirm dialog so the same flow covers desktop and mobile. When no prompt is
/// wired (headless), <see cref="NoopSshHostKeyPrompt"/> refuses — preserving the
/// strict default.
/// </summary>
public interface ISshHostKeyPrompt
{
    /// <summary>
    /// Returns true to trust <paramref name="presentedFingerprint"/> (the caller
    /// then re-pins it and retries the connection), false to refuse. Safe to call
    /// off the UI thread — implementations marshal internally.
    /// </summary>
    Task<bool> ConfirmChangedKeyAsync(
        string host, int port, string? knownFingerprint, string presentedFingerprint, CancellationToken ct);
}

/// <summary>Refuses every changed key. Fallback when no UI prompt is available.</summary>
public sealed class NoopSshHostKeyPrompt : ISshHostKeyPrompt
{
    public Task<bool> ConfirmChangedKeyAsync(
        string host, int port, string? knownFingerprint, string presentedFingerprint, CancellationToken ct) =>
        Task.FromResult(false);
}
