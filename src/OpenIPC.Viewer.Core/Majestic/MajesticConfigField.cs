using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Majestic;

// One editable knob from the camera's config.json, flattened to (section, key).
// Value is the camera's current value as a canonical string — "true"/"false"
// for Bool, invariant number text for Int/Number, raw text for String/Enum.
// Keeping a single string form (instead of object/JsonElement) lets Core stay
// package-free (netstandard2.1) while the Devices layer owns JSON coercion.
//
// We deliberately do NOT try to model the whole Majestic schema — it drifts
// release-to-release (phase-05 §"JSON schema unstable"). The parser surfaces
// whatever scalar fields the live config actually contains; unknown sections
// just become more fields.
public sealed record MajesticConfigField(
    string Section,
    string Key,
    MajesticFieldKind Kind,
    string Value,
    // Candidate values when Kind == Enum (e.g. codec h264/h265). null otherwise.
    IReadOnlyList<string>? Options = null,
    // True when changing this field restarts the video pipeline (video*/system).
    // The editor flags these so the user knows the stream will blink.
    bool RequiresRestart = false)
{
    // Stable identity for a field across a reload — used as the diff key.
    public string Path => Section + "." + Key;
}
