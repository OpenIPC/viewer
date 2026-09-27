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

public sealed partial class MainWindowViewModel : ViewModelBase,
    IRecipient<OpenCameraMessage>,
    IRecipient<GoBackToLibraryMessage>,
    IRecipient<OpenRecordingMessage>,
    IRecipient<ShowDetectionEventsMessage>,
    IRecipient<GoBackToRecordingsMessage>,
    IRecipient<ToggleKioskMessage>,
    IRecipient<SetPlayerFullscreenMessage>
{
    private readonly CameraDirectoryService _directory;
    private readonly SingleCameraPageFactory _singleCameraFactory;
    private readonly RecordingPlayerPageFactory _playerFactory;
    private readonly ILogger<MainWindowViewModel> _logger;

    private SingleCameraPageViewModel? _activeSingleCamera;
    private RecordingPlayerPageViewModel? _activePlayer;

    // The page the single-camera view was opened from (grid vs library), so
    // Back returns there instead of always dropping to the library list.
    private ViewModelBase? _singleCameraOrigin;

    public GridPageViewModel Live { get; }
    public CameraLibraryPageViewModel Library { get; }
    public RecordingsPageViewModel Recordings { get; }
    public EventsPageViewModel Events { get; }
    public AnalyticsPageViewModel Analytics { get; }
    public SettingsPageViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveSelected))]
    [NotifyPropertyChangedFor(nameof(IsLibrarySelected))]
    [NotifyPropertyChangedFor(nameof(IsRecordingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsEventsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnalyticsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    private ViewModelBase _currentPage;

    // Orientation-driven fullscreen (mobile only). MainView reports whether
    // the viewport is in mobile landscape; fullscreen engages only while the
    // single-camera page is open, so list/settings pages keep their chrome.
    private bool _isMobileLandscape;

    // Desktop kiosk (Phase 20): a manually-toggled chrome-free fullscreen grid
    // for an unattended guard station. Independent of the mobile orientation
    // path above; both feed IsFullscreen via UpdateFullscreen.
    [ObservableProperty]
    private bool _kioskMode;

    [ObservableProperty]
    private bool _isFullscreen;

    // Recording player fullscreen, requested by the player's own button / F.
    private bool _playerFullscreen;

    public bool IsLiveSelected => CurrentPage is GridPageViewModel;
    public bool IsLibrarySelected => CurrentPage is CameraLibraryPageViewModel or SingleCameraPageViewModel;
    public bool IsRecordingsSelected => CurrentPage is RecordingsPageViewModel or RecordingPlayerPageViewModel;
    public bool IsEventsSelected => CurrentPage is EventsPageViewModel;
    public bool IsAnalyticsSelected => CurrentPage is AnalyticsPageViewModel;
    public bool IsSettingsSelected => CurrentPage is SettingsPageViewModel;

    public MainWindowViewModel(
        GridPageViewModel live,
        CameraLibraryPageViewModel library,
        RecordingsPageViewModel recordings,
        EventsPageViewModel events,
        AnalyticsPageViewModel analytics,
        SettingsPageViewModel settings,
        CameraDirectoryService directory,
        SingleCameraPageFactory singleCameraFactory,
        RecordingPlayerPageFactory playerFactory,
        UserSettingsService userSettings,
        ILogger<MainWindowViewModel> logger)
    {
        Live = live;
        Library = library;
        Recordings = recordings;
        Events = events;
        Analytics = analytics;
        Settings = settings;
        _directory = directory;
        _singleCameraFactory = singleCameraFactory;
        _playerFactory = playerFactory;
        _logger = logger;
        // Settings → Start page (#70): open straight into the live grid if asked.
        _currentPage = userSettings.Current.StartupPage == "live" ? live : library;

        WeakReferenceMessenger.Default.Register<OpenCameraMessage>(this);
        WeakReferenceMessenger.Default.Register<GoBackToLibraryMessage>(this);
        WeakReferenceMessenger.Default.Register<OpenRecordingMessage>(this);
        WeakReferenceMessenger.Default.Register<ShowDetectionEventsMessage>(this);
        WeakReferenceMessenger.Default.Register<GoBackToRecordingsMessage>(this);
        WeakReferenceMessenger.Default.Register<ToggleKioskMessage>(this);
        WeakReferenceMessenger.Default.Register<SetPlayerFullscreenMessage>(this);

        // While a mobile overlay dialog is open the bottom nav must not switch
        // pages under it — the dim layer doesn't reliably swallow those taps.
        // Re-evaluate CanNavigate whenever an overlay opens/closes; this greys
        // out the nav buttons (Avalonia disables a control whose command can't
        // execute). This VM is an app-lifetime singleton, so the static
        // subscription lives as long as the process — no unsubscribe needed.
        OverlayDialogPresenter.ActiveChanged += () => NavigateCommand.NotifyCanExecuteChanged();
    }

    public void SetViewportOrientation(bool isMobileLandscape)
    {
        if (_isMobileLandscape == isMobileLandscape)
            return;
        _isMobileLandscape = isMobileLandscape;
        UpdateFullscreen();
    }

    partial void OnCurrentPageChanged(ViewModelBase value) => UpdateFullscreen();

    private void UpdateFullscreen()
    {
        var onPlayer = CurrentPage is RecordingPlayerPageViewModel;
        IsFullscreen = KioskMode
            || (_isMobileLandscape && (CurrentPage is SingleCameraPageViewModel || onPlayer))
            || (_playerFullscreen && onPlayer);
        // Camera-to-camera swipe (and player previous/next) replaces the page
        // VM while IsFullscreen stays true, so the flag is pushed to the current
        // page explicitly instead of relying on the property-changed callback.
        if (CurrentPage is SingleCameraPageViewModel camera)
            camera.IsFullscreen = IsFullscreen;
        if (CurrentPage is RecordingPlayerPageViewModel player)
            player.IsFullscreen = IsFullscreen;
    }

    public void Receive(SetPlayerFullscreenMessage message)
    {
        _playerFullscreen = message.On;
        UpdateFullscreen();
    }

    // Toggle the desktop kiosk. Entering tears down any single-camera/player
    // page and drops to the live grid — kiosk is the grid, full-bleed. Exiting
    // restores the chrome; the window state is reverted by MainView.
    public void Receive(ToggleKioskMessage message)
    {
        KioskMode = !KioskMode;
        if (KioskMode)
        {
            if (_activeSingleCamera is not null) _ = DisposeActiveSingleCameraAsync();
            if (_activePlayer is not null) _ = DisposeActivePlayerAsync();
            CurrentPage = Live;
        }
        UpdateFullscreen();
    }

    // F11: on the recording player it toggles the player's own fullscreen
    // (kiosk would close the player and drop to the grid).
    [RelayCommand]
    private void ToggleKiosk()
    {
        if (CurrentPage is RecordingPlayerPageViewModel)
            Receive(new SetPlayerFullscreenMessage(!_playerFullscreen));
        else
            Receive(new ToggleKioskMessage());
    }

    // Esc only exits (never enters) — a no-op elsewhere, so it won't swallow
    // Escape from dialogs that aren't in kiosk.
    [RelayCommand]
    private void ExitKiosk()
    {
        if (_playerFullscreen && CurrentPage is RecordingPlayerPageViewModel)
        {
            Receive(new SetPlayerFullscreenMessage(false));
            return;
        }
        if (!KioskMode) return;
        KioskMode = false;
        UpdateFullscreen();
    }

    private static bool CanNavigate() => !OverlayDialogPresenter.IsAnyOpen;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(string target)
    {
        if (_activeSingleCamera is not null && target != "library")
            _ = DisposeActiveSingleCameraAsync();

        // The recordings player lives under the Recordings tab; any nav (incl.
        // tapping Recordings again) returns to the list and tears it down.
        if (_activePlayer is not null)
            _ = DisposeActivePlayerAsync();

        CurrentPage = target switch
        {
            "live" => Live,
            "library" => Library,
            "recordings" => Recordings,
            "events" => Events,
            "analytics" => Analytics,
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
            // Camera-to-camera swipe re-enters this with a single-camera page
            // already current; keep the original entry page so Back still lands
            // where the user started (the live grid or the library list).
            if (CurrentPage is not SingleCameraPageViewModel)
                _singleCameraOrigin = CurrentPage;
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
        CurrentPage = _singleCameraOrigin ?? Library;
        _singleCameraOrigin = null;
    }

    public void Receive(ShowDetectionEventsMessage message)
    {
        Events.KindFilter = EventKindFilter.Detection;
        Navigate("events");
    }

    public void Receive(OpenRecordingMessage message)
    {
        try
        {
            // Fullscreen carries over previous/next, not a fresh open.
            if (CurrentPage is not RecordingPlayerPageViewModel)
                _playerFullscreen = false;
            _ = DisposeActivePlayerAsync();
            _activePlayer = _playerFactory.Create(message.Recording, message.CameraName);
            _activePlayer.StartAt = message.StartAt;
            _activePlayer.Rate = message.Rate;
            CurrentPage = _activePlayer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open recording {Path}", message.Recording.FilePath);
        }
    }

    public async void Receive(GoBackToRecordingsMessage message)
    {
        await DisposeActivePlayerAsync().ConfigureAwait(true);
        _playerFullscreen = false;
        CurrentPage = Recordings;
        if (message.Reload)
            await Recordings.LoadAsync(CancellationToken.None).ConfigureAwait(true);
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

    private async Task DisposeActivePlayerAsync()
    {
        if (_activePlayer is null)
            return;

        var vm = _activePlayer;
        _activePlayer = null;
        try
        {
            await vm.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing recording player page");
        }
    }
}
