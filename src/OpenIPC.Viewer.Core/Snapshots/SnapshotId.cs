using System;

namespace OpenIPC.Viewer.Core.Snapshots;

public readonly record struct SnapshotId(Guid Value)
{
    public static SnapshotId New() => new(Guid.NewGuid());

    public static SnapshotId Parse(string text) => new(Guid.Parse(text));

    public override string ToString() => Value.ToString("D");
}
