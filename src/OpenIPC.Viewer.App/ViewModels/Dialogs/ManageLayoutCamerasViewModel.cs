using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

/// <summary>
/// Phase 19.1 polish — a friendly camera picker for a single layout. Lists every
/// camera in the library with an "in this layout" checkbox; toggling adds/removes
/// the tile against the live repository (a camera can belong to many layouts, so
/// this never touches its membership in other tabs).
/// </summary>
public sealed partial class ManageLayoutCamerasViewModel : ViewModelBase
{
    private readonly LayoutId _layoutId;
    private readonly string _layoutName;
    private readonly ILayoutRepository _layouts;
    private readonly CameraDirectoryService _directory;
    private readonly ILogger<ManageLayoutCamerasViewModel> _logger;

    // Master list; Cameras is the search-filtered view bound by the UI.
    private readonly List<LayoutCameraRowViewModel> _all = new();

    public ObservableCollection<LayoutCameraRowViewModel> Cameras { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string? _errorMessage;

    public string TitleText =>
        string.Format(CultureInfo.CurrentCulture, Localizer.Instance["LayoutCameras.TitleFormat"], _layoutName);

    public ManageLayoutCamerasViewModel(
        LayoutId layoutId,
        string layoutName,
        ILayoutRepository layouts,
        CameraDirectoryService directory,
        ILogger<ManageLayoutCamerasViewModel> logger)
    {
        _layoutId = layoutId;
        _layoutName = layoutName;
        _layouts = layouts;
        _directory = directory;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        var members = (await _layouts.GetTilesAsync(_layoutId, ct).ConfigureAwait(true)).ToHashSet();

        _all.Clear();
        foreach (var c in cameras)
            _all.Add(new LayoutCameraRowViewModel(c, members.Contains(c.Id), this));

        TotalCount = _all.Count;
        ApplyFilter();
        UpdateCount();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = SearchText?.Trim() ?? "";
        Cameras.Clear();
        foreach (var row in _all)
        {
            if (q.Length == 0
                || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                Cameras.Add(row);
        }
    }

    private void UpdateCount() => SelectedCount = _all.Count(r => r.IsInLayout);

    // Called by a row when its checkbox flips. Persists the single change so the
    // dialog is "apply as you go" — closing just dismisses.
    public async Task SetMembershipAsync(CameraId id, bool inLayout)
    {
        try
        {
            if (inLayout)
                await _layouts.AddTileAsync(_layoutId, id, CancellationToken.None).ConfigureAwait(true);
            else
                await _layouts.RemoveTileAsync(_layoutId, id, CancellationToken.None).ConfigureAwait(true);
            ErrorMessage = null;
            UpdateCount();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set layout membership for {CameraId}", id);
            ErrorMessage = ex.Message;
        }
    }
}

public sealed partial class LayoutCameraRowViewModel : ViewModelBase
{
    private readonly ManageLayoutCamerasViewModel _owner;

    public CameraId Id { get; }
    public string Name { get; }
    public string Subtitle { get; }

    [ObservableProperty] private bool _isInLayout;

    public LayoutCameraRowViewModel(Camera camera, bool isInLayout, ManageLayoutCamerasViewModel owner)
    {
        Id = camera.Id;
        Name = camera.Name;
        Subtitle = camera.Host;
        _owner = owner;
        _isInLayout = isInLayout;
    }

    partial void OnIsInLayoutChanged(bool value) => _ = _owner.SetMembershipAsync(Id, value);
}
