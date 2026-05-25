using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using OpenIPC.Viewer.App.Messages;

namespace OpenIPC.Viewer.App.Views;

public sealed partial class MainWindow : Window
{
    private WindowState _previousState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
            return;

        var current = (WindowState)e.NewValue!;
        if (current == WindowState.Minimized && _previousState != WindowState.Minimized)
            WeakReferenceMessenger.Default.Send(new WindowMinimizedMessage());
        else if (_previousState == WindowState.Minimized && current != WindowState.Minimized)
            WeakReferenceMessenger.Default.Send(new WindowRestoredMessage());

        _previousState = current;
    }
}
