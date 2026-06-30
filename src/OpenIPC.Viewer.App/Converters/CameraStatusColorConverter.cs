using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Status;

namespace OpenIPC.Viewer.App.Converters;

// Unified status-badge palette (grid + sidebar). Online green, Connecting amber,
// Attention orange (camera answers but the stream is wedged), Offline red,
// Unknown grey. Supersedes StateColorConverter, which only saw SessionState and
// so could not tell Attention from Offline.
public sealed class CameraStatusColorConverter : IValueConverter
{
    public static readonly CameraStatusColorConverter Instance = new();

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush AttentionBrush = new SolidColorBrush(Color.Parse("#f97316"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#5e6878"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CameraStatus status) return UnknownBrush;
        return status switch
        {
            CameraStatus.Online => OnlineBrush,
            CameraStatus.Connecting => ConnectingBrush,
            CameraStatus.Attention => AttentionBrush,
            CameraStatus.Offline => OfflineBrush,
            _ => UnknownBrush,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
