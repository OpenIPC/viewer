using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class FileManagerContent : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private bool _started;

    public Task<bool> Completion => _tcs.Task;

    public FileManagerContent()
    {
        InitializeComponent();
        this.FindControl<Button>("CloseButton")!.Click += OnClose;
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (VisualRoot is Window w)
            w.Close();
        else
            _tcs.TrySetResult(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started || DataContext is not FileManagerViewModel vm)
            return;
        _started = true;
        _ = vm.ConnectAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tcs.TrySetResult(true);
        if (DataContext is FileManagerViewModel vm)
            _ = vm.DisposeAsync();
    }
}
