using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class RecordingsPage : UserControl
{
    private RecordingsPageViewModel? _vm;
    private RecordingRowViewModel? _menuRow;

    public RecordingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is RecordingsPageViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RecordingsPage] OnLoaded failed: {ex}");
        }
    }

    // A new page of rows starts at the top of the list.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as RecordingsPageViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingsPageViewModel.CurrentPage))
            ListScroll.Offset = default;
    }

    // Clicking a row plays it; clicks on its buttons are theirs.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: RecordingRowViewModel row } || _vm is null) return;
        for (var v = e.Source as Visual; v is not null && v != sender; v = v.GetVisualParent())
            if (v is Button) return;
        _vm.PlayCommand.Execute(row);
    }

    // --- Row menu ("⋯", or right-click a row) -----------------------------
    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        button.FindAncestorOfType<Border>(includeSelf: false)?.ContextFlyout?.ShowAt(button);
    }

    private void OnRowMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout) return;
        _menuRow = flyout.Target?.DataContext as RecordingRowViewModel;
        foreach (var item in flyout.Items.OfType<MenuItem>())
            if (Equals(item.Tag, "folder"))
                item.IsVisible = _vm?.CanShowInFolder == true;
    }

    private async void OnShowInFolderClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.ShowInFolderCommand.ExecuteAsync(row));

    private async void OnCopyFileClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.CopyFileCommand.ExecuteAsync(row));

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
        => await RunOnMenuRowAsync((vm, row) => vm.DeleteCommand.ExecuteAsync(row));

    private async Task RunOnMenuRowAsync(Func<RecordingsPageViewModel, RecordingRowViewModel, Task> action)
    {
        try
        {
            if (_vm is not { } vm || _menuRow is not { } row) return;
            _menuRow = null;
            await action(vm, row);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RecordingsPage] row menu action failed: {ex}");
        }
    }
}
