using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels.Dialogs;

namespace OpenIPC.Viewer.App.Views.Dialogs;

public sealed partial class DiscoveryDialogContent : UserControl
{
    private readonly TaskCompletionSource<DiscoveryDialogResult?> _tcs = new();

    public Task<DiscoveryDialogResult?> Completion => _tcs.Task;

    // Raised when the header is pressed, so a borderless host window can drag.
    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    public DiscoveryDialogContent()
    {
        InitializeComponent();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Finish(null);
        this.FindControl<Button>("ManualLink")!.Click += (_, _) => Finish(new DiscoveryDialogResult(null, null, ManualEntry: true));
        this.FindControl<Button>("EmptyManualButton")!.Click += (_, _) => Finish(new DiscoveryDialogResult(null, null, ManualEntry: true));
        this.FindControl<Border>("HeaderBar")!.PointerPressed += (_, e) => HeaderPressed?.Invoke(this, e);

        // Escape closes; the window has no Cancel button to carry IsCancel.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(null); e.Handled = true; }
        };

        // A fresh session starts scanning the moment the dialog shows.
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is DiscoveryDialogViewModel vm)
                await vm.StartAsync();
        };
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiscoveryDialogViewModel vm
            || (sender as Control)?.DataContext is not DiscoveredDeviceRowVm row) return;
        Finish(vm.BuildResult(row));
    }

    private void Finish(DiscoveryDialogResult? result)
    {
        (DataContext as DiscoveryDialogViewModel)?.Cancel();
        _tcs.TrySetResult(result);
    }
}
