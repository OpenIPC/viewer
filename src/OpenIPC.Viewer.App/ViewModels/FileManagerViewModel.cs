using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;
using CoreRemotePath = OpenIPC.Viewer.Core.Ssh.RemotePath;

namespace OpenIPC.Viewer.App.ViewModels;

/// <summary>
/// Remote (camera) file browser over SCP/SSH (phase-13 §13.4). Cross-platform:
/// the remote panel and operations work on every head; local I/O goes through
/// the OS file pickers (no local FS panel) so there are no desktop-only paths
/// or mobile sandbox issues. Root-level deletes are refused.
/// </summary>
public sealed partial class FileManagerViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Camera _camera;
    private readonly CameraDirectoryService _directory;
    private readonly ISshSessionFactory _sessions;
    private readonly IDialogService _dialogs;
    private readonly ILogger<FileManagerViewModel> _logger;

    private ISshSession? _session;

    public ObservableCollection<RemoteEntryViewModel> Entries { get; } = new();
    public string Title => $"{Localizer.Instance["FileManager.Title"]} — {_camera.Name}";

    [ObservableProperty] private string _remotePath = "/";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private string _statusText = "";

    public FileManagerViewModel(
        Camera camera,
        CameraDirectoryService directory,
        ISshSessionFactory sessions,
        IDialogService dialogs,
        ILogger<FileManagerViewModel> logger)
    {
        _camera = camera;
        _directory = directory;
        _sessions = sessions;
        _dialogs = dialogs;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        StatusText = Localizer.Instance["FileManager.Connecting"];
        try
        {
            var endpoint = await _directory.GetSshEndpointAsync(_camera, CancellationToken.None).ConfigureAwait(true);
            if (endpoint is null)
            {
                StatusText = Localizer.Instance["FileManager.NoCreds"];
                return;
            }

            var session = _sessions.Create();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await session.ConnectAsync(endpoint, cts.Token).ConfigureAwait(true);
            _session = session;
            IsConnected = true;
            StatusText = "";
            await LoadEntriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File manager connect failed for {CameraId}", _camera.Id);
            StatusText = string.Format(Localizer.Instance["FileManager.FailedFormat"], ex.Message);
            await DisposeAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadEntriesAsync();

    [RelayCommand]
    private Task NavigateUpAsync()
    {
        RemotePath = CoreRemotePath.Parent(RemotePath);
        return LoadEntriesAsync();
    }

    [RelayCommand]
    private async Task OpenEntryAsync(RemoteEntryViewModel? entry)
    {
        if (entry is null)
            return;
        if (entry.IsDirectory)
        {
            RemotePath = CoreRemotePath.Combine(RemotePath, entry.Name);
            await LoadEntriesAsync().ConfigureAwait(true);
        }
        else
        {
            await DownloadAsync(entry).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(RemoteEntryViewModel? entry)
    {
        if (_session is null || entry is null || entry.IsDirectory)
            return;

        var target = await _dialogs.PickSaveTargetAsync(entry.Name, Localizer.Instance["FileManager.SaveTitle"]).ConfigureAwait(true);
        if (string.IsNullOrEmpty(target))
            return;

        var remote = CoreRemotePath.Combine(RemotePath, entry.Name);
        await RunTransferAsync(
            p => _session.DownloadAsync(remote, target, p, CancellationToken.None),
            entry.SizeBytes,
            Localizer.Instance["FileManager.Downloading"]).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (_session is null)
            return;

        var local = await _dialogs.PickAnyFileAsync(Localizer.Instance["FileManager.UploadTitle"]).ConfigureAwait(true);
        if (string.IsNullOrEmpty(local))
            return;

        var name = Path.GetFileName(local);
        var remote = CoreRemotePath.Combine(RemotePath, name);
        long? total = null;
        try { total = new FileInfo(local).Length; } catch (IOException) { /* unknown size */ }

        await RunTransferAsync(
            p => _session.UploadAsync(local, remote, p, CancellationToken.None),
            total,
            Localizer.Instance["FileManager.Uploading"]).ConfigureAwait(true);
        await LoadEntriesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (_session is null)
            return;

        var name = await _dialogs.PromptAsync(
            Localizer.Instance["FileManager.NewFolderTitle"], "",
            Localizer.Instance["Common.Save"], Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            await _session.CreateDirectoryAsync(CoreRemotePath.Combine(RemotePath, name), CancellationToken.None).ConfigureAwait(true);
            await LoadEntriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mkdir failed");
            StatusText = string.Format(Localizer.Instance["FileManager.FailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(RemoteEntryViewModel? entry)
    {
        if (_session is null || entry is null)
            return;

        var full = CoreRemotePath.Combine(RemotePath, entry.Name);
        if (RemotePathGuard.IsProtected(full))
        {
            StatusText = Localizer.Instance["FileManager.DeleteProtected"];
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Localizer.Instance["FileManager.DeleteTitle"],
            string.Format(Localizer.Instance["FileManager.DeleteMessage"], entry.Name),
            Localizer.Instance["Common.Delete"],
            Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed)
            return;

        try
        {
            await _session.DeleteAsync(full, CancellationToken.None).ConfigureAwait(true);
            await LoadEntriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote delete failed for {Path}", full);
            StatusText = string.Format(Localizer.Instance["FileManager.FailedFormat"], ex.Message);
        }
    }

    private async Task LoadEntriesAsync()
    {
        if (_session is null)
            return;

        IsBusy = true;
        try
        {
            Entries.Clear();
            await foreach (var entry in _session.ListAsync(RemotePath, CancellationToken.None).ConfigureAwait(true))
                Entries.Add(new RemoteEntryViewModel(entry));
            StatusText = "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List failed for {Path}", RemotePath);
            StatusText = string.Format(Localizer.Instance["FileManager.FailedFormat"], ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Progress<long> captures the UI sync context (this method runs on the UI
    // thread), so the percent updates marshal back automatically.
    private async Task RunTransferAsync(Func<IProgress<long>, Task> op, long? total, string label)
    {
        IsTransferring = true;
        TransferPercent = 0;
        StatusText = label;
        var progress = new Progress<long>(bytes =>
            TransferPercent = total is > 0 ? Math.Clamp((double)bytes / total.Value, 0, 1) : 0);
        try
        {
            await op(progress).ConfigureAwait(true);
            StatusText = Localizer.Instance["FileManager.TransferDone"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer failed");
            StatusText = string.Format(Localizer.Instance["FileManager.FailedFormat"], ex.Message);
        }
        finally
        {
            IsTransferring = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}

public sealed class RemoteEntryViewModel
{
    public RemoteEntryViewModel(RemoteEntry entry)
    {
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        SizeBytes = entry.Size;
        SizeText = entry.IsDirectory ? "" : FormatSize(entry.Size);
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public string Display => IsDirectory ? Name + "/" : Name;

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, unit == 0 ? "{0:0} {1}" : "{0:0.0} {1}", size, units[unit]);
    }
}
