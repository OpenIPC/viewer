namespace OpenIPC.Viewer.Core.Majestic;

// Typed accessors for fields we actually surface in the UI. The full payload
// stays in RawJson as text so the advanced view can render it without us
// pre-binding to a fragile schema (Majestic JSON shape drifts release-to-
// release — phase-05 §"JSON schema unstable"). We keep raw as string instead
// of JsonElement because Core targets netstandard2.1 (no package deps); the
// Devices layer handles parsing.
public sealed record MajesticConfig(
    string RawJson,
    string? Codec,
    int? Fps,
    string? Resolution,
    int? Bitrate,
    string? Profile,
    NightMode NightMode,
    bool? RtmpEnabled = null,
    string? RtmpUrl = null,
    // Audio capture on the camera (Phase 17.4). null = the build doesn't expose
    // an audio section; false = present but off → we hint the user to enable it.
    bool? AudioEnabled = null);
