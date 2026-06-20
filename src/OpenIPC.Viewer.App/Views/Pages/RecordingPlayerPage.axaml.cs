using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class RecordingPlayerPage : UserControl
{
    public RecordingPlayerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private RecordingPlayerPageViewModel? Vm => DataContext as RecordingPlayerPageViewModel;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ActivateAsync(CancellationToken.None);
    }

    private async void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        // Page VM is owned by MainWindowViewModel, which disposes it on
        // navigation; Unloaded only fires on teardown, so nothing to do here
        // beyond the safety net the VM's idempotent DisposeAsync provides.
        if (Vm is { } vm)
            await vm.DisposeAsync();
    }
}
