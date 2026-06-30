using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenIPC.Viewer.App.Converters;

// Highlights the active page button in the grid pager. MultiBinding inputs:
// [thisPageNumber, currentPageNumber] (both 1-based). Returns the accent brush
// when they match, else the neutral button background — mirrors
// LayoutTabBrushConverter so the pager tracks the theme.
public sealed class PageActiveBrushConverter : IMultiValueConverter
{
    public static readonly PageActiveBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = values.Count >= 2 && values[0] is int page && values[1] is int current && page == current;
        var key = active ? "AccentBrush" : "Bg2Brush";
        object? res = null;
        if (Application.Current?.Resources.TryGetResource(key, null, out res) == true && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}
