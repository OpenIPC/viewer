using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Recording;

public interface IRecordingRepository
{
    Task<IReadOnlyList<Recording>> ListAsync(CameraId? cameraId, CancellationToken ct);
    Task AddAsync(Recording recording, CancellationToken ct);
    Task UpdateAsync(Recording recording, CancellationToken ct);
    Task<Recording?> GetByPathAsync(string filePath, CancellationToken ct);
    Task RemoveAsync(RecordingId id, CancellationToken ct);
}
