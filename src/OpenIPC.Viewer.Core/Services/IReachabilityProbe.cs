using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Services;

/// <summary>
/// Lightweight TCP reachability check used by the library to show whether a
/// camera answers on its stream port. Connect-only — it does not authenticate
/// or pull a frame; "reachable" means the TCP handshake completed within the
/// timeout. The implementation lives in Infrastructure (it does socket IO) and
/// is wired via DI; Core only owns the contract.
/// </summary>
public interface IReachabilityProbe
{
    Task<bool> IsReachableAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}
