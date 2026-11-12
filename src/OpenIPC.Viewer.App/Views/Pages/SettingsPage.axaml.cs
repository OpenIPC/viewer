using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // Tunnel so Right is claimed before anything else; bubble for Left so a text
        // field keeps the key when it still has somewhere to move the caret.
        AddHandler(KeyDownEvent, OnDirectionKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            ApplyLayout();
            // Layouts may have changed on the Live page since the last visit;
            // the Windows notification state may have changed in Windows Settings.
            if (_vm is not null)
            {
                _ = _vm.LoadStartupLayoutsAsync();
                _ = _vm.Notifications.LoadAsync();
            }
        };
        // Don't lose a debounced edit when the user navigates away mid-typing.
        DetachedFromVisualTree += (_, _) =>
        {
            _ = _vm?.Notifications.FlushAsync();
            _vm?.LeaveTransfer();
        };
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
            _ = _vm.LoadStartupLayoutsAsync();
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

    // The section list and the controls in the detail pane don't line up, so a TV
    // remote's Right never finds Find a PC (or any other detail control) on its own.
    // Right or OK from the list steps into the open section; Left from its first
    // control steps back.
    private void OnDirectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is null) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focus) return;

        if (e.Route == RoutingStrategies.Tunnel)
        {
            if (e.Key is Key.Right or Key.Enter or Key.Space && IsInSectionList(focus)
                && FirstFocusable(DetailScroller) is Control target)
            {
                target.Focus(NavigationMethod.Directional);
                target.BringIntoView();
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Left || !IsInDetail(focus)) return;
        var next = TopLevel.GetTopLevel(this)?.FocusManager?.FindNextElement(
            NavigationDirection.Left,
            new FindNextElementOptions { SearchRoot = DetailScroller });
        if (next is not null) return;
        FocusSectionList();
        e.Handled = true;
    }

    private bool IsInSectionList(Visual focus) =>
        ReferenceEquals(focus, SectionList) || SectionList.IsVisualAncestorOf(focus);

    private bool IsInDetail(Visual focus) =>
        ReferenceEquals(focus, DetailScroller) || DetailScroller.IsVisualAncestorOf(focus);

    private void FocusSectionList()
    {
        // TryGoBack clears the selection, so the row has to be captured first.
        var index = SectionList.SelectedIndex;
        if (_vm is { IsWide: false })
            _vm.TryGoBack();
        Dispatcher.UIThread.Post(() =>
        {
            if (SectionList.ContainerFromIndex(index) is InputElement item)
                item.Focus(NavigationMethod.Directional);
            else
                SectionList.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    private static Control? FirstFocusable(Control root) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(e =>
            e.Focusable && e.IsEffectivelyVisible && e.IsEffectivelyEnabled
            && KeyboardNavigation.GetIsTabStop(e));
}
