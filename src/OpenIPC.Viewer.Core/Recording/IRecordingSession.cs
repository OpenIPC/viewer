using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Recording;

public interface IRecordingSession : IAsyncDisposable
{
    DateTime StartedAt { get; }
    string? CurrentSegmentPath { get; }
    IObservable<RecordingEvent> Events { get; }

    Task StopAsync(CancellationToken ct);
}
