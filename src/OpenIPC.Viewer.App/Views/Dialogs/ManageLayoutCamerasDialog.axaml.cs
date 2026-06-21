using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ManageLayoutCamerasDialog : Window
{
    public ManageLayoutCamerasDialog()
    {
        InitializeComponent();
        var content = this.FindControl<ManageLayoutCamerasContent>("InnerContent")!;
        _ = content.Completion.ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => Close()),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
