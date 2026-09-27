using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.App.ViewModels;

// A camera as the picker lists it.
public sealed record CameraPickSource(CameraId Id, string Name, string Host, string? Group);

// Searchable multi-select camera filter shared by the Events and Recordings
// pages (rendered by Controls/CameraPickerView). The page feeds it every camera
// plus a per-camera count of the items it lists (events, recordings); by default
// the picker only offers cameras with a non-zero count, busiest first.
public sealed partial class CameraPickerViewModel : ObservableObject
{
    private readonly string _footerFormatKey;
    private readonly List<CameraPickItem> _items = new();

    // withItemsKey: label of the "only cameras that have something" scope
    // ("With events"); footerFormatKey: "{0} of {1} with events".
    public CameraPickerViewModel(string withItemsKey, string footerFormatKey)
    {
        WithItemsLabel = Localizer.Instance[withItemsKey];
        _footerFormatKey = footerFormatKey;
        UpdateButtonLabel();
    }

    public string WithItemsLabel { get; }

    public ObservableCollection<CameraPickItem> PickerItems { get; } = new();
    public ObservableCollection<CameraPickItem> SelectedCameras { get; } = new();

    // Raised after the ticked set changes.
    public event Action? SelectionChanged;

    [ObservableProperty] private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWithItems))]
    private bool _showAll;

    public bool IsWithItems { get => !ShowAll; set => ShowAll = !value; }

    [ObservableProperty] private string _footer = "";

    // "Cameras: all" / the single camera's name / "Cameras: 2"
    [ObservableProperty] private string _buttonLabel = "";

    public bool HasSelection => SelectedCameras.Count > 0;

    partial void OnSearchChanged(string value) => Rebuild();
    partial void OnShowAllChanged(bool value) => Rebuild();

    // True when nothing is ticked (no filter) or the camera is ticked.
    public bool Matches(CameraId id) => SelectedCameras.Count == 0 || SelectedCameras.Any(i => i.Id == id);

    // Replaces the camera list, keeping ticks on cameras that still exist.
    public void SetCameras(IEnumerable<CameraPickSource> cameras)
    {
        var keep = SelectedCameras.Select(i => i.Id).ToHashSet();
        SelectedCameras.Clear();
        _items.Clear();
        foreach (var c in cameras)
        {
            var item = new CameraPickItem(c.Id, c.Name, c.Host, c.Group, OnToggled);
            item.SetSelectedSilently(keep.Contains(c.Id));
            if (item.IsSelected) SelectedCameras.Add(item);
            _items.Add(item);
        }
        UpdateButtonLabel();
        Rebuild();
    }

    // Per-camera counts (the page computes them ignoring the camera filter, so
    // unticked cameras still show how busy they are).
    public void SetCounts(IReadOnlyDictionary<CameraId, int> counts)
    {
        var max = counts.Count == 0 ? 0 : counts.Values.Max();
        var changed = false;
        foreach (var item in _items)
        {
            var c = counts.TryGetValue(item.Id, out var n) ? n : 0;
            if (c != item.Count) changed = true;
            item.SetCount(c, max);
        }
        // Ticking a camera doesn't move counts — leave an open picker alone
        // then, so the row under the pointer isn't rebuilt mid-click.
        if (changed || PickerItems.Count == 0) Rebuild();
    }

    // Ticks one camera (e.g. clicking its name in a list row).
    public void Select(CameraId id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is not null && !item.IsSelected) item.IsSelected = true;
    }

    [RelayCommand]
    private void Remove(CameraPickItem? item)
    {
        if (item is not null) item.IsSelected = false; // → OnToggled
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var item in SelectedCameras.ToList()) item.IsSelected = false;
    }

    private void OnToggled(CameraPickItem item)
    {
        if (item.IsSelected && !SelectedCameras.Contains(item)) SelectedCameras.Add(item);
        else if (!item.IsSelected) SelectedCameras.Remove(item);
        UpdateButtonLabel();
        SelectionChanged?.Invoke();
    }

    private void UpdateButtonLabel()
    {
        ButtonLabel = SelectedCameras.Count switch
        {
            0 => Localizer.Instance["Events.Cameras.All"],
            1 => SelectedCameras[0].Name,
            var n => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Events.Cameras.CountFormat"], n),
        };
        OnPropertyChanged(nameof(HasSelection));
    }

    // Visible rows: search over name / host / group; busiest first. The default
    // scope hides cameras with nothing to show (ticked ones always stay).
    private void Rebuild()
    {
        var q = Search?.Trim() ?? "";
        var items = _items.Where(i => i.Matches(q));
        if (!ShowAll) items = items.Where(i => i.Count > 0 || i.IsSelected);
        PickerItems.Clear();
        foreach (var i in items.OrderByDescending(i => i.Count).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            PickerItems.Add(i);
        Footer = string.Format(CultureInfo.CurrentCulture, Localizer.Instance[_footerFormatKey],
            _items.Count(i => i.Count > 0), _items.Count);
    }
}

// One camera in the picker: its count (with a proportional bar) and whether
// it's part of the filter.
public sealed partial class CameraPickItem : ObservableObject
{
    private const double BarMax = 60;

    private readonly Action<CameraPickItem> _onToggled;
    private bool _silent;

    public CameraPickItem(CameraId id, string name, string host, string? group, Action<CameraPickItem> onToggled)
    {
        Id = id;
        Name = name;
        Host = host;
        Group = group;
        _onToggled = onToggled;
    }

    public CameraId Id { get; }
    public string Name { get; }
    public string Host { get; }
    public string? Group { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _barWidth;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_silent) _onToggled(this);
    }

    public void SetSelectedSilently(bool value)
    {
        _silent = true;
        try { IsSelected = value; }
        finally { _silent = false; }
    }

    public void SetCount(int count, int max)
    {
        Count = count;
        BarWidth = count == 0 || max == 0 ? 0 : Math.Max(3, BarMax * count / max);
    }

    internal bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Host.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (Group?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}
