using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.App.ViewModels;

// Drives the splash window shown while the app initializes (DB migrations +
// event ingestion). This work used to run synchronously in Program.Main before
// Avalonia even started, so a slow first-run migration looked like a frozen,
// window-less process. Now it runs off the UI thread with visible progress and
// a retry path on failure.
public sealed partial class StartupViewModel : ViewModelBase
{
    private readonly IMigrationRunner _migrations;
    private readonly EventIngestionService _events;
    private readonly Services.ConfigSyncService _configSync;
    private readonly ILogger<StartupViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _hasError;

    [ObservableProperty] private string _statusText = Localizer.Instance["Startup.Preparing"];
    [ObservableProperty] private double _progress;            // 0..1
    [ObservableProperty] private string? _errorText;

    public bool IsBusy => !HasError;

    // Raised on the UI thread once initialization succeeds; the host swaps the
    // splash for the main window.
    public event Action? Completed;

    public StartupViewModel(
        IMigrationRunner migrations,
        EventIngestionService events,
        Services.ConfigSyncService configSync,
        ILogger<StartupViewModel> logger)
    {
        _migrations = migrations;
        _events = events;
        _configSync = configSync;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        HasError = false;
        ErrorText = null;
        try
        {
            StatusText = Localizer.Instance["Startup.Migrating"];
            Progress = 0.15;
            // Task.Run keeps the synchronous bits (SqliteConnection.Open) off the
            // UI thread so the splash animates; ConfigureAwait(true) resumes on
            // the UI thread to update the bound properties.
            await Task.Run(() => _migrations.MigrateAsync(CancellationToken.None)).ConfigureAwait(true);

            // Network config auto-sync (Phase 20): mirror cameras+layouts from a
            // shared file before the main window opens. Best-effort — it never
            // throws (unreachable path falls back to the local config), so a
            // down share doesn't stall startup.
            if (_configSync.IsConfigured)
            {
                StatusText = Localizer.Instance["Startup.SyncingConfig"];
                Progress = 0.45;
                await Task.Run(() => _configSync.RunStartupSyncAsync(CancellationToken.None)).ConfigureAwait(true);
            }

            StatusText = Localizer.Instance["Startup.StartingServices"];
            Progress = 0.7;
            await Task.Run(() => _events.StartAsync(CancellationToken.None)).ConfigureAwait(true);

            StatusText = Localizer.Instance["Startup.Ready"];
            Progress = 1.0;
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "App initialization failed");
            ErrorText = ex.Message;
            HasError = true;
        }
    }

    [RelayCommand]
    private async Task RetryAsync() => await RunAsync().ConfigureAwait(true);

    [RelayCommand]
    private void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(1);
        else
            Environment.Exit(1);
    }
}
