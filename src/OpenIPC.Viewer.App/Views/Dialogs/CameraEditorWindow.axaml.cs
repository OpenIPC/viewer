using Avalonia.Controls;
using Avalonia.Threading;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class CameraEditorWindow : Window
{
    public CameraEditorWindow()
    {
        InitializeComponent();

        // Bridge the embedded content's TCS to Window.Close so desktop's
        // ShowDialog<TResult> still returns when the user picks Save/Cancel.
        // Inner DataContext inherits from this Window's DataContext implicitly,
        // so callers only set DataContext on the window.
        var content = this.FindControl<CameraEditorContent>("InnerContent")!;
        _ = content.Completion.ContinueWith(t =>
            Dispatcher.UIThread.Post(() => Close(t.Result)),
            System.Threading.Tasks.TaskScheduler.Default);
    }
}
