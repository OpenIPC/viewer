using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Ssh;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class SettingsPageViewModel : ViewModelBase
{
    private readonly UserSettingsService _settings;
    private readonly IFileSystem _fs;
    private readonly IDialogService _dialogs;
    private readonly ISshHostKeyStore _hostKeys;
    private readonly OpenIPC.Viewer.Core.Persistence.IConfigBackupService _backup;
    private bool _suppressSave;

    public string Title => Localizer.Instance["Settings.Title"];

    [ObservableProperty] private bool _showTelemetryOverlay;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private bool _rawConfigEditorEnabled;
    [ObservableProperty] private bool _autoScanLanOnStartup;
    [ObservableProperty] private int _maxConcurrentGridSessions;
    [ObservableProperty] private string _rtspTransport = "tcp";
    [ObservableProperty] private bool _autoSdHd = true;

    // AI analytics (Phase 15.2). On → pin the CPU execution provider.
    [ObservableProperty] private bool _aiForceCpu;

    [ObservableProperty] private NetworkInterfaceOption? _selectedNetworkInterface;
    [ObservableProperty] private string _language = "system";

    // SSH section (Phase 13).
    [ObservableProperty] private bool _sshStrictHostKey = true;
    [ObservableProperty] private int _sshDefaultPort = 22;
    [ObservableProperty] private int _sshTerminalFontSize = 14;
    [ObservableProperty] private string _majesticConfigPath = "/etc/majestic.yaml";
    [ObservableProperty] private bool _hostKeysJustCleared;

    // Notifications (Phase 19.3).
    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private bool _notifyOnMotion = true;
    [ObservableProperty] private bool _notifyOnDetection = true;
    [ObservableProperty] private int _notificationCooldownSeconds = 30;
    [ObservableProperty] private bool _quietHoursEnabled;
    [ObservableProperty] private int _quietHoursStartHour = 22;
    [ObservableProperty] private int _quietHoursEndHour = 7;
    public int[] HourOptions { get; } = System.Linq.Enumerable.Range(0, 24).ToArray();
    public int[] CooldownOptions { get; } = new[] { 0, 10, 30, 60, 120, 300 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRecordingsDirectory))]
    [NotifyPropertyChangedFor(nameof(IsRecordingsDirOverridden))]
    private string _recordingsDirOverride = "";

    // Master-detail state. -1 = no section selected (list view on narrow);
    // 0..5 maps to Appearance/Video/Recording/Discovery/Advanced/About. On
    // wide viewports a section is always selected (defaults to 0). Code-behind
    // toggles IsWide based on viewport width and resizes the Grid columns to
    // hide one pane on narrow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearance))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    [NotifyPropertyChangedFor(nameof(IsDiscovery))]
    [NotifyPropertyChangedFor(nameof(IsSsh))]
    [NotifyPropertyChangedFor(nameof(IsAdvanced))]
    [NotifyPropertyChangedFor(nameof(IsAbout))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowDetail))]
    [NotifyPropertyChangedFor(nameof(ShowBackButton))]
    private int _selectedSectionIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowDetail))]
    [NotifyPropertyChangedFor(nameof(ShowBackButton))]
    private bool _isWide;

    public bool IsAppearance => SelectedSectionIndex == 0;
    public bool IsVideo      => SelectedSectionIndex == 1;
    public bool IsRecording  => SelectedSectionIndex == 2;
    public bool IsDiscovery  => SelectedSectionIndex == 3;
    public bool IsSsh        => SelectedSectionIndex == 4;
    public bool IsAdvanced   => SelectedSectionIndex == 5;
    public bool IsAbout      => SelectedSectionIndex == 6;

    public bool ShowList       => IsWide || SelectedSectionIndex < 0;
    public bool ShowDetail     => IsWide || SelectedSectionIndex >= 0;
    public bool ShowBackButton => !IsWide && SelectedSectionIndex >= 0;

    partial void OnIsWideChanged(bool value)
    {
        // Transitioning narrow→wide with nothing selected would leave the
        // detail pane blank; default to the first section.
        if (value && SelectedSectionIndex < 0)
            SelectedSectionIndex = 0;
    }

    [RelayCommand]
    private void BackToList() => SelectedSectionIndex = -1;

    public bool IsRecordingsDirOverridden => !string.IsNullOrWhiteSpace(RecordingsDirOverride);

    // What RecordingService will actually use — override if set, otherwise
    // the platform default. Updated reactively via the two NotifyPropertyChangedFor.
    public string EffectiveRecordingsDirectory =>
        IsRecordingsDirOverridden ? RecordingsDirOverride : _fs.RecordingsDir.FullName;

    public int[] GridSessionOptions { get; } = new[] { 4, 9, 16, 25 };
    public string[] TransportOptions { get; } = new[] { "tcp", "udp" };
    public string[] LanguageOptions { get; } = new[] { "system", "en", "ru" };

    // Auto + each usable LAN adapter (Phase 12.6). Display shown in the combo,
    // Value persisted ("" = auto).
    public IReadOnlyList<NetworkInterfaceOption> NetworkInterfaceOptions { get; }

    public string AppDataDirectory => _fs.AppDataDir.FullName;
    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.1.0";
    public string RepositoryUrl => "https://github.com/keyldev/openipc-viewer";

    public SettingsPageViewModel(
        UserSettingsService settings,
        IFileSystem fs,
        IDialogService dialogs,
        INetworkInterfaceProvider nics,
        ISshHostKeyStore hostKeys,
        OpenIPC.Viewer.Core.Persistence.IConfigBackupService backup)
    {
        _settings = settings;
        _fs = fs;
        _dialogs = dialogs;
        _hostKeys = hostKeys;
        _backup = backup;

        var options = new List<NetworkInterfaceOption>
        {
            new(Localizer.Instance["Settings.Video.NetworkInterface.Auto"], ""),
        };
        options.AddRange(nics.GetCandidates().Select(c => new NetworkInterfaceOption(c.DisplayName, c.Address)));
        NetworkInterfaceOptions = options;

        Load();
    }

    private void Load()
    {
        // _suppressSave keeps the OnXxxChanged hooks from re-saving while we
        // hydrate from disk (each property setter would otherwise round-trip
        // the file once on construction).
        _suppressSave = true;
        try
        {
            var s = _settings.Current;
            ShowTelemetryOverlay = s.ShowTelemetryOverlay;
            VerboseLogging = s.VerboseLogging;
            RawConfigEditorEnabled = s.RawConfigEditorEnabled;
            AutoScanLanOnStartup = s.AutoScanLanOnStartup;
            MaxConcurrentGridSessions = s.MaxConcurrentGridSessions;
            RtspTransport = s.RtspTransport;
            AutoSdHd = s.AutoSdHd;
            AiForceCpu = string.Equals(s.AiAcceleration, "force-cpu", StringComparison.OrdinalIgnoreCase);
            SelectedNetworkInterface =
                NetworkInterfaceOptions.FirstOrDefault(o => o.Value == s.PreferredNetworkInterface)
                ?? NetworkInterfaceOptions[0];
            RecordingsDirOverride = s.RecordingsDirOverride;
            Language = s.Language;
            SshStrictHostKey = s.SshStrictHostKey;
            SshDefaultPort = s.SshDefaultPort;
            SshTerminalFontSize = s.SshTerminalFontSize;
            MajesticConfigPath = s.MajesticConfigPath;
            NotificationsEnabled = s.NotificationsEnabled;
            NotifyOnMotion = s.NotifyOnMotion;
            NotifyOnDetection = s.NotifyOnDetection;
            NotificationCooldownSeconds = s.NotificationCooldownSeconds;
            QuietHoursEnabled = s.QuietHoursEnabled;
            QuietHoursStartHour = s.QuietHoursStartHour;
            QuietHoursEndHour = s.QuietHoursEndHour;
        }
        finally { _suppressSave = false; }
    }

    partial void OnShowTelemetryOverlayChanged(bool value) => Persist();
    partial void OnVerboseLoggingChanged(bool value) => Persist();
    partial void OnRawConfigEditorEnabledChanged(bool value) => Persist();
    partial void OnAutoScanLanOnStartupChanged(bool value) => Persist();
    partial void OnMaxConcurrentGridSessionsChanged(int value) => Persist();
    partial void OnRtspTransportChanged(string value) => Persist();
    partial void OnAutoSdHdChanged(bool value) => Persist();
    partial void OnAiForceCpuChanged(bool value) => Persist();
    partial void OnSelectedNetworkInterfaceChanged(NetworkInterfaceOption? value) => Persist();
    partial void OnRecordingsDirOverrideChanged(string value) => Persist();
    partial void OnLanguageChanged(string value) => Persist();
    partial void OnSshStrictHostKeyChanged(bool value) => Persist();
    partial void OnSshDefaultPortChanged(int value) => Persist();
    partial void OnSshTerminalFontSizeChanged(int value) => Persist();
    partial void OnMajesticConfigPathChanged(string value) => Persist();
    partial void OnNotificationsEnabledChanged(bool value) => Persist();
    partial void OnNotifyOnMotionChanged(bool value) => Persist();
    partial void OnNotifyOnDetectionChanged(bool value) => Persist();
    partial void OnNotificationCooldownSecondsChanged(int value) => Persist();
    partial void OnQuietHoursEnabledChanged(bool value) => Persist();
    partial void OnQuietHoursStartHourChanged(int value) => Persist();
    partial void OnQuietHoursEndHourChanged(int value) => Persist();

    private void Persist()
    {
        if (_suppressSave) return;
        var next = _settings.Current with
        {
            ShowTelemetryOverlay = ShowTelemetryOverlay,
            VerboseLogging = VerboseLogging,
            RawConfigEditorEnabled = RawConfigEditorEnabled,
            AutoScanLanOnStartup = AutoScanLanOnStartup,
            MaxConcurrentGridSessions = MaxConcurrentGridSessions,
            RtspTransport = RtspTransport,
            AutoSdHd = AutoSdHd,
            AiAcceleration = AiForceCpu ? "force-cpu" : "auto",
            PreferredNetworkInterface = SelectedNetworkInterface?.Value ?? "",
            RecordingsDirOverride = RecordingsDirOverride,
            Language = Language,
            SshStrictHostKey = SshStrictHostKey,
            SshDefaultPort = SshDefaultPort,
            SshTerminalFontSize = SshTerminalFontSize,
            MajesticConfigPath = MajesticConfigPath,
            NotificationsEnabled = NotificationsEnabled,
            NotifyOnMotion = NotifyOnMotion,
            NotifyOnDetection = NotifyOnDetection,
            NotificationCooldownSeconds = NotificationCooldownSeconds,
            QuietHoursEnabled = QuietHoursEnabled,
            QuietHoursStartHour = QuietHoursStartHour,
            QuietHoursEndHour = QuietHoursEndHour,
        };
        // Fire-and-forget; binding setters are synchronous and any save
        // error is logged inside UpdateAsync.
        _ = _settings.UpdateAsync(next, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ResetHostKeysAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Localizer.Instance["Settings.Ssh.ResetTitle"],
            Localizer.Instance["Settings.Ssh.ResetMessage"],
            Localizer.Instance["Settings.Ssh.ResetConfirm"],
            Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!confirmed)
            return;

        await _hostKeys.ClearAsync(CancellationToken.None).ConfigureAwait(true);
        HostKeysJustCleared = true;
    }

    [RelayCommand]
    private async Task PickRecordingsDirectoryAsync()
    {
        var picked = await _dialogs.PickFolderAsync("Pick recordings folder").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked))
            RecordingsDirOverride = picked;
    }

    [RelayCommand]
    private void ResetRecordingsDirectory() => RecordingsDirOverride = "";

    [RelayCommand]
    private void OpenAppDataDirectory() => OpenInShell(_fs.AppDataDir.FullName);

    [RelayCommand]
    private void OpenRecordingsDirectory() => OpenInShell(EffectiveRecordingsDirectory);

    [RelayCommand]
    private void OpenRepository() => OpenInShell(RepositoryUrl);

    // --- Config export / import (Phase 19.2) ------------------------------
    [ObservableProperty] private string? _backupStatus;

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        try
        {
            var json = await _backup.ExportAsync(CancellationToken.None).ConfigureAwait(true);
            var path = await _dialogs.PickSaveFileAsync("openipc-config.json",
                Localizer.Instance["Settings.Backup.ExportTitle"], "json").ConfigureAwait(true);
            if (string.IsNullOrEmpty(path)) return;
            await System.IO.File.WriteAllTextAsync(path, json).ConfigureAwait(true);
            BackupStatus = Localizer.Instance["Settings.Backup.Exported"];
        }
        catch (Exception ex)
        {
            BackupStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Backup.FailedFormat"], ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        try
        {
            var path = await _dialogs.PickAnyFileAsync(Localizer.Instance["Settings.Backup.ImportTitle"]).ConfigureAwait(true);
            if (string.IsNullOrEmpty(path)) return;
            var json = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(true);

            var preview = await _backup.PreviewAsync(json, CancellationToken.None).ConfigureAwait(true);
            var confirmed = await _dialogs.ConfirmAsync(
                Localizer.Instance["Settings.Backup.ImportTitle"],
                string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Backup.ConfirmFormat"],
                    preview.CamerasAdded, preview.CamerasUpdated, preview.LayoutsAdded),
                Localizer.Instance["Settings.Backup.ImportConfirm"],
                Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
            if (!confirmed) return;

            await _backup.ImportAsync(json, CancellationToken.None).ConfigureAwait(true);
            BackupStatus = Localizer.Instance["Settings.Backup.Imported"];
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Messages.ConfigImportedMessage());
        }
        catch (Exception ex)
        {
            BackupStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Backup.FailedFormat"], ex.Message);
        }
    }

    private static void OpenInShell(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception) { /* best effort — Android sandbox blocks shell-open, desktop works */ }
    }
}

// Combo item for the Settings → Video network-interface picker (Phase 12.6).
// Display is the human label ("Auto" / "Ethernet (192.168.1.5)"); Value is the
// persisted IPv4 ("" = auto).
public sealed record NetworkInterfaceOption(string Display, string Value);
