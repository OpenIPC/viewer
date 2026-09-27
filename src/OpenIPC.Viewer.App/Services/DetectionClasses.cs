using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenIPC.Viewer.App.Services;

// Presentation helpers for AI detection class labels (COCO names as the model
// emits them, e.g. "car"): localized name, icon, and parsing of an event's
// "person ×2, car ×1" summary.
public static class DetectionClasses
{
    // Localizer returns the key itself when a translation is missing — fall
    // back to the model's own label then.
    public static string Localize(string label)
    {
        var key = "Coco." + label.ToLowerInvariant();
        var text = Localizer.Instance[key];
        return text == key ? label : text;
    }

    // Theme.axaml geometry key for the class; similar classes share one.
    public static string IconKey(string label) => label.ToLowerInvariant() switch
    {
        "person" => "IconPerson",
        "car" => "IconCar",
        "truck" or "bus" or "train" => "IconTruck",
        "bicycle" or "motorcycle" => "IconBike",
        _ => "IconScanSearch",
    };

    // "person ×2, car ×1" → { person: 2, car: 1 }.
    public static Dictionary<string, int> Parse(string? summary)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(summary)) return result;
        foreach (var part in summary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('×', StringSplitOptions.TrimEntries);
            var label = bits[0];
            if (label.Length == 0) continue;
            var count = bits.Length > 1 && int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 1;
            result[label] = result.TryGetValue(label, out var cur) ? Math.Max(cur, count) : count;
        }
        return result;
    }
}

// A class with a count, rendered as an icon chip ("🚗 машина 84").
public sealed record DetectionClassChip(string Label, int Count, string IconKey)
{
    public string Name => DetectionClasses.Localize(Label);
    public bool HasCount => Count > 0;
    public string CountLabel => Count > 0 ? $"×{Count}" : "";
}
