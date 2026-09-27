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
        => ThemeResource.Brush(LayoutTabIsActiveConverter.IsActive(values) ? "AccentBrush" : "Bg2Brush");
}

// Same inputs as LayoutTabBrushConverter; true only for the active tab (drives
// the tab's "⋯" actions button).
public sealed class LayoutTabIsActiveConverter : IMultiValueConverter
{
    public static readonly LayoutTabIsActiveConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => IsActive(values);

    internal static bool IsActive(IList<object?> values)
        => values.Count > 1 && values[0] is GridLayout self
           && values[1] is GridLayout act
           && self.Id == act.Id;
}

// Grid-size menu: value is the layout's current size, parameter the menu item's
// size ("1".."5"). The current size's glyph is drawn in the accent colour.
public sealed class GridSizeBrushConverter : IValueConverter
{
    public static readonly GridSizeBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value is int n && parameter is string p
                      && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                      && n == size;
        return ThemeResource.Brush(current ? "AccentBrush" : "TextSecondaryBrush");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Grid size (1..5) → the matching IconGrid{n} geometry, so the size button
// shows the shape of the current grid.
public sealed class GridSizeIconConverter : IValueConverter
{
    public static readonly GridSizeIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? Math.Clamp(i, 1, 5) : 2;
        object? res = null;
        return Application.Current?.Resources.TryGetResource($"IconGrid{n}", null, out res) == true ? res : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class ThemeResource
{
    public static IBrush Brush(string key)
    {
        object? res = null;
        if (Application.Current?.Resources.TryGetResource(key, null, out res) == true && res is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }
}
