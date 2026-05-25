using System;

namespace OpenIPC.Viewer.Core.Recording;

public readonly record struct RecordingId(Guid Value)
{
    public static RecordingId New() => new(Guid.NewGuid());

    public static RecordingId Parse(string text) => new(Guid.Parse(text));

    public override string ToString() => Value.ToString("D");
}
