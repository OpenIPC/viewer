using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

public sealed partial class CameraEditorViewModel : ViewModelBase
{
    private readonly IVideoEngine? _engine;
    private readonly CameraDirectoryService? _directory;
    private readonly UserSettingsService? _userSettings;
    private readonly ILogger<CameraEditorViewModel>? _logger;
    private GroupId? _pendingGroupId;

    public CameraId? EditingId { get; }
    public string Title => Localizer.Instance[EditingId is null ? "CameraEditor.Title.Add" : "CameraEditor.Title.Edit"];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _httpPort = 80;
    [ObservableProperty] private string _onvifPortText = "";
    [ObservableProperty] private string _rtspMainText = "";
    [ObservableProperty] private string _rtspSubText = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private CameraGroup? _selectedGroup;

    // Per-camera SD/HD override (Phase 12.2).
    public IReadOnlyList<StreamQualityOption> StreamQualityOptions { get; } = new[]
    {
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.Auto"], StreamQualityOverride.Auto),
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.AlwaysHd"], StreamQualityOverride.AlwaysHd),
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.AlwaysSd"], StreamQualityOverride.AlwaysSd),
    };

    [ObservableProperty] private StreamQualityOption? _selectedStreamQuality;

    // Includes a leading null entry so the user can pick "no group".
    public ObservableCollection<CameraGroup?> AvailableGroups { get; } = new();

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _testStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    private bool _testInProgress;

    public bool CanTestConnection => _engine is not null && !TestInProgress;

    public CameraEditorViewModel() { }

    public CameraEditorViewModel(IVideoEngine engine, CameraDirectoryService directory, UserSettingsService userSettings, ILogger<CameraEditorViewModel> logger)
    {
        _engine = engine;
        _directory = directory;
        _userSettings = userSettings;
        _logger = logger;
        SelectedStreamQuality = StreamQualityOptions[0]; // Auto
    }

    public CameraEditorViewModel(Camera existing, CameraCredentials? credentials, IVideoEngine engine, CameraDirectoryService directory, UserSettingsService userSettings, ILogger<CameraEditorViewModel> logger)
        : this(engine, directory, userSettings, logger)
    {
        EditingId = existing.Id;
        Name = existing.Name;
        Host = existing.Host;
        HttpPort = existing.HttpPort;
        OnvifPortText = existing.OnvifPort?.ToString() ?? "";
        RtspMainText = existing.RtspMainUri.ToString();
        RtspSubText = existing.RtspSubUri?.ToString() ?? "";
        Username = credentials?.Username ?? "";
        Password = credentials?.Password ?? "";
        _pendingGroupId = existing.GroupId;
        SelectedStreamQuality = StreamQualityOptions.FirstOrDefault(o => o.Value == existing.StreamQualityOverride)
            ?? StreamQualityOptions[0];
    }

    public async Task LoadGroupsAsync(CancellationToken ct)
    {
        if (_directory is null) return;
        var groups = await _directory.ListGroupsAsync(ct).ConfigureAwait(true);
        AvailableGroups.Clear();
        AvailableGroups.Add(null); // "(no group)" entry
        foreach (var g in groups) AvailableGroups.Add(g);

        // Restore selection if editing — match by Id since the loaded list
        // is a fresh set of records.
        if (_pendingGroupId is { } id)
        {
            foreach (var g in AvailableGroups)
                if (g is not null && g.Id.Equals(id)) { SelectedGroup = g; break; }
        }
    }

    [RelayCommand]
    private void AutoDeriveRtsp()
    {
        if (!string.IsNullOrWhiteSpace(Host))
            RtspMainText = $"rtsp://{Host.Trim()}/";
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (_engine is null) return;

        if (!TryValidate(out _, out var rtspMain, out _, out _))
        {
            TestStatus = ErrorMessage;
            return;
        }

        TestInProgress = true;
        TestStatus = Localizer.Instance["CameraEditor.Status.Connecting"];
        TestConnectionCommand.NotifyCanExecuteChanged();

        var creds = string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)
            ? null
            : new CameraCredentials(Username, Password);
        // Same transport the live view will use — a UDP-only setup used to pass
        // playback but fail the test (which was hardwired to the default TCP).
        var options = VideoSessionOptions.Default(rtspMain, creds)
            with { Transport = ParseTransport(_userSettings?.Current.RtspTransport) };
        var session = _engine.CreateSession(options);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await session.StartAsync(cts.Token).ConfigureAwait(true);
            var frame = await session.Frames.Take(1).ToTask(cts.Token).ConfigureAwait(true);
            TestStatus = string.Format(Localizer.Instance["CameraEditor.Status.OkFormat"], frame.Width, frame.Height);
        }
        catch (OperationCanceledException)
        {
            TestStatus = Localizer.Instance["CameraEditor.Status.Timeout"];
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Test connection failed");
            TestStatus = string.Format(Localizer.Instance["CameraEditor.Status.FailedFormat"], ex.Message);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(true);
            TestInProgress = false;
            TestConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    public bool TryBuildRequest(out NewCameraRequest? newRequest, out UpdateCameraRequest? updateRequest)
    {
        newRequest = null;
        updateRequest = null;

        if (!TryValidate(out var ok, out var rtspMain, out var rtspSub, out var onvifPort))
            return false;

        var credentials = string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)
            ? null
            : new CameraCredentials(Username, Password);

        var quality = SelectedStreamQuality?.Value ?? StreamQualityOverride.Auto;

        if (EditingId is null)
        {
            newRequest = new NewCameraRequest(
                Name: Name.Trim(),
                Host: Host.Trim(),
                HttpPort: HttpPort,
                OnvifPort: onvifPort,
                RtspMainUri: rtspMain,
                RtspSubUri: rtspSub,
                Credentials: credentials,
                GroupId: SelectedGroup?.Id,
                StreamQualityOverride: quality);
        }
        else
        {
            updateRequest = new UpdateCameraRequest(
                Name: Name.Trim(),
                Host: Host.Trim(),
                HttpPort: HttpPort,
                OnvifPort: onvifPort,
                RtspMainUri: rtspMain,
                RtspSubUri: rtspSub,
                Credentials: credentials,
                GroupId: SelectedGroup?.Id,
                StreamQualityOverride: quality);
        }

        return ok;
    }

    private bool TryValidate(out bool ok, out Uri rtspMain, out Uri? rtspSub, out int? onvifPort)
    {
        ok = false;
        rtspMain = default!;
        rtspSub = null;
        onvifPort = null;
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = Localizer.Instance["CameraEditor.Error.NameRequired"];
            return false;
        }
        if (string.IsNullOrWhiteSpace(Host))
        {
            ErrorMessage = Localizer.Instance["CameraEditor.Error.HostRequired"];
            return false;
        }

        var rtspMainSource = string.IsNullOrWhiteSpace(RtspMainText)
            ? $"rtsp://{Host.Trim()}/"
            : RtspMainText.Trim();

        if (!Uri.TryCreate(rtspMainSource, UriKind.Absolute, out rtspMain!))
        {
            ErrorMessage = Localizer.Instance["CameraEditor.Error.RtspMainInvalid"];
            return false;
        }

        if (!string.IsNullOrWhiteSpace(RtspSubText))
        {
            if (!Uri.TryCreate(RtspSubText.Trim(), UriKind.Absolute, out rtspSub))
            {
                ErrorMessage = Localizer.Instance["CameraEditor.Error.RtspSubInvalid"];
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(OnvifPortText))
        {
            if (!int.TryParse(OnvifPortText.Trim(), out var port) || port < 1 || port > 65535)
            {
                ErrorMessage = Localizer.Instance["CameraEditor.Error.OnvifPortInvalid"];
                return false;
            }
            onvifPort = port;
        }

        if (HttpPort < 1 || HttpPort > 65535)
        {
            ErrorMessage = Localizer.Instance["CameraEditor.Error.HttpPortInvalid"];
            return false;
        }

        ok = true;
        return true;
    }

    private static RtspTransport ParseTransport(string? s) => s?.ToLowerInvariant() switch
    {
        "udp" => RtspTransport.Udp,
        _ => RtspTransport.Tcp,
    };
}

public sealed record CameraEditorResult(NewCameraRequest? NewRequest, UpdateCameraRequest? UpdateRequest);

// Combo item for the per-camera SD/HD override picker (Phase 12.2).
public sealed record StreamQualityOption(string Display, StreamQualityOverride Value);
