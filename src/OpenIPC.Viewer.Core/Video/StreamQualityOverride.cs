namespace OpenIPC.Viewer.Core.Video;

// Per-camera SD/HD policy override (Phase 12.2). Auto defers to the global
// Auto SD/HD setting + layout; the explicit values pin a camera regardless.
// Stored as an INTEGER column, so the numeric values are part of the schema —
// append new members, don't renumber.
public enum StreamQualityOverride
{
    Auto = 0,
    AlwaysHd = 1,
    AlwaysSd = 2,
}
