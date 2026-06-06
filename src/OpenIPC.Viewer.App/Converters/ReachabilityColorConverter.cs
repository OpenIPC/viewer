using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Converters;

public sealed class ReachabilityColorConverter : IValueConverter
{
    public static readonly ReachabilityColorConverter Instance = new();

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush CheckingBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#5e6878"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CameraReachability state) return OfflineBrush;
        return state switch
        {
            CameraReachability.Online => OnlineBrush,
            CameraReachability.Checking => CheckingBrush,
            _ => OfflineBrush,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
