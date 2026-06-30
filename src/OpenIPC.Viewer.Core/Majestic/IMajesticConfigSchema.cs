using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Majestic;

// Schema-driven view over a Majestic config.json. Lives in Core as a contract
// (App references Core only); the JSON walk + write-back is implemented in
// Devices where System.Text.Json is available.
//
// This is what powers the dynamic editor (Slice B): instead of binding to a
// fixed set of fields, the UI renders whatever scalar knobs the live config
// exposes, then hands the edited values back through ApplyEdits.
public interface IMajesticConfigSchema
{
    // Flatten the raw config.json into an ordered list of editable scalar
    // fields. Nested objects become "section.key" entries; arrays and deeply
    // nested objects are skipped (not editable as a single control).
    MajesticConfigModel Parse(string rawJson);

    // Read-modify-write: apply the edits onto the original raw JSON and return
    // the full updated payload to POST. Only the listed (section, key) pairs are
    // touched; everything else is preserved byte-for-byte where possible.
    string ApplyEdits(string rawJson, IReadOnlyList<MajesticConfigFieldEdit> edits);
}
