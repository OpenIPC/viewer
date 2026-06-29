using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class HealthCenterContent : UserControl
{
    // No per-action result — the caller awaits "user dismissed". CloseButton
    // flips the TCS; Refresh mutates the VM in place.
    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> Completion => _tcs.Task;

    public HealthCenterContent()
    {
        InitializeComponent();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => _tcs.TrySetResult(true);
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is HealthCenterViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        };
    }
}
