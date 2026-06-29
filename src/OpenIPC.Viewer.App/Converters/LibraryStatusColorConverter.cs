using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Status;

namespace OpenIPC.Viewer.App.Converters;

// Library/sidebar status dot. Shares the CameraStatus model with the grid but
// keeps a calmer palette: offline rows are muted grey (a management list, not an
// active monitor) rather than the grid's alarming red. Online green, Checking
// amber, Attention orange (a live grid session is wedged — surfaced via the
// status registry).
public sealed class LibraryStatusColorConverter : IValueConverter
{
    public static readonly LibraryStatusColorConverter Instance = new();

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush CheckingBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush AttentionBrush = new SolidColorBrush(Color.Parse("#f97316"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#5e6878"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CameraStatus status) return OfflineBrush;
        return status switch
        {
            CameraStatus.Online => OnlineBrush,
            CameraStatus.Connecting => CheckingBrush,
            CameraStatus.Attention => AttentionBrush,
            _ => OfflineBrush, // Offline / Unknown
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
