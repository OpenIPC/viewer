namespace OpenIPC.Viewer.Core.Video;

// Auto SD/HD policy (Phase 12.2): the multi-camera grid runs the substream to
// stay light; a single tile filling the view (1×1 layout) gets the mainstream.
// Disabling auto pins the grid to the substream everywhere.
public static class StreamQualityPolicy
{
    public static StreamQuality ForGrid(bool autoSdHd, int layoutSize) =>
        autoSdHd && layoutSize == 1 ? StreamQuality.Main : StreamQuality.Sub;

    // A per-camera override pins quality regardless of layout/global setting;
    // Auto falls back to ForGrid.
    public static StreamQuality Resolve(StreamQualityOverride cameraOverride, bool autoSdHd, int layoutSize) =>
        cameraOverride switch
        {
            StreamQualityOverride.AlwaysHd => StreamQuality.Main,
            StreamQualityOverride.AlwaysSd => StreamQuality.Sub,
            _ => ForGrid(autoSdHd, layoutSize),
        };
}
