using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class HealthDialog : Window
{
    public HealthDialog()
    {
        InitializeComponent();
        var content = this.FindControl<HealthCenterContent>("InnerContent")!;
        _ = content.Completion.ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => Close()),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
