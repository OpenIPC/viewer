using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class CameraLibraryPageViewModel : ViewModelBase
{
    private readonly CameraDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly CameraEditorFactory _editorFactory;
    private readonly DiscoveryDialogFactory _discoveryFactory;
    private readonly ILogger<CameraLibraryPageViewModel> _logger;

    public string Title => "Cameras";
    public ObservableCollection<CameraRowViewModel> Cameras { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCameras))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoaded;

    public bool HasCameras => IsLoaded && Cameras.Count > 0;
    public bool IsEmpty => IsLoaded && Cameras.Count == 0;

    public CameraLibraryPageViewModel(
        CameraDirectoryService directory,
        IDialogService dialogs,
        CameraEditorFactory editorFactory,
        DiscoveryDialogFactory discoveryFactory,
        ILogger<CameraLibraryPageViewModel> logger)
    {
        _directory = directory;
        _dialogs = dialogs;
        _editorFactory = editorFactory;
        _discoveryFactory = discoveryFactory;
        _logger = logger;
        Cameras.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCameras));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var cameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        Cameras.Clear();
        foreach (var camera in cameras)
            Cameras.Add(new CameraRowViewModel(camera, _directory, _logger));
        IsLoaded = true;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void OpenCamera(CameraRowViewModel? row)
    {
        if (row is null)
            return;
        WeakReferenceMessenger.Default.Send(new OpenCameraMessage(row.Camera.Id));
    }

    [RelayCommand]
    private async Task AddCameraAsync()
    {
        var editor = _editorFactory.CreateForNew();
        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.NewRequest is not { } req)
            return;

        try
        {
            await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add camera {Name}", req.Name);
        }
    }

    [RelayCommand]
    private async Task DiscoverCameraAsync()
    {
        var discoveryVm = _discoveryFactory.Create();
        var found = await _dialogs.ShowDiscoveryDialogAsync(discoveryVm).ConfigureAwait(true);
        if (found is null)
            return;

        // Pre-fill the editor from the probe result so the user sees / can tweak
        // everything before saving (RTSP URI especially — phase-04 risks §"ONVIF
        // returns wrong RTSP URI behind NAT" applies).
        var editor = _editorFactory.CreateForNew();
        editor.Name = found.Discovered.Model ?? found.Discovered.Name ?? found.Discovered.Host;
        editor.Host = found.Discovered.Host;
        editor.OnvifPortText = found.Discovered.OnvifPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        editor.RtspMainText = found.Probe.RtspMainUri.ToString();
        editor.Username = found.Credentials?.Username ?? "";
        editor.Password = found.Credentials?.Password ?? "";

        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.NewRequest is not { } req)
            return;

        try
        {
            var id = await _directory.AddAsync(req, CancellationToken.None).ConfigureAwait(true);
            // Persist HasPtz / ProfileToken / manufacturer info from the probe so
            // SingleCameraPage knows whether to show the PTZ joystick (Phase 4c).
            await _directory.SaveOnvifMetadataAsync(id, found.Probe, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add discovered camera {Host}", req.Host);
        }
    }

    [RelayCommand]
    private async Task EditCameraAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var creds = await _directory.GetCredentialsAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
        var editor = _editorFactory.CreateForEdit(row.Camera, creds);
        var result = await _dialogs.ShowCameraEditorAsync(editor).ConfigureAwait(true);
        if (result?.UpdateRequest is not { } req)
            return;

        try
        {
            await _directory.UpdateAsync(row.Camera.Id, req, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update camera {Id}", row.Camera.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteCameraAsync(CameraRowViewModel? row)
    {
        if (row is null)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            title: "Delete camera",
            message: $"Delete '{row.Camera.Name}'? This cannot be undone.").ConfigureAwait(true);

        if (!confirmed)
            return;

        try
        {
            await _directory.RemoveAsync(row.Camera.Id, CancellationToken.None).ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete camera {Id}", row.Camera.Id);
        }
    }
}

public sealed partial class CameraRowViewModel : ViewModelBase
{
    private readonly CameraDirectoryService? _directory;
    private readonly ILogger? _logger;

    public Camera Camera { get; }
    public string Name => Camera.Name;
    public string HostAndPort => Camera.HttpPort == 80
        ? Camera.Host
        : $"{Camera.Host}:{Camera.HttpPort}";

    [ObservableProperty] private bool _isIncludedInGrid;

    public CameraRowViewModel(Camera camera) : this(camera, null, null) { }

    public CameraRowViewModel(Camera camera, CameraDirectoryService? directory, ILogger? logger)
    {
        Camera = camera;
        _directory = directory;
        _logger = logger;
        _isIncludedInGrid = camera.IncludedInGrid;
    }

    partial void OnIsIncludedInGridChanged(bool value)
    {
        if (_directory is null) return;
        _ = PersistGridFlagAsync(value);
    }

    private async Task PersistGridFlagAsync(bool value)
    {
        try
        {
            await _directory!.SetIncludedInGridAsync(Camera.Id, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist IncludedInGrid for {CameraId}", Camera.Id);
        }
    }
}
