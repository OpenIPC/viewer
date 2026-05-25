using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class RecordingsPage : UserControl
{
    public RecordingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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
}
