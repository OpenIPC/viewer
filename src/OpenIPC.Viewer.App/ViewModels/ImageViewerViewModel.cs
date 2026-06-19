using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed record SnapshotViewEntry(Snapshot Snapshot, string CameraName);

public enum EditTool { Arrow, Rectangle, Text, Crop }

/// <summary>
/// Phase 14.4 — the in-app image viewer. Navigates prev/next over the browser's
/// current filtered set, with zoom / pan / rotate / flip / slideshow and
/// copy / delete. Hosted as a window on desktop and a full-screen overlay on
/// mobile (see <see cref="IDialogService.ShowImageViewerAsync"/>).
/// </summary>
public sealed partial class ImageViewerViewModel : ViewModelBase
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;

    private static readonly TimeSpan SlideshowInterval = TimeSpan.FromSeconds(3);

    // Editor palette (ARGB). Kept small per the phase scope — crop + draw + text.
    public static readonly uint[] Palette =
    {
        0xFFFF3B30, // red
        0xFFFFCC00, // yellow
        0xFF34C759, // green
        0xFFFFFFFF, // white
        0xFF111111, // near-black
    };

    private readonly List<SnapshotViewEntry> _items;
    private readonly ISnapshotRepository _repo;
    private readonly ISnapshotService _snapshots;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ImageViewerViewModel> _logger;
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DispatcherTimer? _slideshowTimer;

    public Task<bool> Completion => _closed.Task;

    /// <summary>True if any snapshot was deleted, so the browser reloads on close.</summary>
    public bool AnyChanges { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterLabel))]
    [NotifyPropertyChangedFor(nameof(CameraName))]
    [NotifyPropertyChangedFor(nameof(TimeLabel))]
    [NotifyPropertyChangedFor(nameof(PropertiesText))]
    private int _index;

    [ObservableProperty] private Bitmap? _currentImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleX))]
    [NotifyPropertyChangedFor(nameof(ScaleY))]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private double _zoom = 1.0;

    [ObservableProperty] private double _rotation;
    [ObservableProperty] private double _translateX;
    [ObservableProperty] private double _translateY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleX))]
    private bool _flipH;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleY))]
    private bool _flipV;

    [ObservableProperty] private bool _isSlideshow;
    [ObservableProperty] private bool _showProperties;

    public double ScaleX => Zoom * (FlipH ? -1 : 1);
    public double ScaleY => Zoom * (FlipV ? -1 : 1);
    public string ZoomLabel => string.Format(CultureInfo.InvariantCulture, "{0:P0}", Zoom);

    public bool HasMultiple => _items.Count > 1;
    public string CounterLabel => $"{Index + 1} / {_items.Count}";

    private SnapshotViewEntry? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;

    public string CameraName => Current?.CameraName ?? string.Empty;
    public string TimeLabel => Current is { } e
        ? e.Snapshot.TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
        : string.Empty;

    public string PropertiesText
    {
        get
        {
            if (Current is not { } e) return string.Empty;
            var res = e.Snapshot.Width > 0 ? $"{e.Snapshot.Width}×{e.Snapshot.Height}" : "—";
            var size = "—";
            try
            {
                if (File.Exists(e.Snapshot.Path))
                {
                    var bytes = new FileInfo(e.Snapshot.Path).Length;
                    size = bytes > 1024 * 1024
                        ? $"{bytes / (1024.0 * 1024):F1} MB"
                        : $"{bytes / 1024.0:F0} KB";
                }
            }
            catch { /* size is best-effort */ }
            return $"{e.CameraName}\n{res}\n{TimeLabel}\n{size}";
        }
    }

    public ImageViewerViewModel(
        IReadOnlyList<SnapshotViewEntry> items,
        int startIndex,
        ISnapshotRepository repo,
        ISnapshotService snapshots,
        IDialogService dialogs,
        ILogger<ImageViewerViewModel> logger)
    {
        _items = new List<SnapshotViewEntry>(items);
        _repo = repo;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _logger = logger;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        ResetView();
        var old = CurrentImage;
        CurrentImage = null;
        old?.Dispose();

        if (Current is not { } e || !File.Exists(e.Snapshot.Path))
            return;
        try
        {
            using var stream = File.OpenRead(e.Snapshot.Path);
            // Cap the decode so a 4K still doesn't blow up mobile memory; still
            // plenty of detail to zoom into.
            CurrentImage = Bitmap.DecodeToWidth(stream, 2560);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode snapshot {Path}", e.Snapshot.Path);
        }
    }

    private void ResetView()
    {
        Zoom = 1.0;
        Rotation = 0;
        TranslateX = 0;
        TranslateY = 0;
        FlipH = false;
        FlipV = false;
    }

    // --- view manipulation (called from code-behind gestures) ---

    public void Pan(double dx, double dy)
    {
        TranslateX += dx;
        TranslateY += dy;
    }

    public void ApplyZoomFactor(double factor)
    {
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
    }

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    // --- commands ---

    [RelayCommand]
    private void Next()
    {
        if (_items.Count == 0) return;
        Index = (Index + 1) % _items.Count;
        LoadCurrent();
    }

    [RelayCommand]
    private void Prev()
    {
        if (_items.Count == 0) return;
        Index = (Index - 1 + _items.Count) % _items.Count;
        LoadCurrent();
    }

    [RelayCommand] private void ZoomIn() => ApplyZoomFactor(1.25);
    [RelayCommand] private void ZoomOut() => ApplyZoomFactor(0.8);
    [RelayCommand] private void FitToScreen() => ResetView();
    [RelayCommand] private void RotateLeft() => Rotation = (Rotation - 90) % 360;
    [RelayCommand] private void RotateRight() => Rotation = (Rotation + 90) % 360;
    [RelayCommand] private void FlipHorizontal() => FlipH = !FlipH;
    [RelayCommand] private void FlipVertical() => FlipV = !FlipV;

    [RelayCommand]
    private void ToggleSlideshow()
    {
        IsSlideshow = !IsSlideshow;
        if (IsSlideshow)
        {
            _slideshowTimer ??= new DispatcherTimer { Interval = SlideshowInterval };
            _slideshowTimer.Tick -= OnSlideshowTick;
            _slideshowTimer.Tick += OnSlideshowTick;
            _slideshowTimer.Start();
        }
        else
        {
            _slideshowTimer?.Stop();
        }
    }

    private void OnSlideshowTick(object? sender, EventArgs e) => Next();

    [RelayCommand] private void ToggleProperties() => ShowProperties = !ShowProperties;

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Current is not { } e) return;
        try { await _dialogs.CopyFileToClipboardAsync(e.Snapshot.Path).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Copy from viewer failed"); }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Current is not { } e) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Snapshots.Dialog.DeleteTitle"],
            message: Localizer.Instance["Snapshots.Dialog.DeleteOneMessage"],
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            TryDeleteFile(e.Snapshot.Path);
            if (!string.IsNullOrEmpty(e.Snapshot.ThumbPath))
                TryDeleteFile(e.Snapshot.ThumbPath!);
            await _repo.RemoveAsync(e.Snapshot.Id, CancellationToken.None).ConfigureAwait(true);
            AnyChanges = true;

            _items.RemoveAt(Index);
            if (_items.Count == 0) { RequestClose(); return; }
            if (Index >= _items.Count) Index = _items.Count - 1;
            OnPropertyChanged(nameof(HasMultiple));
            LoadCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete from viewer failed for {Id}", e.Snapshot.Id);
        }
    }

    // --- editor (14.5) ---

    [ObservableProperty] private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolArrow))]
    [NotifyPropertyChangedFor(nameof(IsToolRectangle))]
    [NotifyPropertyChangedFor(nameof(IsToolText))]
    [NotifyPropertyChangedFor(nameof(IsToolCrop))]
    private EditTool _currentTool = EditTool.Arrow;

    [ObservableProperty] private uint _editColor = Palette[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThin))]
    [NotifyPropertyChangedFor(nameof(IsMedium))]
    [NotifyPropertyChangedFor(nameof(IsThick))]
    private double _editThickness = 0.006;

    public bool IsToolArrow => CurrentTool == EditTool.Arrow;
    public bool IsToolRectangle => CurrentTool == EditTool.Rectangle;
    public bool IsToolText => CurrentTool == EditTool.Text;
    public bool IsToolCrop => CurrentTool == EditTool.Crop;
    public bool IsThin => EditThickness <= 0.004;
    public bool IsMedium => EditThickness > 0.004 && EditThickness < 0.009;
    public bool IsThick => EditThickness >= 0.009;

    [RelayCommand]
    private void EnterEdit()
    {
        if (Current is null) return;
        ResetView();
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SetTool(string tool) => CurrentTool = tool switch
    {
        "rect" => EditTool.Rectangle,
        "text" => EditTool.Text,
        "crop" => EditTool.Crop,
        _ => EditTool.Arrow,
    };

    [RelayCommand]
    private void SetColor(string argbHex)
    {
        if (uint.TryParse(argbHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c))
            EditColor = c;
    }

    [RelayCommand]
    private void SetThickness(string level) => EditThickness = level switch
    {
        "thin" => 0.003,
        "thick" => 0.011,
        _ => 0.006,
    };

    /// <summary>Prompts for the text of a text annotation (returns null if cancelled).</summary>
    public Task<string?> PromptAnnotationTextAsync() =>
        _dialogs.PromptAsync(
            Localizer.Instance["Edit.TextPrompt"],
            string.Empty,
            Localizer.Instance["Edit.AddText"],
            Localizer.Instance["Common.Cancel"]);

    /// <summary>
    /// Renders <paramref name="edit"/> over the current snapshot as a saved copy
    /// (via <see cref="ISnapshotService.SaveEditAsync"/>), inserts it after the
    /// current one and navigates to it. Called by the editor view once the user
    /// commits. The original is never modified.
    /// </summary>
    public async Task ApplyEditAsync(SnapshotEdit edit)
    {
        if (Current is not { } e) return;
        try
        {
            var saved = await _snapshots.SaveEditAsync(e.Snapshot, edit, CancellationToken.None).ConfigureAwait(true);
            AnyChanges = true;
            var entry = new SnapshotViewEntry(saved, e.CameraName);
            _items.Insert(Index + 1, entry);
            Index += 1;
            OnPropertyChanged(nameof(HasMultiple));
            IsEditing = false;
            LoadCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save edited snapshot");
        }
    }

    [RelayCommand]
    private void Close() => RequestClose();

    public void RequestClose() => _closed.TrySetResult(true);

    public void Cleanup()
    {
        _slideshowTimer?.Stop();
        var img = CurrentImage;
        CurrentImage = null;
        img?.Dispose();
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { _logger.LogWarning(ex, "Snapshot file locked: {Path}", path); }
    }
}
