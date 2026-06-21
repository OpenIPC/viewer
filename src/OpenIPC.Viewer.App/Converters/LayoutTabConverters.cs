using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.App.Converters;

// Highlights the active layout tab (Phase 19.1). MultiBinding inputs: [thisLayout,
// activeLayout]. Returns the accent brush when they're the same layout, else the
// neutral tab background — resolved from theme resources so it tracks the theme.
public sealed class LayoutTabBrushConverter : IMultiValueConverter
{
    public static readonly LayoutTabBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = values.Count > 0 && values[0] is GridLayout self
                     && values[1] is GridLayout act
                     && self.Id == act.Id;
        var key = active ? "AccentBrush" : "Bg2Brush";
        object? res = null;
        if (Application.Current?.Resources.TryGetResource(key, null, out res) == true && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}
