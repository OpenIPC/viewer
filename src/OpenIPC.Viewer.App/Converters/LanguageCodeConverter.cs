using System;
using System.Globalization;
using Avalonia.Data.Converters;
using OpenIPC.Viewer.App.Services;

namespace OpenIPC.Viewer.App.Converters;

// Maps raw language codes (system/en/ru) to localized display names via
// Localizer.Instance. Used by the Settings → Appearance language picker so
// the dropdown shows "System / English / Русский" instead of "system/en/ru".
//
// Re-evaluation after a language switch: Localizer raises PropertyChanged
// on "Item[]" — but converters don't subscribe. ComboBox items re-render
// when the dropdown opens, so labels refresh on next open. The closed
// ComboBox face may lag one frame; acceptable for a one-off picker.
public sealed class LanguageCodeConverter : IValueConverter
{
    public static readonly LanguageCodeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var code = value as string ?? "";
        var key = code switch
        {
            "system" => "Lang.System",
            "en" => "Lang.English",
            "ru" => "Lang.Russian",
            _ => $"Lang.{code}",
        };
        return Localizer.Instance[key];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
