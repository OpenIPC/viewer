using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class PromptDialog : Window
{
    public PromptDialog()
    {
        InitializeComponent();
    }

    public void Configure(string title, string initial, string okLabel, string cancelLabel)
    {
        Title = title;
        var content = this.FindControl<PromptDialogContent>("InnerContent")!;
        content.Configure(title, initial, okLabel, cancelLabel);
        _ = content.Completion.ContinueWith(t =>
            Dispatcher.UIThread.Post(() => Close(t.Result)),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
