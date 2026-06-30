using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenIPC.Viewer.Core.Majestic;

// A parsed config.json reduced to a flat, ordered list of editable fields plus
// the original raw JSON (kept so write-back is read-modify-write of the whole
// payload — partial POSTs are unreliable across Majestic builds, phase-05).
//
// All schema/JSON work happens in the Devices layer (IMajesticConfigSchema);
// this type and its diff are pure so they're unit-tested in Core.Tests.
public sealed class MajesticConfigModel
{
    public string RawJson { get; }
    public IReadOnlyList<MajesticConfigField> Fields { get; }

    public MajesticConfigModel(string rawJson, IReadOnlyList<MajesticConfigField> fields)
    {
        RawJson = rawJson ?? throw new ArgumentNullException(nameof(rawJson));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    // Fields grouped by section, preserving first-seen section order and the
    // field order within each section.
    public IReadOnlyList<MajesticConfigSection> Sections =>
        Fields
            .GroupBy(f => f.Section, StringComparer.Ordinal)
            .Select(g => new MajesticConfigSection(g.Key, g.ToList()))
            .ToList();

    // Given the user's edited values keyed by field Path, return one edit per
    // field whose value actually changed. Unknown paths are ignored (the editor
    // can only surface fields we parsed). Comparison is value-kind aware so
    // "30" vs "30.0" or "True" vs "true" don't register as spurious changes.
    public IReadOnlyList<MajesticConfigFieldEdit> ComputeEdits(IReadOnlyDictionary<string, string> editedByPath)
    {
        if (editedByPath is null) throw new ArgumentNullException(nameof(editedByPath));
        var edits = new List<MajesticConfigFieldEdit>();
        foreach (var field in Fields)
        {
            if (!editedByPath.TryGetValue(field.Path, out var newValue)) continue;
            if (newValue is null) continue;
            if (ValuesEqual(field.Kind, field.Value, newValue)) continue;
            edits.Add(new MajesticConfigFieldEdit(field.Section, field.Key, field.Kind, newValue.Trim()));
        }
        return edits;
    }

    // Kind-aware value comparison, shared with the editor's "modified" highlight
    // so "30" vs "30.0" or "true" vs "True" never count as a change.
    public static bool ValuesEqual(MajesticFieldKind kind, string a, string b)
    {
        a = a?.Trim() ?? string.Empty;
        b = b?.Trim() ?? string.Empty;
        switch (kind)
        {
            case MajesticFieldKind.Bool:
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            case MajesticFieldKind.Int:
                if (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ia)
                    && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ib))
                    return ia == ib;
                // An int field can receive a "30.0"-style value from the editor;
                // fall back to numeric comparison so it isn't a spurious change.
                if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var dia)
                    && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var dib))
                    return dia.Equals(dib);
                return string.Equals(a, b, StringComparison.Ordinal);
            case MajesticFieldKind.Number:
                if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
                    && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
                    return da.Equals(db);
                return string.Equals(a, b, StringComparison.Ordinal);
            default:
                return string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}

public sealed record MajesticConfigSection(string Name, IReadOnlyList<MajesticConfigField> Fields);
