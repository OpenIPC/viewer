using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IRecipient<OpenCameraMessage>, IRecipient<GoBackToLibraryMessage>
{
    private readonly CameraDirectoryService _directory;
    private readonly SingleCameraPageFactory _singleCameraFactory;
    private readonly ILogger<MainWindowViewModel> _logger;

    private SingleCameraPageViewModel? _activeSingleCamera;

    public GridPageViewModel Live { get; }
    public CameraLibraryPageViewModel Library { get; }
    public RecordingsPageViewModel Recordings { get; }
    public SettingsPageViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveSelected))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySelected))]
    [NotifyPropertyChangedFor(nameof(IsRecordingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    private ViewModelBase _currentPage;

    public bool IsLiveSelected => CurrentPage is GridPageViewModel;
    public bool IsLibrarySelected => CurrentPage is CameraLibraryPageViewModel or SingleCameraPageViewModel;
    public bool IsRecordingsSelected => CurrentPage is RecordingsPageViewModel;
    public bool IsSettingsSelected => CurrentPage is SettingsPageViewModel;

    public MainWindowViewModel(
        GridPageViewModel live,
        CameraLibraryPageViewModel library,
        RecordingsPageViewModel recordings,
        SettingsPageViewModel settings,
        CameraDirectoryService directory,
        SingleCameraPageFactory singleCameraFactory,
        ILogger<MainWindowViewModel> logger)
    {
        Live = live;
        Library = library;
        Recordings = recordings;
        Settings = settings;
        _directory = directory;
        _singleCameraFactory = singleCameraFactory;
        _logger = logger;
        _currentPage = library;

        WeakReferenceMessenger.Default.Register<OpenCameraMessage>(this);
        WeakReferenceMessenger.Default.Register<GoBackToLibraryMessage>(this);
    }

    [RelayCommand]
    private void Navigate(string target)
    {
        if (_activeSingleCamera is not null && target != "library")
            _ = DisposeActiveSingleCameraAsync();

        CurrentPage = target switch
        {
            "live" => Live,
            "library" => Library,
            "recordings" => Recordings,
            "settings" => Settings,
            _ => CurrentPage,
        };
    }

    public async void Receive(OpenCameraMessage message)
    {
        try
        {
            var camera = await _directory.GetAsync(message.CameraId, CancellationToken.None).ConfigureAwait(true);
            if (camera is null)
            {
                _logger.LogWarning("Camera {Id} not found on open request", message.CameraId);
                return;
            }

            await DisposeActiveSingleCameraAsync().ConfigureAwait(true);
            _activeSingleCamera = _singleCameraFactory.Create(camera);
            CurrentPage = _activeSingleCamera;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open camera {Id}", message.CameraId);
        }
    }

    public async void Receive(GoBackToLibraryMessage message)
    {
        await DisposeActiveSingleCameraAsync().ConfigureAwait(true);
        CurrentPage = Library;
    }

    private async Task DisposeActiveSingleCameraAsync()
    {
        if (_activeSingleCamera is null)
            return;

        var vm = _activeSingleCamera;
        _activeSingleCamera = null;
        try
        {
            await vm.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing single camera page");
        }
    }
}
