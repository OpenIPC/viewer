using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class FirmwareDialog : Window
{
    public FirmwareDialog()
    {
        InitializeComponent();
        var content = this.FindControl<FirmwareDialogContent>("InnerContent")!;
        _ = content.Completion.ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => Close()),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
