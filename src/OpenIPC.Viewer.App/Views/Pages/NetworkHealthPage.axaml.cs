using Avalonia;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public partial class NetworkHealthPage : UserControl
{
    public NetworkHealthPage() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is NetworkHealthPageViewModel vm)
            _ = vm.StartAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is NetworkHealthPageViewModel vm)
            vm.Stop();
    }
}
