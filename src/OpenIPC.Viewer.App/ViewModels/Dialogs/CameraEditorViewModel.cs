using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

public enum ConnectState { Idle, Connecting, Connected, Failed }

// Add / edit camera. The form leads with what's needed to reach the camera
// (address + login); "Connect" works out the rest (device type, RTSP URIs,
// ports, a name) and proves it with a live frame. Everything else sits in
// collapsed "Advanced" sections with a one-line summary each.
public sealed partial class CameraEditorViewModel : ViewModelBase
{
    private const int DefaultHttpPort = 80;

    private readonly CameraConnectService? _connect;
    private readonly CameraDirectoryService? _directory;
    private readonly IDialogService? _dialogs;
    private readonly ILogger<CameraEditorViewModel>? _logger;
    private GroupId? _pendingGroupId;
    private CancellationTokenSource? _connectCts;
    private string? _autoName;
    private bool _saveAttempted;

    public CameraId? EditingId { get; }
    public bool IsNew => EditingId is null;

    // Library "Add camera" sets this so the header offers the other ways in.
    [ObservableProperty] private bool _showAlternatives;

    // Opened from discovery: run Connect as soon as the dialog shows.
    public bool AutoConnect { get; set; }

    // A name to start with that Connect may still replace with a better one.
    public void SuggestName(string name)
    {
        Name = name;
        _autoName = name;
    }

    public string Title => IsNew
        ? Localizer.Instance["CameraEditor.Title.Add"]
        : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["CameraEditor.Title.EditFormat"],
            string.IsNullOrWhiteSpace(Name) ? Host : Name);

    public string SaveLabel => Localizer.Instance[IsNew ? "CameraEditor.Button.Add" : "Common.Save"];
    public string FooterHint => CanSave
        ? Localizer.Instance[IsNew ? "CameraEditor.Hint.EnterAdd" : "CameraEditor.Hint.EnterSave"]
        : Localizer.Instance["CameraEditor.Hint.NeedAddress"];

    // ── Connection ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNameSection), nameof(Title), nameof(CanSave), nameof(FooterHint))]
    private string _host = "";

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = "";

    [ObservableProperty] private CameraGroup? _selectedGroup;

    public bool ShowNameSection => !IsNew || !string.IsNullOrWhiteSpace(Host);

    // ── Advanced: streams / ports / SSH / quality / AI ───────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamsSummary))]
    private string _rtspMainText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamsSummary))]
    private string _rtspSubText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsSummary))]
    private string _httpPortText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsSummary))]
    private string _onvifPortText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsSummary))]
    private string _sshPortText = "";

    // SSH device suite (Phase 13). Blank = reuse the main login.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshSummary))]
    private string _sshUsername = "";

    [ObservableProperty] private string _sshPassword = "";

    // Kept for callers that pre-fill a port (QR payloads).
    public int HttpPort
    {
        get => int.TryParse(HttpPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : DefaultHttpPort;
        set => HttpPortText = value == DefaultHttpPort ? "" : value.ToString(CultureInfo.InvariantCulture);
    }

    // Per-camera SD/HD override (Phase 12.2).
    public IReadOnlyList<StreamQualityOption> StreamQualityOptions { get; } = new[]
    {
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.Auto"], StreamQualityOverride.Auto),
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.AlwaysHd"], StreamQualityOverride.AlwaysHd),
        new StreamQualityOption(Localizer.Instance["CameraEditor.Quality.AlwaysSd"], StreamQualityOverride.AlwaysSd),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualitySummary))]
    private StreamQualityOption? _selectedStreamQuality;

    // AI analytics (Phase 15.4). Threshold 0..1; fps clamped 1..10 by the engine.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiSummary))]
    private bool _aiEnabled;

    [ObservableProperty] private double _aiThreshold = 0.5;
    [ObservableProperty] private int _aiFps = 3;
    [ObservableProperty] private bool _aiAutoRecord;
    [ObservableProperty] private int _aiPostEventSeconds = 15;

    // Curated, surveillance-relevant subset of COCO classes the user can toggle.
    public ObservableCollection<DetectionClassOption> AnalyticsClasses { get; } = new();

    // Includes a leading null entry so the user can pick "no group".
    public ObservableCollection<CameraGroup?> AvailableGroups { get; } = new();

    [ObservableProperty] private bool _isStreamsOpen;
    [ObservableProperty] private bool _isPortsOpen;
    [ObservableProperty] private bool _isSshOpen;
    [ObservableProperty] private bool _isQualityOpen;
    [ObservableProperty] private bool _isAiOpen;

    public string StreamsSummary =>
        string.IsNullOrWhiteSpace(RtspMainText)
            ? Localizer.Instance["CameraEditor.Summary.Auto"]
            : string.IsNullOrWhiteSpace(RtspSubText)
                ? Localizer.Instance["CameraEditor.Summary.MainOnly"]
                : Localizer.Instance["CameraEditor.Summary.MainSub"];

    public string PortsSummary =>
        $"HTTP {PortOr(HttpPortText, "80")} · ONVIF {PortOr(OnvifPortText, "—")} · SSH {PortOr(SshPortText, "22")}";

    public string SshSummary => string.IsNullOrWhiteSpace(SshUsername)
        ? Localizer.Instance["CameraEditor.Summary.SameLogin"]
        : SshUsername.Trim();

    public string QualitySummary => SelectedStreamQuality?.Display ?? "";

    public string AiSummary
    {
        get
        {
            if (!AiEnabled) return Localizer.Instance["CameraEditor.Summary.Off"];
            var names = AnalyticsClasses.Where(c => c.IsSelected).Select(c => c.DisplayName).ToList();
            return names.Count == 0 ? Localizer.Instance["CameraEditor.Summary.On"] : string.Join(", ", names);
        }
    }

    private static string PortOr(string text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    // ── Validation (per field, live once there's something to judge) ─────

    [ObservableProperty] private string? _hostError;
    [ObservableProperty] private string? _rtspMainError;
    [ObservableProperty] private string? _rtspSubError;
    [ObservableProperty] private string? _portsError;
    [ObservableProperty] private string? _sshError;

    // Save/storage failure that isn't about a single field.
    [ObservableProperty] private string? _errorMessage;

    public bool CanSave => !string.IsNullOrWhiteSpace(Host);

    // ── Connect result ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsConnected), nameof(IsConnectFailed), nameof(IsConnectIdle))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private ConnectState _connectState = ConnectState.Idle;

    public bool IsConnecting => ConnectState == ConnectState.Connecting;
    public bool IsConnected => ConnectState == ConnectState.Connected;
    public bool IsConnectFailed => ConnectState == ConnectState.Failed;
    public bool IsConnectIdle => ConnectState == ConnectState.Idle;

    [ObservableProperty] private string? _connectStatus;
    [ObservableProperty] private string? _resultTitle;
    [ObservableProperty] private string? _resultDetails;
    [ObservableProperty] private Bitmap? _resultThumbnail;
    [ObservableProperty] private string? _failTitle;
    [ObservableProperty] private string? _failHint;
    public ObservableCollection<string> ResultPills { get; } = new();

    // What "Connect" learned about the device, handed back with the result so
    // the caller can store PTZ/ONVIF/Majestic metadata.
    public OnvifProbeResult? DetectedOnvif { get; private set; }
    public bool? DetectedMajestic { get; private set; }

    public CameraEditorViewModel() { }

    public CameraEditorViewModel(CameraConnectService connect, CameraDirectoryService directory, IDialogService dialogs, ILogger<CameraEditorViewModel> logger)
    {
        _connect = connect;
        _directory = directory;
        _dialogs = dialogs;
        _logger = logger;
        SelectedStreamQuality = StreamQualityOptions[0]; // Auto
        PopulateAnalyticsClasses();
    }

    public CameraEditorViewModel(Camera existing, CameraCredentials? credentials, CameraCredentials? sshCredentials,
        CameraConnectService connect, CameraDirectoryService directory, IDialogService dialogs, ILogger<CameraEditorViewModel> logger)
        : this(connect, directory, dialogs, logger)
    {
        EditingId = existing.Id;
        Name = existing.Name;
        Host = existing.Host;
        HttpPort = existing.HttpPort;
        OnvifPortText = existing.OnvifPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        RtspMainText = existing.RtspMainUri.ToString();
        RtspSubText = existing.RtspSubUri?.ToString() ?? "";
        Username = credentials?.Username ?? "";
        Password = credentials?.Password ?? "";
        SshUsername = sshCredentials?.Username ?? "";
        SshPassword = sshCredentials?.Password ?? "";
        SshPortText = existing.SshPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        _pendingGroupId = existing.GroupId;
        SelectedStreamQuality = StreamQualityOptions.FirstOrDefault(o => o.Value == existing.StreamQualityOverride)
            ?? StreamQualityOptions[0];

        var ai = existing.AnalyticsOrDefault;
        AiEnabled = ai.Enabled;
        AiThreshold = ai.ConfidenceThreshold;
        AiFps = ai.AnalyticsFps;
        AiAutoRecord = ai.AutoRecord;
        AiPostEventSeconds = ai.PostEventSeconds;
        if (ai.ClassIds is { Count: > 0 } ids)
        {
            var set = new HashSet<int>(ids);
            foreach (var opt in AnalyticsClasses)
                opt.IsSelected = set.Contains(opt.ClassId);
        }
    }

    // Surveillance-relevant COCO classes. person is pre-selected so a fresh
    // enable does something sensible; the user can broaden/narrow from here.
    private static readonly (int Id, bool Default)[] CuratedClasses =
    {
        (0, true), (1, false), (2, false), (3, false), (5, false), (7, false), (15, false), (16, false),
    };

    private void PopulateAnalyticsClasses()
    {
        if (AnalyticsClasses.Count > 0) return;
        foreach (var (id, def) in CuratedClasses)
        {
            var opt = new DetectionClassOption(id, CocoClasses.Names[id]) { IsSelected = def };
            opt.PropertyChanged += (_, _) => OnPropertyChanged(nameof(AiSummary));
            AnalyticsClasses.Add(opt);
        }
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
    private async Task NewGroupAsync()
    {
        if (_dialogs is null || _directory is null) return;
        var name = await _dialogs.PromptAsync(
            Localizer.Instance["CameraEditor.NewGroup.Title"], "",
            Localizer.Instance["Common.Create"], Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var id = await _directory.AddGroupAsync(name.Trim(), CancellationToken.None).ConfigureAwait(true);
            _pendingGroupId = id;
            await LoadGroupsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Creating group {Name} failed", name);
        }
    }

    // ── Address parsing ──────────────────────────────────────────────────

    // Accepts a bare host, host:port, or a whole URL (rtsp://user:pass@host/path,
    // http://host:8080) and spreads it over the fields. Called when the address
    // box loses focus, on paste, and before Connect/Save — never per keystroke,
    // so the text doesn't rearrange itself under the caret.
    public void NormalizeAddress()
    {
        var raw = Host.Trim();
        if (raw.Length == 0) { if (Host.Length > 0) Host = ""; return; }

        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1) Password = Uri.UnescapeDataString(parts[1]);
            }
            if (uri.Scheme is "rtsp" or "rtsps")
            {
                // Credentials ride the RTSP handshake, not the URI.
                var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
                RtspMainText = clean.ToString();
            }
            else if (uri.Scheme is "http" or "https" && !uri.IsDefaultPort)
            {
                HttpPort = uri.Port;
            }
            Host = uri.Host;
            return;
        }

        // host:port (not IPv6). RTSP ports mean the stream, anything else HTTP.
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && raw.IndexOf(':') == colon
            && int.TryParse(raw[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and < 65536)
        {
            var host = raw[..colon];
            if (port is 554 or 8554)
                RtspMainText = $"rtsp://{host}:{port}/";
            else
                HttpPort = port;
            Host = host;
            return;
        }

        if (raw != Host) Host = raw;
    }

    // ── Connect ──────────────────────────────────────────────────────────

    private bool CanConnect() => _connect is not null && ConnectState != ConnectState.Connecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (_connect is null) return;
        NormalizeAddress();
        if (string.IsNullOrWhiteSpace(Host))
        {
            HostError = Localizer.Instance["CameraEditor.Error.HostRequired"];
            return;
        }
        if (!ValidateFields()) return;

        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();
        ConnectState = ConnectState.Connecting;
        ConnectStatus = Localizer.Instance["CameraEditor.Status.Connecting"];

        var host = Host.Trim();
        var mainText = RtspMainText.Trim();
        // A bare rtsp://host/ is our own guess, not a choice — let Connect improve it.
        var userMain = mainText.Length == 0 || IsTrivialRtsp(mainText, host) ? null : new Uri(mainText);
        var userSub = string.IsNullOrWhiteSpace(RtspSubText) ? null : new Uri(RtspSubText.Trim());
        var request = new CameraConnectRequest(host, HttpPort, ParsePort(OnvifPortText), userMain, userSub, Credentials());

        CameraConnectResult result;
        try
        {
            result = await _connect.ConnectAsync(request, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Connect failed for {Host}", host);
            ShowFailure(CameraConnectFailure.NoVideo, false);
            return;
        }
        if (cts.IsCancellationRequested) return;
        Apply(result, host);
    }

    private void Apply(CameraConnectResult r, string host)
    {
        DetectedOnvif = r.Onvif;
        DetectedMajestic = r.IsMajestic;
        if (r.OnvifPort is { } op && string.IsNullOrWhiteSpace(OnvifPortText))
            OnvifPortText = op.ToString(CultureInfo.InvariantCulture);

        if (!r.Ok)
        {
            // Keep what was learned about the streams even when they didn't open.
            if (r.RtspMain is { } m && (string.IsNullOrWhiteSpace(RtspMainText) || IsTrivialRtsp(RtspMainText, host)))
                RtspMainText = m.ToString();
            ShowFailure(r.Failure, r.IsMajestic);
            return;
        }

        RtspMainText = r.RtspMain!.ToString();
        if (r.RtspSub is { } sub) RtspSubText = sub.ToString();

        var (title, details, suggested) = Describe(r, host);
        if (string.IsNullOrWhiteSpace(Name) || Name == _autoName)
        {
            Name = suggested;
            _autoName = suggested;
        }

        ResultTitle = title;
        ResultDetails = details;
        ResultPills.Clear();
        ResultPills.Add(r.MainCodec is null ? $"{r.MainWidth}×{r.MainHeight}" : $"{r.MainWidth}×{r.MainHeight} {PrettyCodec(r.MainCodec)}");
        if (r.SubWidth > 0) ResultPills.Add(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["CameraEditor.Pill.SubFormat"], r.SubWidth, r.SubHeight));
        if (r.HasPtz) ResultPills.Add("PTZ");
        if (r.HasAudio) ResultPills.Add(Localizer.Instance["CameraEditor.Pill.Audio"]);
        ResultThumbnail = Decode(r.SnapshotJpeg);
        ConnectStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["CameraEditor.Status.ConnectedFormat"],
            r.Elapsed.TotalSeconds);
        ConnectState = ConnectState.Connected;
    }

    private void ShowFailure(CameraConnectFailure failure, bool isMajestic)
    {
        (FailTitle, FailHint) = failure switch
        {
            CameraConnectFailure.Unreachable => (Localizer.Instance["CameraEditor.Fail.Unreachable"], Localizer.Instance["CameraEditor.Fail.UnreachableHint"]),
            CameraConnectFailure.Unauthorized when Credentials() is null => (Localizer.Instance["CameraEditor.Fail.AuthNeeded"],
                Localizer.Instance[isMajestic ? "CameraEditor.Fail.AuthNeededHintOpenIpc" : "CameraEditor.Fail.AuthNeededHint"]),
            CameraConnectFailure.Unauthorized => (Localizer.Instance["CameraEditor.Fail.Auth"],
                Localizer.Instance[isMajestic ? "CameraEditor.Fail.AuthHintOpenIpc" : "CameraEditor.Fail.AuthHint"]),
            CameraConnectFailure.Timeout => (Localizer.Instance["CameraEditor.Fail.Timeout"], Localizer.Instance["CameraEditor.Fail.NoVideoHint"]),
            _ => (Localizer.Instance["CameraEditor.Fail.NoVideo"], Localizer.Instance["CameraEditor.Fail.NoVideoHint"]),
        };
        ConnectStatus = Localizer.Instance["CameraEditor.Status.Failed"];
        ConnectState = ConnectState.Failed;
        if (failure is CameraConnectFailure.NoVideo or CameraConnectFailure.Timeout) IsStreamsOpen = true;
    }

    private (string Title, string Details, string SuggestedName) Describe(CameraConnectResult r, string host)
    {
        if (r.IsMajestic)
        {
            var chip = r.Majestic?.ChipModel?.ToUpperInvariant();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(chip)) parts.Add(chip!);
            if (!string.IsNullOrWhiteSpace(r.Majestic?.FirmwareVersion))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["CameraEditor.Result.FirmwareFormat"], r.Majestic!.FirmwareVersion));
            return ("OpenIPC · Majestic", string.Join(" · ", parts), $"{chip ?? "OpenIPC"} — {host}");
        }
        if (r.Onvif is { } o)
        {
            var model = string.Join(" ", new[] { o.Manufacturer, o.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var details = string.IsNullOrWhiteSpace(o.FirmwareVersion)
                ? "ONVIF"
                : "ONVIF · " + string.Format(CultureInfo.CurrentCulture, Localizer.Instance["CameraEditor.Result.FirmwareFormat"], o.FirmwareVersion);
            return (model.Length > 0 ? model : "ONVIF", details, $"{(string.IsNullOrWhiteSpace(o.Model) ? model : o.Model)} — {host}".TrimStart(' ', '—'));
        }
        return (Localizer.Instance["CameraEditor.Result.Generic"], "RTSP", host);
    }

    private static string PrettyCodec(string codec) => codec.ToLowerInvariant() switch
    {
        var c when c.StartsWith("hevc", StringComparison.Ordinal) || c.StartsWith("h265", StringComparison.Ordinal) => "H.265",
        var c when c.StartsWith("h264", StringComparison.Ordinal) => "H.264",
        var c => c.Split(' ')[0].ToUpperInvariant(),
    };

    private static Bitmap? Decode(byte[]? jpeg)
    {
        if (jpeg is null) return null;
        try
        {
            using var ms = new MemoryStream(jpeg);
            return Bitmap.DecodeToWidth(ms, 320);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsTrivialRtsp(string text, string host) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == "rtsp" && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)
        && u.Port is -1 or 554
        && (u.AbsolutePath is "" or "/") && string.IsNullOrEmpty(u.Query);

    // Editing the address or login invalidates a previous Connect result.
    partial void OnHostChanged(string value) { HostError = null; ResetConnect(); }
    partial void OnUsernameChanged(string value) => ResetConnect();
    partial void OnPasswordChanged(string value) => ResetConnect();

    partial void OnRtspMainTextChanged(string value) { if (_saveAttempted || RtspMainError is not null) ValidateFields(); }
    partial void OnRtspSubTextChanged(string value) { if (_saveAttempted || RtspSubError is not null) ValidateFields(); }
    partial void OnHttpPortTextChanged(string value) { if (_saveAttempted || PortsError is not null) ValidateFields(); }
    partial void OnOnvifPortTextChanged(string value) { if (_saveAttempted || PortsError is not null) ValidateFields(); }
    partial void OnSshPortTextChanged(string value) { if (_saveAttempted || PortsError is not null) ValidateFields(); }
    partial void OnSshUsernameChanged(string value) { if (SshError is not null) ValidateFields(); }
    partial void OnSshPasswordChanged(string value) { if (SshError is not null) ValidateFields(); }

    private void ResetConnect()
    {
        if (ConnectState == ConnectState.Idle) return;
        _connectCts?.Cancel();
        ConnectState = ConnectState.Idle;
        ConnectStatus = null;
    }

    public void CancelPending() => _connectCts?.Cancel();

    // ── Save ─────────────────────────────────────────────────────────────

    public bool TryBuildRequest(out NewCameraRequest? newRequest, out UpdateCameraRequest? updateRequest)
    {
        newRequest = null;
        updateRequest = null;
        _saveAttempted = true;
        ErrorMessage = null;

        NormalizeAddress();
        if (string.IsNullOrWhiteSpace(Host))
        {
            HostError = Localizer.Instance["CameraEditor.Error.HostRequired"];
            return false;
        }
        if (!ValidateFields()) return false;

        var host = Host.Trim();
        var rtspMain = string.IsNullOrWhiteSpace(RtspMainText)
            ? new Uri($"rtsp://{host}/")
            : new Uri(RtspMainText.Trim());
        var rtspSub = string.IsNullOrWhiteSpace(RtspSubText) ? null : new Uri(RtspSubText.Trim());
        var name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim();

        // Built only when both SSH fields are set; ValidateFields enforces
        // both-or-neither so we never store a half credential.
        var sshCredentials = string.IsNullOrEmpty(SshUsername) && string.IsNullOrEmpty(SshPassword)
            ? null
            : new CameraCredentials(SshUsername, SshPassword);

        var quality = SelectedStreamQuality?.Value ?? StreamQualityOverride.Auto;
        var analytics = BuildAnalyticsSettings();

        if (IsNew)
        {
            newRequest = new NewCameraRequest(
                Name: name, Host: host, HttpPort: HttpPort, OnvifPort: ParsePort(OnvifPortText),
                RtspMainUri: rtspMain, RtspSubUri: rtspSub, Credentials: Credentials(),
                GroupId: SelectedGroup?.Id, StreamQualityOverride: quality,
                SshCredentials: sshCredentials, SshPort: ParsePort(SshPortText), Analytics: analytics);
        }
        else
        {
            updateRequest = new UpdateCameraRequest(
                Name: name, Host: host, HttpPort: HttpPort, OnvifPort: ParsePort(OnvifPortText),
                RtspMainUri: rtspMain, RtspSubUri: rtspSub, Credentials: Credentials(),
                GroupId: SelectedGroup?.Id, StreamQualityOverride: quality,
                SshCredentials: sshCredentials, SshPort: ParsePort(SshPortText), Analytics: analytics);
        }
        return true;
    }

    // Validates everything but the address; errors land on their own field
    // and the section holding one is opened so it's never hidden.
    private bool ValidateFields()
    {
        RtspMainError = !string.IsNullOrWhiteSpace(RtspMainText) && !IsRtspUri(RtspMainText)
            ? Localizer.Instance["CameraEditor.Error.RtspMainInvalid"] : null;
        RtspSubError = !string.IsNullOrWhiteSpace(RtspSubText) && !IsRtspUri(RtspSubText)
            ? Localizer.Instance["CameraEditor.Error.RtspSubInvalid"] : null;

        PortsError = !IsPortOrBlank(HttpPortText) ? Localizer.Instance["CameraEditor.Error.HttpPortInvalid"]
            : !IsPortOrBlank(OnvifPortText) ? Localizer.Instance["CameraEditor.Error.OnvifPortInvalid"]
            : !IsPortOrBlank(SshPortText) ? Localizer.Instance["CameraEditor.Error.SshPortInvalid"]
            : null;

        // SSH login is password-based here — both fields, or neither.
        SshError = string.IsNullOrEmpty(SshUsername) != string.IsNullOrEmpty(SshPassword)
            ? Localizer.Instance["CameraEditor.Error.SshCredsIncomplete"] : null;

        if (RtspMainError is not null || RtspSubError is not null) IsStreamsOpen = true;
        if (PortsError is not null) IsPortsOpen = true;
        if (SshError is not null) IsSshOpen = true;
        return RtspMainError is null && RtspSubError is null && PortsError is null && SshError is null;
    }

    private static bool IsRtspUri(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "rtsp" or "rtsps" && !string.IsNullOrEmpty(u.Host);

    private static bool IsPortOrBlank(string text) =>
        string.IsNullOrWhiteSpace(text) || ParsePort(text) is not null;

    private static int? ParsePort(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : null;

    private CameraCredentials? Credentials() =>
        string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password) ? null : new CameraCredentials(Username, Password);

    private AnalyticsSettings BuildAnalyticsSettings()
    {
        var classIds = AnalyticsClasses.Where(c => c.IsSelected).Select(c => c.ClassId).ToArray();
        return new AnalyticsSettings(
            Enabled: AiEnabled,
            ClassIds: classIds.Length > 0 ? classIds : null,
            ConfidenceThreshold: (float)AiThreshold,
            AnalyticsFps: AiFps,
            AutoRecord: AiAutoRecord,
            PostEventSeconds: AiPostEventSeconds);
    }

    // ── Vendor RTSP templates (Streams section) ──────────────────────────

    // Fills both main/sub from the host field. Credentials ride the RTSP auth
    // handshake (the login fields), so they are NOT embedded in the URI —
    // except XM/Xiongmai (NETSurveillance), whose firmware wants the login in
    // the path itself; there the fields are used, falling back to the stock
    // "admin" + empty password those cameras ship with.
    [RelayCommand]
    private void ApplyRtspTemplate(string vendor)
    {
        NormalizeAddress();
        if (string.IsNullOrWhiteSpace(Host)) return;
        var host = Host.Trim();
        var user = string.IsNullOrWhiteSpace(Username) ? "admin" : Username.Trim();
        (RtspMainText, RtspSubText) = vendor switch
        {
            "openipc" => (CameraConnectService.MajesticMain(host).ToString(), CameraConnectService.MajesticSub(host).ToString()),
            "xm" => (
                $"rtsp://{host}:554/user={user}&password={Password}&channel=1&stream=0.sdp?real_stream",
                $"rtsp://{host}:554/user={user}&password={Password}&channel=1&stream=1.sdp?real_stream"),
            "xm-sofia" => (
                $"rtsp://{host}:554/user={user}&password={SofiaHash(Password)}&channel=1&stream=0.sdp?real_stream",
                $"rtsp://{host}:554/user={user}&password={SofiaHash(Password)}&channel=1&stream=1.sdp?real_stream"),
            "hikvision" => (
                $"rtsp://{host}:554/Streaming/Channels/101",
                $"rtsp://{host}:554/Streaming/Channels/102"),
            "dahua" => (
                $"rtsp://{host}:554/cam/realmonitor?channel=1&subtype=0",
                $"rtsp://{host}:554/cam/realmonitor?channel=1&subtype=1"),
            "reolink" => (
                $"rtsp://{host}:554/h264Preview_01_main",
                $"rtsp://{host}:554/h264Preview_01_sub"),
            "tplink" => (
                $"rtsp://{host}:554/stream1",
                $"rtsp://{host}:554/stream2"),
            "uniview" => (
                $"rtsp://{host}:554/media/video1",
                $"rtsp://{host}:554/media/video2"),
            _ => (RtspMainText, RtspSubText),
        };
    }

    // XM/Xiongmai "Sofia" password digest (NETSurveillance/DVRIP): pairs of MD5
    // bytes summed mod 62, mapped to [0-9A-Za-z], 8 chars. Newer XM firmwares
    // reject the plaintext password in the RTSP URL and want this instead
    // (empty password hashes to the well-known "tlJwpbo6"). MD5 here is a
    // protocol requirement, not our choice of cryptography.
    private static string SofiaHash(string password)
    {
        var md5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
        {
            var n = (md5[2 * i] + md5[2 * i + 1]) % 62;
            chars[i] = (char)(n < 10 ? '0' + n : n < 36 ? 'A' + n - 10 : 'a' + n - 36);
        }
        return new string(chars);
    }
}

// Toggleable detection class in the editor (Phase 15.4).
public sealed partial class DetectionClassOption : ObservableObject
{
    public DetectionClassOption(int classId, string name)
    {
        ClassId = classId;
        Name = name;
    }

    public int ClassId { get; }
    public string Name { get; }
    public string DisplayName => DetectionClasses.Localize(Name);

    [ObservableProperty] private bool _isSelected;
}

// Onvif/IsMajestic: what Connect detected, for the caller to persist (null =
// not probed this time, keep what's stored).
public sealed record CameraEditorResult(
    NewCameraRequest? NewRequest,
    UpdateCameraRequest? UpdateRequest,
    OnvifProbeResult? Onvif = null,
    bool? IsMajestic = null,
    CameraEditorRedirect Redirect = CameraEditorRedirect.None);

// The add dialog's header links: leave the form for discovery or a QR code.
public enum CameraEditorRedirect { None, Discover, ScanQr }

// Combo item for the per-camera SD/HD override picker (Phase 12.2).
public sealed record StreamQualityOption(string Display, StreamQualityOverride Value);
