using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Ssh;
using OpenIPC.Viewer.Core.Ssh.Terminal;

namespace OpenIPC.Viewer.App.ViewModels;

/// <summary>
/// Drives an interactive SSH shell over a camera (phase-13 §13.3). Owns the
/// session/shell lifecycle and pumps received bytes into a
/// <see cref="TerminalEmulator"/> on the UI thread; the view renders the grid
/// and forwards keystrokes back via <see cref="SendAsync"/>.
/// </summary>
public sealed partial class SshTerminalViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Camera _camera;
    private readonly CameraDirectoryService _directory;
    private readonly ISshSessionFactory _sessions;
    private readonly ILogger<SshTerminalViewModel> _logger;

    private ISshSession? _session;
    private ISshShell? _shell;
    private int _columns = 80;
    private int _rows = 24;

    public TerminalEmulator Emulator { get; } = new(80, 24);
    public string Title => $"SSH — {_camera.Name}";

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isConnected;

    public SshTerminalViewModel(
        Camera camera,
        CameraDirectoryService directory,
        ISshSessionFactory sessions,
        ILogger<SshTerminalViewModel> logger)
    {
        _camera = camera;
        _directory = directory;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        StatusText = Localizer.Instance["Terminal.Connecting"];
        try
        {
            var endpoint = await _directory.GetSshEndpointAsync(_camera, CancellationToken.None).ConfigureAwait(true);
            if (endpoint is null)
            {
                StatusText = Localizer.Instance["Terminal.NoCreds"];
                return;
            }

            var session = _sessions.Create();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await session.ConnectAsync(endpoint, cts.Token).ConfigureAwait(true);
            var shell = await session.OpenShellAsync((uint)_columns, (uint)_rows, cts.Token).ConfigureAwait(true);

            _session = session;
            _shell = shell;
            shell.DataReceived += OnShellData;

            IsConnected = true;
            StatusText = "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH terminal connect failed for {CameraId}", _camera.Id);
            StatusText = string.Format(Localizer.Instance["Terminal.FailedFormat"], ex.Message);
            await DisposeAsync().ConfigureAwait(true);
        }
    }

    // Fires on the SSH receive thread — marshal onto the UI thread before
    // touching the emulator grid the renderer reads.
    private void OnShellData(object? sender, byte[] data) =>
        Dispatcher.UIThread.Post(() => Emulator.Feed(data));

    public async Task SendAsync(string text)
    {
        if (_shell is null)
            return;
        try
        {
            await _shell.SendAsync(text, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH terminal send failed");
        }
    }

    public Task ResizeAsync(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        Emulator.Resize(columns, rows);
        _shell?.Resize((uint)columns, (uint)rows);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        if (_shell is not null)
        {
            _shell.DataReceived -= OnShellData;
            await _shell.DisposeAsync().ConfigureAwait(false);
            _shell = null;
        }
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
