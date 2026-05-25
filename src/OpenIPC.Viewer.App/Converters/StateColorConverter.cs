using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Converters;

public sealed class StateColorConverter : IValueConverter
{
    public static readonly StateColorConverter Instance = new();

    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#5e6878"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionState state) return OfflineBrush;
        return state switch
        {
            SessionState.Playing => LiveBrush,
            SessionState.Connecting or SessionState.Reconnecting => WarnBrush,
            _ => OfflineBrush,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
