using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpenIPC.Viewer.App.Controls;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class SshTerminalContent : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private TerminalView? _term;
    private bool _started;

    public Task<bool> Completion => _tcs.Task;

    public SshTerminalContent()
    {
        InitializeComponent();
        _term = this.FindControl<TerminalView>("Term");
        var close = this.FindControl<Button>("CloseButton")!;
        close.Click += OnClose;
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Desktop hosts in a Window; mobile in an overlay awaiting Completion.
        if (VisualRoot is Window w)
            w.Close();
        else
            _tcs.TrySetResult(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started || DataContext is not SshTerminalViewModel vm || _term is null)
            return;
        _started = true;

        _term.Emulator = vm.Emulator;
        _term.Input += (_, text) => _ = vm.SendAsync(text);
        _term.GridResized += (_, g) => _ = vm.ResizeAsync(g.Columns, g.Rows);

        _ = vm.ConnectAsync();
        _term.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tcs.TrySetResult(true);
        if (DataContext is SshTerminalViewModel vm)
            _ = vm.DisposeAsync();
    }
}
