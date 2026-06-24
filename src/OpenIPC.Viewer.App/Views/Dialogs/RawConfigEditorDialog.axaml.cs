using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class RawConfigEditorDialog : Window
{
    private readonly RawConfigEditorContent _content;

    public RawConfigEditorDialog()
    {
        InitializeComponent();
        _content = this.FindControl<RawConfigEditorContent>("InnerContent")!;
        _ = _content.Completion.ContinueWith(t =>
            Dispatcher.UIThread.Post(() => Close(t.Result)),
            System.Threading.Tasks.TaskScheduler.Default);
    }

    public void SetInitialText(string text) => _content.SetInitialText(text);

    public void SetValidateJson(bool validate) => _content.SetValidateJson(validate);
}
