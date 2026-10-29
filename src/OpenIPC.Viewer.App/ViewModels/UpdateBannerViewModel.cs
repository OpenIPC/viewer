using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Updates;

namespace OpenIPC.Viewer.App.ViewModels;

// The "new version" strip above the page content. Download saves the archive into Downloads on
// desktop (with progress, hash-checked) and hands the APK link to the browser on mobile; Later
// hides it for this run; Skip hides this version for good.
public sealed partial class UpdateBannerViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly IDialogService _dialogs;
    private readonly IShareService _share;
    private readonly ILogger<UpdateBannerViewModel> _logger;
    private CancellationTokenSource? _downloadCts;
    // The release DownloadedPath (or the download in flight) belongs to. Feeds reuse file names
    // across releases (openipc-viewer-win-x64.zip), so the name can't tell an old archive apart.
    private AppVersion? _downloadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private bool _dismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDownloaded))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _downloadedPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    public UpdateBannerViewModel(
        UpdateService updates,
        IDialogService dialogs,
        IShareService share,
        ILogger<UpdateBannerViewModel> logger)
    {
        _updates = updates;
        _dialogs = dialogs;
        _share = share;
        _logger = logger;
        _updates.Changed += (_, _) => OnAvailableChanged();
        Localizer.Instance.PropertyChanged += (_, _) => RaiseTexts();
        _updates.StartBackgroundChecks();
    }

    public bool IsVisible => _updates.Available is not null && !Dismissed;
    public bool IsIdle => !IsDownloading && DownloadedPath is null;
    public bool IsDownloaded => DownloadedPath is not null;
    public bool HasReleasePage => _updates.Available?.PageUrl is not null;

    public string Title => _updates.Available is { } r
        ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Available"], r.Version)
        : string.Empty;

    public string StatusText
    {
        get
        {
            if (IsDownloading)
                return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Downloading"],
                    (int)Math.Round(Progress * 100));
            if (DownloadedPath is not null)
                return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Downloaded"], DownloadedPath);
            if (Error is not null)
                return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Failed"], Error);
            return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Update.Current"], UpdateService.VersionText);
        }
    }

    private void OnAvailableChanged()
    {
        // Whatever the trigger — a newer release, or a manual check — the banner is wanted again.
        Dismissed = false;
        if (_downloadVersion is not null && _updates.Available?.Version != _downloadVersion)
        {
            // A different release (or none) is on offer now: the saved archive or the download
            // in flight is for the old one.
            _downloadCts?.Cancel();
            _downloadVersion = null;
            DownloadedPath = null;
        }
        Error = null;
        RaiseTexts();
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HasReleasePage));
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_updates.AvailableAsset is not { } asset || _updates.Available is not { } release) return;

        if (!_updates.CanDownloadInApp)
        {
            // The browser downloads the APK and the system installer takes it from there; an
            // archive without a published hash goes the same way rather than as "verified".
            if (!await _dialogs.OpenUrlAsync(asset.Url.AbsoluteUri).ConfigureAwait(true))
                Error = asset.Url.AbsoluteUri;
            return;
        }

        _downloadCts?.Dispose();
        var cts = _downloadCts = new CancellationTokenSource();
        _downloadVersion = release.Version;
        Error = null;
        Progress = 0;
        IsDownloading = true;
        try
        {
            // Progress<T> captures the UI context it is created on.
            var progress = new Progress<double>(p => Progress = p);
            var path = await _updates.DownloadAsync(progress, cts.Token).ConfigureAwait(true);
            // The offer may have moved on while this was downloading.
            if (_downloadVersion == release.Version) DownloadedPath = path;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // User pressed Cancel, or the offer changed — back to the offer.
            if (_downloadVersion == release.Version) _downloadVersion = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed");
            if (_downloadVersion == release.Version)
            {
                _downloadVersion = null;
                Error = ex.Message;
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private async Task ShowInFolderAsync()
    {
        if (DownloadedPath is { } path)
            await _share.RevealInFolderAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        if (_updates.Available?.PageUrl is { } page)
            await _dialogs.OpenUrlAsync(page.AbsoluteUri).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Later() => Dismissed = true;

    [RelayCommand]
    private async Task SkipAsync()
    {
        _downloadCts?.Cancel();
        await _updates.SkipAvailableAsync().ConfigureAwait(true);
    }
}
