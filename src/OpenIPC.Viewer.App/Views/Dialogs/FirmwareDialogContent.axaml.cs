using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class FirmwareDialogContent : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> Completion => _tcs.Task;

    public FirmwareDialogContent()
    {
        InitializeComponent();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => _tcs.TrySetResult(true);
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is FirmwareDialogViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        };
    }
}
