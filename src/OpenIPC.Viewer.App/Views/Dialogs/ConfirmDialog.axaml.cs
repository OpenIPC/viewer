using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public void Configure(string title, string message, string confirmLabel, string cancelLabel)
    {
        Title = title;
        var content = this.FindControl<ConfirmDialogContent>("InnerContent")!;
        content.Configure(title, message, confirmLabel, cancelLabel);
        _ = content.Completion.ContinueWith(t =>
            Dispatcher.UIThread.Post(() => Close(t.Result)),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
