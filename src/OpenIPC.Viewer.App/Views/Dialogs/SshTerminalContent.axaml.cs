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
    private bool _connected;

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
        _term.TerminalFontSize = vm.FontSize;
        _term.Input += (_, text) => _ = vm.SendAsync(text);
        // Defer the connect until the terminal has been measured: SSH.NET's
        // ShellStream can't be resized mid-session, so the PTY keeps whatever
        // size we open it with. Opening at the default 80x24 before layout means
        // a narrow (phone) view gets 80-column output that overflows off-screen.
        // Wait for the first real grid size, then open the shell to match.
        _term.GridResized += OnGridResized;
        _term.Focus();
    }

    private void OnGridResized(object? sender, (int Columns, int Rows) grid)
    {
        if (DataContext is not SshTerminalViewModel vm)
            return;
        _ = vm.ResizeAsync(grid.Columns, grid.Rows);
        // The pre-layout pass reports a 1x1 grid (Bounds still 0) — wait for a
        // real measurement before opening the shell at that size.
        if (!_connected && grid.Columns > 1 && grid.Rows > 1)
        {
            _connected = true;
            _ = vm.ConnectAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tcs.TrySetResult(true);
        if (DataContext is SshTerminalViewModel vm)
            _ = vm.DisposeAsync();
    }
}
