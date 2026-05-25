using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

// The form, extracted from CameraEditorWindow so the same control can host
// inside a Window (desktop) or in TopLevel.OverlayLayer (mobile). Owns the
// TaskCompletionSource that the caller awaits — the Window wrapper bridges
// completion to Window.Close, the overlay path returns it directly.
public sealed partial class CameraEditorContent : UserControl
{
    private readonly TaskCompletionSource<CameraEditorResult?> _tcs = new();

    public Task<CameraEditorResult?> Completion => _tcs.Task;

    public CameraEditorContent()
    {
        InitializeComponent();

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => _tcs.TrySetResult(null);
        this.FindControl<Button>("SaveButton")!.Click += OnSave;
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is CameraEditorViewModel vm)
                await vm.LoadGroupsAsync(CancellationToken.None);
        };
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraEditorViewModel vm) return;
        if (!vm.TryBuildRequest(out var newRequest, out var updateRequest)) return;
        _tcs.TrySetResult(new CameraEditorResult(newRequest, updateRequest));
    }
}
