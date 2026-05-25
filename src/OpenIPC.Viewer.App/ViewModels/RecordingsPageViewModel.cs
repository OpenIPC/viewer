using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class RecordingsPageViewModel : ViewModelBase
{
    private readonly IRecordingRepository _repo;
    private readonly CameraDirectoryService _cameras;
    private readonly IDialogService _dialogs;
    private readonly ILogger<RecordingsPageViewModel> _logger;

    public string Title => "Recordings";

    public ObservableCollection<RecordingRowViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    public bool IsEmpty => IsLoaded && Items.Count == 0;

    public RecordingsPageViewModel(
        IRecordingRepository repo,
        CameraDirectoryService cameras,
        IDialogService dialogs,
        ILogger<RecordingsPageViewModel> logger)
    {
        _repo = repo;
        _cameras = cameras;
        _dialogs = dialogs;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var recordings = await _repo.ListAsync(cameraId: null, ct).ConfigureAwait(true);
        var cams = await _cameras.ListAsync(ct).ConfigureAwait(true);
        var nameById = new Dictionary<CameraId, string>();
        foreach (var c in cams) nameById[c.Id] = c.Name;

        Items.Clear();
        foreach (var r in recordings)
        {
            var name = nameById.TryGetValue(r.CameraId, out var n) ? n : "(unknown)";
            Items.Add(new RecordingRowViewModel(r, name));
        }
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task DeleteAsync(RecordingRowViewModel? row)
    {
        if (row is null) return;
        var confirmed = await _dialogs.ConfirmAsync(
            title: "Delete recording",
            message: $"Delete {Path.GetFileName(row.FilePath)}? The MP4 file will be removed.").ConfigureAwait(true);
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
        : "live";
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
