using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenIPC.Viewer.App.Views;

public sealed partial class StartupWindow : Window
{
    public StartupWindow() => AvaloniaXamlLoader.Load(this);
}
