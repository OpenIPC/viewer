using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Persistence;

// Export / import the configuration as a single JSON document (Phase 19.2) — a
// community sharing + machine-migration tool. Secrets (camera passwords) are
// NEVER written: they live in the OS secrets store, not in the exported
// entities, so the JSON is safe to share by default. Camera ids are GUIDs we
// control, so they survive a round-trip; group int-ids and live settings are
// out of this first cut.
public interface IConfigBackupService
{
    Task<string> ExportAsync(CancellationToken ct);

    // Counts what an import would change WITHOUT applying it (drives the preview).
    Task<ConfigImportPreview> PreviewAsync(string json, CancellationToken ct);

    // Applies the import (upsert cameras by id, append layouts) and returns the
    // same counts.
    Task<ConfigImportPreview> ImportAsync(string json, CancellationToken ct);
}

public sealed record ConfigImportPreview(int CamerasAdded, int CamerasUpdated, int LayoutsAdded);
