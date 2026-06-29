using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenIPC.Viewer.Core.Majestic;

namespace OpenIPC.Viewer.Devices.Majestic;

// Flattens Majestic config.json into editable scalar fields and writes edits
// back into the full payload (read-modify-write). Lives in Devices because
// Core is package-free (netstandard2.1) and can't reference System.Text.Json.
//
// We only surface scalars (string / number / bool). Arrays and nested objects
// deeper than one level aren't editable as a single control, so they're left
// untouched in the raw JSON and simply not shown.
public sealed class MajesticConfigSchema : IMajesticConfigSchema
{
    // Top-level scalar fields (rare in Majestic, but lossless to support) use an
    // empty section name; ApplyEdits writes those at the JSON root.
    private const string RootSection = "";

    public MajesticConfigModel Parse(string rawJson)
    {
        if (rawJson is null) throw new ArgumentNullException(nameof(rawJson));
        var fields = new List<MajesticConfigField>();

        if (JsonNode.Parse(rawJson) is JsonObject root)
        {
            foreach (var (name, node) in root)
            {
                if (node is JsonObject section)
                {
                    foreach (var (key, value) in section)
                        AddScalar(fields, name, key, value);
                }
                else
                {
                    // Top-level scalar.
                    AddScalar(fields, RootSection, name, node);
                }
            }
        }

        return new MajesticConfigModel(rawJson, fields);
    }

    public string ApplyEdits(string rawJson, IReadOnlyList<MajesticConfigFieldEdit> edits)
    {
        if (rawJson is null) throw new ArgumentNullException(nameof(rawJson));
        if (edits is null) throw new ArgumentNullException(nameof(edits));

        var root = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new InvalidOperationException("Majestic config root is not a JSON object");

        foreach (var edit in edits)
        {
            var target = root;
            if (!string.IsNullOrEmpty(edit.Section))
            {
                if (root[edit.Section] is not JsonObject section)
                {
                    section = new JsonObject();
                    root[edit.Section] = section;
                }
                target = section;
            }
            target[edit.Key] = Coerce(edit.Kind, edit.Value);
        }

        return root.ToJsonString();
    }

    private static void AddScalar(List<MajesticConfigField> fields, string section, string key, JsonNode? node)
    {
        if (node is not JsonValue value) return; // skip objects/arrays
        if (!TryDescribe(value, out var kind, out var text)) return;
        fields.Add(new MajesticConfigField(section, key, kind, text, Options: null, RequiresRestart: RestartsStream(section)));
    }

    // Map a JSON scalar to (kind, canonical string). Returns false for nulls and
    // anything that isn't a plain scalar we can round-trip.
    private static bool TryDescribe(JsonValue value, out MajesticFieldKind kind, out string text)
    {
        if (value.TryGetValue(out bool b))
        {
            kind = MajesticFieldKind.Bool;
            text = b ? "true" : "false";
            return true;
        }
        if (value.TryGetValue(out long l))
        {
            kind = MajesticFieldKind.Int;
            text = l.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue(out double d))
        {
            kind = MajesticFieldKind.Number;
            text = d.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (value.TryGetValue(out string? s) && s is not null)
        {
            kind = MajesticFieldKind.String;
            text = s;
            return true;
        }
        kind = MajesticFieldKind.String;
        text = string.Empty;
        return false;
    }

    private static JsonNode Coerce(MajesticFieldKind kind, string value)
    {
        value = value?.Trim() ?? string.Empty;
        switch (kind)
        {
            case MajesticFieldKind.Bool:
                return JsonValue.Create(
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
            case MajesticFieldKind.Int:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? JsonValue.Create(l)
                    : JsonValue.Create(value);
            case MajesticFieldKind.Number:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? JsonValue.Create(d)
                    : JsonValue.Create(value);
            default:
                return JsonValue.Create(value);
        }
    }

    // Changing video pipeline / system / streaming knobs restarts encoding, so
    // the live stream blinks. ISP / image fields (brightness, etc.) apply on the
    // fly. Used only as a UI hint; the camera ultimately decides.
    private static bool RestartsStream(string section) =>
        section.StartsWith("video", StringComparison.OrdinalIgnoreCase)
        || section.Equals("system", StringComparison.OrdinalIgnoreCase)
        || section.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
        || section.Equals("audio", StringComparison.OrdinalIgnoreCase);
}
