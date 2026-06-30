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
    // credentialPassphrase != null → camera usernames/passwords are read from the
    // secrets store and written ENCRYPTED into the JSON (Phase 20, opt-in). null
    // keeps the default password-free export.
    Task<string> ExportAsync(string? credentialPassphrase, CancellationToken ct);

    // Counts what an import would change WITHOUT applying it (drives the preview).
    Task<ConfigImportPreview> PreviewAsync(string json, CancellationToken ct);

    // Applies the import (upsert cameras by id, append layouts) and returns the
    // same counts.
    Task<ConfigImportPreview> ImportAsync(string json, CancellationToken ct);

    // Authoritative sync for managed-fleet network auto-update (Phase 20): the
    // file is the source of truth. Cameras in the file are upserted by id,
    // cameras ABSENT from the file are removed, and layouts are replaced
    // wholesale. As a wipe-guard, a file with zero cameras leaves local cameras
    // untouched (a truncated/empty file is treated as an error, not "delete
    // everything"); likewise an empty layout set keeps the existing layouts.
    // credentialPassphrase != null → encrypted credentials in the file are
    // decrypted and stored into the local secrets store for each mirrored camera.
    Task<ConfigSyncResult> MirrorAsync(string json, string? credentialPassphrase, CancellationToken ct);
}

public sealed record ConfigImportPreview(int CamerasAdded, int CamerasUpdated, int LayoutsAdded);

public sealed record ConfigSyncResult(int CamerasAdded, int CamerasUpdated, int CamerasRemoved, int LayoutsReplaced);
