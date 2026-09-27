using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class EventsPage : UserControl
{
    public EventsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    // A new page of rows starts at the top of the list.
    private EventsPageViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as EventsPageViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventsPageViewModel.CurrentPage))
            ListScroll.Offset = default;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is EventsPageViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EventsPage] OnLoaded failed: {ex}");
        }
    }
}
