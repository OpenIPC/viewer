using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Analytics;

// Resolves the detection model to a ready-to-load ModelSpec (Phase 15 — model
// is an asset, not code; fetched on first enable). The implementation caches in
// AppData, verifies integrity, and supports a local override path so the repo
// ships no binaries.
public interface IModelProvider
{
    Task<ModelSpec> EnsureModelAsync(CancellationToken ct);
}
