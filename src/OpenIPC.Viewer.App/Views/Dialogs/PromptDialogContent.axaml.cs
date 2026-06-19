using System.Threading.Tasks;
using Avalonia.Controls;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class PromptDialogContent : UserControl
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private TextBox? _input;

    public Task<string?> Completion => _tcs.Task;

    public PromptDialogContent()
    {
        InitializeComponent();
    }

    public void Configure(string title, string initial, string okLabel, string cancelLabel)
    {
        this.FindControl<TextBlock>("TitleBlock")!.Text = title;
        _input = this.FindControl<TextBox>("InputBox")!;
        _input.Text = initial;

        var ok = this.FindControl<Button>("OkButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        ok.Content = okLabel;
        cancel.Content = cancelLabel;
        ok.Click += (_, _) =>
        {
            var text = _input.Text?.Trim();
            _tcs.TrySetResult(string.IsNullOrEmpty(text) ? null : text);
        };
        cancel.Click += (_, _) => _tcs.TrySetResult(null);

        Loaded += (_, _) => _input.Focus();
    }
}
