using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class RecordingPlayerPage : UserControl
{
    private static readonly TimeSpan ArrowStep = TimeSpan.FromSeconds(5);

    private TopLevel? _topLevel;

    public RecordingPlayerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private RecordingPlayerPageViewModel? Vm => DataContext as RecordingPlayerPageViewModel;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Player hotkeys work wherever focus sits on the page, so listen on the
        // window (tunnel) rather than on a focusable child.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        if (Vm is { } vm)
            await vm.ActivateAsync(CancellationToken.None);
    }

    // Previous/next swap the page VM while this view stays in place (same
    // DataTemplate), so Loaded doesn't fire again — activate the new VM here.
    private async void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (IsLoaded && Vm is { } vm)
            await vm.ActivateAsync(CancellationToken.None);
    }

    private async void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;

        // Page VM is owned by MainWindowViewModel, which disposes it on
        // navigation; Unloaded only fires on teardown, so nothing to do here
        // beyond the safety net the VM's idempotent DisposeAsync provides.
        if (Vm is { } vm)
            await vm.DisposeAsync();
    }

    // Space play/pause, ←/→ ±5 s, ,/. frame step, Home/End jump to the ends,
    // PageUp/PageDown previous/next recording, M mute, F fullscreen. F11 and
    // Esc are the window's own key bindings (MainWindowViewModel routes them
    // to the player's fullscreen while it's open).
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is TextBox) return;

        switch (e.Key)
        {
            case Key.OemComma:
                vm.StepFrame(false);
                break;
            case Key.OemPeriod:
                vm.StepFrame(true);
                break;
            case Key.PageUp:
                if (vm.PreviousCommand.CanExecute(null)) vm.PreviousCommand.Execute(null);
                break;
            case Key.PageDown:
                if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                break;
            case Key.M:
                vm.ToggleMute();
                break;
            case Key.F:
                vm.ToggleFullscreenCommand.Execute(null);
                break;
            case Key.Space:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                _ = vm.SeekRelativeAsync(-ArrowStep);
                break;
            case Key.Right:
                _ = vm.SeekRelativeAsync(ArrowStep);
                break;
            case Key.Home:
                _ = vm.SeekToStartAsync();
                break;
            case Key.End:
                _ = vm.SeekToEndAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Phone speed pill: the rates as a one-tap menu above the button.
    private void OnSpeedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || Vm is not { } vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        foreach (var option in vm.RateOptions)
        {
            menu.Items.Add(new MenuItem
            {
                Header = option.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = option.IsActive,
                Command = vm.SetRateCommand,
                CommandParameter = option.Parameter,
            });
        }
        menu.ShowAt(anchor);
    }
}
