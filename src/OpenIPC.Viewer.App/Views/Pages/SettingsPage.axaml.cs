using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class SettingsPage : UserControl
{
    // Threshold below which we collapse to a single-column stack with
    // master-list ↔ detail navigation. 700px lands between a phone in
    // landscape (~640 logical) and a small tablet portrait (~768).
    private const double WideThreshold = 700;

    private SettingsPageViewModel? _vm;
    private bool _relayoutQueued;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => ApplyLayout();
        SizeChanged += OnSizeChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as SettingsPageViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            // Hydrate the credential-sync passphrase from the secrets store (async,
            // can't run in the VM's synchronous Load()).
            _ = _vm.LoadConfigSyncSecretAsync();
            ApplyLayout();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Column widths depend on which pane is visible. Re-apply whenever
        // visibility flips (narrow list↔detail toggle) so the active pane
        // takes full width instead of leaving a zero-width gap.
        if (e.PropertyName is nameof(SettingsPageViewModel.ShowList)
                           or nameof(SettingsPageViewModel.ShowDetail))
        {
            ApplyLayout();
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm is null) return;
        var nextWide = Bounds.Width >= WideThreshold;
        if (nextWide != _vm.IsWide)
            _vm.IsWide = nextWide;  // triggers ShowList/ShowDetail change → ApplyLayout
        else
            ApplyLayout();

        // During an interactive resize Avalonia can render with a stale
        // arrange pass and (rarely, seen on Windows) miss the final one,
        // leaving the detail pane clipped at the window edge. Queue a single
        // low-priority re-apply + re-measure so a definitive layout pass runs
        // after the resize storm settles. Coalesced: one pending post at most.
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _relayoutQueued = false;
            ApplyLayout();
            RootGrid.InvalidateMeasure();
        }, DispatcherPriority.Background);
    }

    private void ApplyLayout()
    {
        if (_vm is null || RootGrid.ColumnDefinitions.Count < 2) return;
        var list = RootGrid.ColumnDefinitions[0];
        var detail = RootGrid.ColumnDefinitions[1];
        if (_vm.IsWide)
        {
            // Two-pane: fixed-width sidebar + flexible detail.
            list.Width = new GridLength(240);
            detail.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // Single-pane: whichever pane is currently visible takes the full
            // width; the other collapses to zero so it doesn't reserve space.
            list.Width = _vm.ShowList ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            detail.Width = _vm.ShowDetail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }
}
