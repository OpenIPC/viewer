using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class GridPage : UserControl
{
    public GridPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // Wrapped in try/catch because async void hides exceptions from the framework.
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is GridPageViewModel vm)
                await vm.LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GridPage] OnLoaded failed: {ex}");
        }
    }

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: CameraTileViewModel tile })
            WeakReferenceMessenger.Default.Send(new OpenCameraMessage(tile.Camera.Id));
    }
}
