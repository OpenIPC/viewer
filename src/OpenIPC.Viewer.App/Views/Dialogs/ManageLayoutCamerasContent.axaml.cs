using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class ManageLayoutCamerasContent : UserControl
{
    // No per-action result — the caller awaits "user dismissed". Each row's
    // checkbox mutates the layout against the live repository as it toggles.
    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> Completion => _tcs.Task;

    public ManageLayoutCamerasContent()
    {
        InitializeComponent();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => _tcs.TrySetResult(true);
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ManageLayoutCamerasViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        };
    }
}
