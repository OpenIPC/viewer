namespace OpenIPC.Viewer.Core.Onvif;

// One axis of a camera's PTZ coordinate space, as reported by
// GetConfigurationOptions.
//
// The UI thinks in normalized [-1, 1] (or [0, 1] for absolute zoom); the camera
// thinks in whatever range it declares — commonly [-1, 1], but plenty of
// devices report degrees, [0, 360], or something asymmetric. Sending a value
// outside the declared range is the classic way to have a move silently clamped
// or refused, so every value crossing the wire goes through here.
public readonly record struct PtzRange(float Min, float Max)
{
    public static readonly PtzRange Normalized = new(-1f, 1f);

    public bool IsValid => Max > Min;

    // A normalized [-1, 1] translation scaled onto this range's span: zero stays
    // zero — a step that never touched an axis must not move it — and the result
    // is clamped into the declared bounds, so a range like [0, 100] simply
    // refuses to go negative rather than being sent a value below its minimum.
    public float ScaleTranslation(float value)
    {
        var clamped = value < -1f ? -1f : value > 1f ? 1f : value;
        if (!IsValid) return clamped;
        return Clamp(clamped * (Max - Min) / 2f);
    }

    // Maps normalized [0, 1] onto this range — absolute zoom, where 0 is fully
    // wide and 1 fully tele.
    public float FromUnit(float value)
    {
        if (!IsValid) return value;
        var clamped = value < 0f ? 0f : value > 1f ? 1f : value;
        return Min + clamped * (Max - Min);
    }

    // The inverse, for reporting a camera position back to the UI.
    public float ToUnit(float value)
    {
        if (!IsValid) return value;
        var unit = (value - Min) / (Max - Min);
        return unit < 0f ? 0f : unit > 1f ? 1f : unit;
    }

    public float Clamp(float value) =>
        !IsValid ? value : value < Min ? Min : value > Max ? Max : value;
}
