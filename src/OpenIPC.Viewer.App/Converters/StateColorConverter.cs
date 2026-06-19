using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Converters;

public sealed class StateColorConverter : IValueConverter
{
    public static readonly StateColorConverter Instance = new();

    // Grid badge palette (mockup screen 1): LIVE green, Connecting/Reconnecting
    // amber, Offline/Failed red, idle grey. Distinct from SingleCameraPage where
    // LIVE is danger-red — there the badge doubles as a recording cue.
    private static readonly IBrush LiveBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#5e6878"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionState state) return IdleBrush;
        return state switch
        {
            SessionState.Playing => LiveBrush,
            SessionState.Connecting or SessionState.Reconnecting => WarnBrush,
            SessionState.Failed => FailedBrush,
            _ => IdleBrush,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
