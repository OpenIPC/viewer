using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        DataContextChanged += OnDataContextChanged;
    }

    // A new page of rows starts at the top of the list.
    private CameraLibraryPageViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CameraLibraryPageViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraLibraryPageViewModel.CurrentPage))
            ListScroll.Offset = default;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CameraLibraryPageViewModel vm)
            return;

        if (!vm.IsLoaded)
            await vm.LoadAsync(CancellationToken.None);
        else
        {
            // Coming back to the page (e.g. from a camera view) — the list is
            // already loaded, but the status dots and layout membership may be
            // stale (layouts are edited on the Live page).
            await vm.RefreshLayoutMembershipAsync();
            await vm.ReprobeReachabilityAsync();
        }
    }

    // --- Row menu ("⋯", or right-click a row) -----------------------------
    // The menu is the row's ContextFlyout; Opening captures which row it's for
    // and fills the per-layout submenu.
    private CameraRowViewModel? _menuRow;

    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        button.FindAncestorOfType<Border>(includeSelf: false)?.ContextFlyout?.ShowAt(button);
    }

    private void OnRowMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout || DataContext is not CameraLibraryPageViewModel vm) return;
        _menuRow = flyout.Target?.DataContext as CameraRowViewModel;
        if (_menuRow is not { } row) return;

        foreach (var item in flyout.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, "files"))
                item.IsVisible = vm.IsFileManagerEnabled;
            else if (Equals(item.Tag, "layouts"))
                FillLayoutsSubmenu(item, vm, row);
        }
    }

    private static void FillLayoutsSubmenu(MenuItem parent, CameraLibraryPageViewModel vm, CameraRowViewModel row)
    {
        parent.Items.Clear();
        foreach (var layout in vm.AllLayouts)
        {
            var item = new MenuItem
            {
                Header = layout.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = row.IsInLayout(layout.Id),
            };
            item.Click += async (_, _) => await vm.ToggleLayoutMembershipAsync(row, layout);
            parent.Items.Add(item);
        }
        parent.IsEnabled = parent.Items.Count > 0;
    }

    private async void OnRowEditClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.EditCameraCommand.ExecuteAsync(row));

    private async void OnRowDeviceClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.OpenFirmwareCommand.ExecuteAsync(row));

    private async void OnRowFilesClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.OpenFileManagerCommand.ExecuteAsync(row));

    private async void OnRowDeleteClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.DeleteCameraCommand.ExecuteAsync(row));

    private async Task RunOnMenuRowAsync(Func<CameraLibraryPageViewModel, CameraRowViewModel, Task> action)
    {
        try
        {
            if (DataContext is not CameraLibraryPageViewModel vm || _menuRow is not { } row) return;
            _menuRow = null;
            await action(vm, row);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CameraLibraryPage] row menu action failed: {ex}");
        }
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
