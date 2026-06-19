using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace OpenIPC.Viewer.App.Converters;

/// <summary>
/// Turns a file-path string into a decoded <see cref="Bitmap"/> for an
/// <c>Image.Source</c> binding. Decodes capped at <see cref="DecodeWidth"/> so
/// the gallery never loads full-resolution stills even when a thumbnail is
/// missing. Returns null (blank cell) on any failure. Only the realized
/// (visible) ItemsRepeater items hit this, so virtualization keeps it cheap.
/// </summary>
public sealed class PathToThumbnailConverter : IValueConverter
{
    public static readonly PathToThumbnailConverter Instance = new();

    private const int DecodeWidth = 320;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DecodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
