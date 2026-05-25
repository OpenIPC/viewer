using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Recording;

public interface IRecorder
{
    Task<IRecordingSession> StartAsync(RecordingOptions options, CancellationToken ct);
}
