using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Services;

/// <summary>
/// Narrow seam over <see cref="CameraDirectoryService"/> so credential-needing
/// Core services (e.g. <c>SnapshotService</c>) can resolve a camera's login
/// without taking a dependency on the whole directory/secrets stack — and can
/// be unit-tested with a trivial fake.
/// </summary>
public interface ICameraCredentialsProvider
{
    Task<CameraCredentials?> GetCredentialsAsync(CameraId id, CancellationToken ct);
}
