using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class CameraLibraryPage : UserControl
{
    public CameraLibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraLibraryPageViewModel vm)
            return;

        if (!vm.IsLoaded)
            await vm.LoadAsync(CancellationToken.None);
        else
            // Coming back to the page (e.g. from a camera view) — the list is
            // already loaded, but the online/offline badges may be stale.
            await vm.ReprobeReachabilityAsync();
    }

    private void OnCameraCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not Control { DataContext: CameraRowViewModel row }) return;
        if (DataContext is not CameraLibraryPageViewModel vm) return;

        // Tapped bubbles up from inner Buttons / CheckBox. Skip if the original
        // source sits inside one of those interactive controls.
        if (IsInsideInteractive(e.Source as Visual, sender as Visual))
            return;

        if (vm.OpenCameraCommand.CanExecute(row))
            vm.OpenCameraCommand.Execute(row);
    }

    private static bool IsInsideInteractive(Visual? source, Visual? stopAt)
    {
        for (var v = source; v is not null && v != stopAt; v = v.GetVisualParent())
        {
            if (v is Button or ToggleButton or CheckBox or TextBox)
                return true;
        }
        return false;
    }
}
