namespace OpenIPC.Viewer.Core.Majestic;

// Each property null = leave the camera's current value alone. Implementation
// does read-modify-write of the full config JSON (phase-05 §"Patch semantics"
// — partial-update isn't reliable across Majestic builds).
public sealed record MajesticConfigPatch(
    string? Codec = null,
    int? Fps = null,
    string? Resolution = null,
    int? Bitrate = null,
    string? Profile = null);
