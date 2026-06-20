using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public enum MediaTab { Recordings, Snapshots }

public sealed partial class RecordingsPageViewModel : ViewModelBase
{
    private readonly IRecordingRepository _repo;
    private readonly CameraDirectoryService _cameras;
    private readonly IDialogService _dialogs;
    private readonly ILogger<RecordingsPageViewModel> _logger;

    public string Title => Localizer.Instance["Nav.Recordings"];

    // Phase 14: the Recordings page doubles as the captured-media browser. A
    // segmented header flips between the recordings list and the snapshot
    // gallery; the snapshot tab loads lazily on first view.
    public SnapshotBrowserPageViewModel Snapshots { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordings))]
    [NotifyPropertyChangedFor(nameof(ShowSnapshots))]
    [NotifyPropertyChangedFor(nameof(IsRecordingsTabActive))]
    [NotifyPropertyChangedFor(nameof(IsSnapshotsTabActive))]
    private MediaTab _selectedTab = MediaTab.Recordings;

    public bool ShowRecordings => SelectedTab == MediaTab.Recordings;
    public bool ShowSnapshots => SelectedTab == MediaTab.Snapshots;
    public bool IsRecordingsTabActive => SelectedTab == MediaTab.Recordings;
    public bool IsSnapshotsTabActive => SelectedTab == MediaTab.Snapshots;

    public ObservableCollection<RecordingRowViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    // Set when ListAsync throws — the page shows a localized error overlay
    // instead of the empty/loaded states. Cleared on the next successful load.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _loadError;

    public bool IsEmpty => IsLoaded && !IsLoading && LoadError is null && Items.Count == 0;
    public bool HasLoadError => LoadError is not null;

    public RecordingsPageViewModel(
        IRecordingRepository repo,
        CameraDirectoryService cameras,
        IDialogService dialogs,
        SnapshotBrowserPageViewModel snapshots,
        ILogger<RecordingsPageViewModel> logger)
    {
        _repo = repo;
        _cameras = cameras;
        _dialogs = dialogs;
        Snapshots = snapshots;
        _logger = logger;
    }

    [RelayCommand]
    private void SelectRecordings() => SelectedTab = MediaTab.Recordings;

    [RelayCommand]
    private async Task SelectSnapshotsAsync()
    {
        SelectedTab = MediaTab.Snapshots;
        if (!Snapshots.IsLoaded && !Snapshots.IsLoading)
            await Snapshots.LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var recordings = await _repo.ListAsync(cameraId: null, ct).ConfigureAwait(true);
            var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
            var nameById = new Dictionary<CameraId, string>();
            foreach (var c in cams) nameById[c.Id] = c.Name;

            Items.Clear();
            foreach (var r in recordings)
            {
                var name = nameById.TryGetValue(r.CameraId, out var n) ? n : Localizer.Instance["Common.Unknown"];
                Items.Add(new RecordingRowViewModel(r, name));
            }
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recordings");
            LoadError = Localizer.Instance["Recordings.LoadError"];
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void Play(RecordingRowViewModel? row)
    {
        if (row is null) return;
        WeakReferenceMessenger.Default.Send(new OpenRecordingMessage(row.Recording, row.CameraName));
    }

    [RelayCommand]
    private async Task DeleteAsync(RecordingRowViewModel? row)
    {
        if (row is null) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: Localizer.Instance["Recordings.Dialog.DeleteTitle"],
            message: string.Format(Localizer.Instance["Recordings.Dialog.DeleteMessageFormat"], Path.GetFileName(row.FilePath)),
            confirmLabel: Localizer.Instance["Common.Delete"],
            cancelLabel: Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            try { if (File.Exists(row.FilePath)) File.Delete(row.FilePath); }
            catch (IOException ex) { _logger.LogWarning(ex, "File still locked (recording in progress?)"); }

            await _repo.RemoveAsync(row.Recording.Id, CancellationToken.None).ConfigureAwait(true);
            Items.Remove(row);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete recording {Id}", row.Recording.Id);
        }
    }
}

public sealed class RecordingRowViewModel
{
    public Recording Recording { get; }
    public string CameraName { get; }

    public string FilePath => Recording.FilePath;
    public string FileName => Path.GetFileName(Recording.FilePath);
    public DateTime StartedAtLocal => Recording.StartedAt.ToLocalTime();
    public string Duration => Recording.EndedAt is { } end
        ? FormatDuration(end - Recording.StartedAt)
        : Localizer.Instance["Recordings.Live"];
    public string SizeLabel => Recording.SizeBytes <= 0
        ? "—"
        : Recording.SizeBytes > 1024 * 1024
            ? $"{Recording.SizeBytes / (1024.0 * 1024):F1} MB"
            : $"{Recording.SizeBytes / 1024.0:F0} KB";

    public RecordingRowViewModel(Recording recording, string cameraName)
    {
        Recording = recording;
        CameraName = cameraName;
    }

    private static string FormatDuration(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
}
