using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views.Pages;

public sealed partial class GridPage : UserControl
{
    // In-process custom format — payload is the source tile's Tiles index as
    // a decimal string. Tile reorder never crosses process boundaries, so
    // InProcessFormat skips clipboard/native plumbing entirely. DataFormat<T>
    // is constrained to reference types, so we ship the int as a string.
    // (StringApplicationFormat rejects slashes in the identifier — dot is OK.)
    private static readonly DataFormat<string> TileIndexFormat =
        DataFormat.CreateInProcessFormat<string>("openipc-viewer.grid-tile-index");

    // Same in-process pattern for reordering the layout tabs (Phase 19.1).
    private static readonly DataFormat<string> LayoutIndexFormat =
        DataFormat.CreateInProcessFormat<string>("openipc-viewer.layout-tab-index");

    // A drag must NOT start on plain press — DoDragDropAsync engages a drag
    // session that swallows the Tapped gesture (which is how tab-switch and
    // tile-open fire). We arm on press and only begin the drag once the pointer
    // travels past this threshold, so a clean click still taps.
    private const double DragThreshold = 4;
    private OpenIPC.Viewer.Core.Entities.GridLayout? _pressedLayout;
    private CameraTileViewModel? _pressedTile;
    private Point _pressPoint;
    // DoDragDropAsync needs the originating press args, so we hold them until the
    // pointer moves past the threshold and the drag actually begins.
    private PointerPressedEventArgs? _pressArgs;

    public GridPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

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

    // --- Layout tabs (Phase 19.1) ----------------------------------------
    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: OpenIPC.Viewer.Core.Entities.GridLayout layout }
            && DataContext is GridPageViewModel vm)
            vm.SwitchLayoutCommand.Execute(layout);
    }

    // Arm only — the drag begins in OnTabPointerMoved past the threshold so a
    // plain click still raises Tapped (→ SwitchLayoutCommand).
    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source) return;
        if (source.DataContext is not OpenIPC.Viewer.Core.Entities.GridLayout layout) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        _pressedLayout = layout;
        _pressPoint = e.GetPosition(this);
        _pressArgs = e;
    }

    private async void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedLayout is not { } layout || _pressArgs is not { } pressArgs) return;
        if (sender is not Control source) return;
        if (DataContext is not GridPageViewModel vm) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { _pressedLayout = null; _pressArgs = null; return; }
        if (!MovedPastThreshold(e)) return;

        _pressedLayout = null;
        _pressArgs = null;
        var index = -1;
        for (var i = 0; i < vm.Layouts.Count; i++)
            if (vm.Layouts[i].Id == layout.Id) { index = i; break; }
        if (index < 0) return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(LayoutIndexFormat, index.ToString(CultureInfo.InvariantCulture)));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GridPage] tab drag start failed: {ex}");
        }
    }

    private bool MovedPastThreshold(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return Math.Abs(p.X - _pressPoint.X) >= DragThreshold
            || Math.Abs(p.Y - _pressPoint.Y) >= DragThreshold;
    }

    private void OnTabAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control tab) return;
        DragDrop.AddDragOverHandler(tab, OnTabDragOver);
        DragDrop.AddDropHandler(tab, OnTabDrop);
    }

    private void OnTabDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(LayoutIndexFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTabDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (sender is not Control { DataContext: OpenIPC.Viewer.Core.Entities.GridLayout target }) return;
            if (DataContext is not GridPageViewModel vm) return;

            var raw = e.DataTransfer.TryGetValue(LayoutIndexFormat);
            if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                return;

            var to = -1;
            for (var i = 0; i < vm.Layouts.Count; i++)
                if (vm.Layouts[i].Id == target.Id) { to = i; break; }
            if (to < 0) return;

            await vm.MoveLayoutAsync(from, to, CancellationToken.None);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GridPage] tab drop failed: {ex}");
        }
    }

    // Enter the desktop kiosk fullscreen (Phase 20). MainWindowViewModel owns
    // the state; F11/Esc toggle it too. Exit chrome stays hidden by design —
    // the guard sees just the grid.
    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
        => WeakReferenceMessenger.Default.Send(new ToggleKioskMessage());

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CameraTileViewModel tile }) return;

        // The Tapped gesture bubbles up from inner controls, and Buttons don't
        // mark it handled — so a tap on the Snapshot/Listen button would also
        // open the camera. Ignore taps that originate inside a Button.
        if (e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().TakeWhile(v => v != sender).OfType<Button>().Any())
            return;

        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(tile.Camera.Id));
    }

    // Drag start. Source is the inner tile Grid (DataContext is the tile VM);
    // we look up its index in Tiles via the parent ItemsControl so VM-driven
    // reorders stay the single source of truth for ordering.
    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control source) return;
        if (source.DataContext is not CameraTileViewModel tile) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        _pressedTile = tile;
        _pressPoint = e.GetPosition(this);
        _pressArgs = e;
    }

    private async void OnTilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTile is not { } tile || _pressArgs is not { } pressArgs) return;
        if (sender is not Control source) return;
        if (DataContext is not GridPageViewModel vm) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { _pressedTile = null; _pressArgs = null; return; }
        if (!MovedPastThreshold(e)) return;

        _pressedTile = null;
        _pressArgs = null;
        var index = vm.Tiles.IndexOf(tile);
        if (index < 0) return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(TileIndexFormat, index.ToString(CultureInfo.InvariantCulture)));
            await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GridPage] drag start failed: {ex}");
        }
    }

    // Each slot Border gets DragOver/Drop attached once it enters the tree.
    // Using AttachedToVisualTree keeps the handlers paired with the visual
    // (re-templated items get fresh wiring) without a global Topmost handler.
    private void OnSlotAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control slot) return;
        DragDrop.AddDragOverHandler(slot, OnSlotDragOver);
        DragDrop.AddDropHandler(slot, OnSlotDrop);
    }

    private void OnSlotDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(TileIndexFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSlotDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (sender is not Control slot) return;
            if (DataContext is not GridPageViewModel vm) return;

            // Empty placeholder slots have a null DataContext — dropping there
            // is a no-op (no underlying camera to swap with).
            if (slot.DataContext is not CameraTileViewModel target) return;

            var raw = e.DataTransfer.TryGetValue(TileIndexFormat);
            if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                return;

            var to = vm.Tiles.IndexOf(target);
            if (to < 0) return;

            await vm.MoveTileAsync(from, to, CancellationToken.None);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GridPage] drop failed: {ex}");
        }
    }
}
