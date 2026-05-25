using System.Threading.Tasks;
using Avalonia.Controls;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ConfirmDialogContent : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> Completion => _tcs.Task;

    public ConfirmDialogContent()
    {
        InitializeComponent();
    }

    public void Configure(string title, string message, string confirmLabel, string cancelLabel)
    {
        this.FindControl<TextBlock>("TitleBlock")!.Text = title;
        this.FindControl<TextBlock>("MessageBlock")!.Text = message;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        confirm.Content = confirmLabel;
        cancel.Content = cancelLabel;
        confirm.Click += (_, _) => _tcs.TrySetResult(true);
        cancel.Click += (_, _) => _tcs.TrySetResult(false);
    }
}
