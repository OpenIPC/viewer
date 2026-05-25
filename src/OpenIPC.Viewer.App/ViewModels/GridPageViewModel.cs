using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class GridPageViewModel : ViewModelBase,
    IRecipient<WindowMinimizedMessage>,
    IRecipient<WindowRestoredMessage>,
    IAsyncDisposable
{
    private readonly CameraDirectoryService _directory;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GridPageViewModel> _logger;

    private IReadOnlyList<Camera> _allCameras = Array.Empty<Camera>();
    private bool _minimized;

    public string Title => "Live";

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = new();
    public ObservableCollection<CameraTileViewModel?> Slots { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns))]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private int _layoutSize = 2;

    public int Columns => LayoutSize;
    public int Rows => LayoutSize;

    public GridPageViewModel(
        CameraDirectoryService directory,
        LiveStreamCoordinator coordinator,
        ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _coordinator = coordinator;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GridPageViewModel>();

        WeakReferenceMessenger.Default.Register<WindowMinimizedMessage>(this);
        WeakReferenceMessenger.Default.Register<WindowRestoredMessage>(this);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_minimized) return;
        _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        await RefreshTilesAsync(ct).ConfigureAwait(true);
    }

    // Parameter is string because XAML CommandParameter literals are strings; using
    // int here would make AsyncRelayCommand<int>.Execute throw at first render.
    [RelayCommand]
    private async Task SetLayoutAsync(string size)
    {
        if (!int.TryParse(size, out var n) || n < 1 || n > 3) return;
        LayoutSize = n;
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshTilesAsync(CancellationToken ct)
    {
        var capacity = LayoutSize * LayoutSize;
        var visible = _allCameras.Where(c => c.IncludedInGrid).Take(capacity).ToList();
        var visibleIds = visible.Select(c => c.Id).ToHashSet();

        // Drop tiles that aren't in the new visible set.
        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(Tiles[i].Camera.Id))
            {
                var stale = Tiles[i];
                Tiles.RemoveAt(i);
                try { await stale.DisposeAsync().ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile"); }
            }
        }

        // Add new ones.
        foreach (var camera in visible)
        {
            if (Tiles.Any(t => t.Camera.Id == camera.Id))
                continue;
            var tile = new CameraTileViewModel(camera, _coordinator, _directory, _loggerFactory.CreateLogger<CameraTileViewModel>());
            Tiles.Add(tile);
            try { await tile.ActivateAsync(ct).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to activate tile for {Camera}", camera.Name); }
        }

        // Slots includes nulls so the grid renders empty placeholders.
        Slots.Clear();
        for (var i = 0; i < capacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);
    }

    public async void Receive(WindowMinimizedMessage message)
    {
        _minimized = true;
        await ReleaseAllAsync().ConfigureAwait(true);
    }

    public async void Receive(WindowRestoredMessage message)
    {
        _minimized = false;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task ReleaseAllAsync()
    {
        var copy = Tiles.ToArray();
        Tiles.Clear();
        Slots.Clear();
        foreach (var tile in copy)
        {
            try { await tile.DisposeAsync().ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile during minimize"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        await ReleaseAllAsync().ConfigureAwait(false);
    }
}
