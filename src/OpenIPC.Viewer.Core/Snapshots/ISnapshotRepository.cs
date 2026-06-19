using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Snapshots;

public interface ISnapshotRepository
{
    /// <summary>
    /// Newest-first. <paramref name="sinceUtc"/>/<paramref name="untilUtc"/>
    /// are an inclusive lower / exclusive upper bound on <see cref="Snapshot.TakenAt"/>.
    /// </summary>
    Task<IReadOnlyList<Snapshot>> ListAsync(
        CameraId? cameraId,
        DateTime? sinceUtc,
        DateTime? untilUtc,
        int limit,
        CancellationToken ct);

    Task AddAsync(Snapshot snapshot, CancellationToken ct);

    Task<Snapshot?> GetAsync(SnapshotId id, CancellationToken ct);

    Task RemoveAsync(SnapshotId id, CancellationToken ct);
}
