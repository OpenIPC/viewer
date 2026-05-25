using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class SingleCameraPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LiveStreamCoordinator _coordinator;
    private readonly CameraDirectoryService _directory;
    private readonly IFileSystem _fs;
    private readonly ILogger<SingleCameraPageViewModel> _logger;
    private readonly Camera _camera;

    private readonly StreamQuality _quality = StreamQuality.Main;
    private IDisposable? _stateSub;
    private IDisposable? _telemetrySub;

    [ObservableProperty] private IVideoSession? _session;
    [ObservableProperty] private SessionState _state = SessionState.Idle;
    [ObservableProperty] private SessionTelemetry? _telemetry;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _snapshotPath;

    public string CameraName => _camera.Name;
    public string HostLabel => _camera.Host;

    public SingleCameraPageViewModel(
        Camera camera,
        LiveStreamCoordinator coordinator,
        CameraDirectoryService directory,
        IFileSystem fs,
        ILogger<SingleCameraPageViewModel> logger)
    {
        _camera = camera;
        _coordinator = coordinator;
        _directory = directory;
        _fs = fs;
        _logger = logger;
    }

    public async Task ActivateAsync(CancellationToken ct)
    {
        if (Session is not null)
            return;

        var creds = await _directory.GetCredentialsAsync(_camera.Id, ct).ConfigureAwait(true);
        var options = VideoSessionOptions.Default(_camera.RtspMainUri, creds);

        try
        {
            var session = _coordinator.Acquire(_camera.Id, _quality, options);
            _stateSub = session.StateChanged.Subscribe(s =>
            {
                State = s;
                if (s == SessionState.Failed)
                    ErrorMessage = session.LastError;
            });
            _telemetrySub = session.Telemetry.Subscribe(t => Telemetry = t);
            Session = session;

            if (session.State == SessionState.Idle)
                await session.StartAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start session for camera {CameraId}", _camera.Id);
            ErrorMessage = ex.Message;
            State = SessionState.Failed;
        }
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (Session is null)
            return;

        try
        {
            var bytes = await Session.SnapshotAsync(SnapshotFormat.Jpeg, CancellationToken.None).ConfigureAwait(true);
            var dir = Path.Combine(_fs.SnapshotsDir.FullName, SafeFileName(_camera.Name));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd-HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            SnapshotPath = path;
            _logger.LogInformation("Snapshot saved {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot failed");
            ErrorMessage = $"Snapshot failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Back() =>
        WeakReferenceMessenger.Default.Send(new GoBackToLibraryMessage());

    public async ValueTask DisposeAsync()
    {
        _stateSub?.Dispose();
        _telemetrySub?.Dispose();
        if (Session is not null)
        {
            Session = null;
            await _coordinator.ReleaseAsync(_camera.Id, _quality).ConfigureAwait(false);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }
}
