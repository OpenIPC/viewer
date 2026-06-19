namespace OpenIPC.Viewer.Core.Analytics;

// One detected object. The bounding box is normalized 0..1 relative to the
// SOURCE frame (already un-letterboxed by the detector), so the overlay can
// scale it to any tile size without knowing the model input resolution.
public readonly record struct Detection(
    int ClassId,
    string ClassName,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);
