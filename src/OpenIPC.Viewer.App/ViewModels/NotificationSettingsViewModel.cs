using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.App.ViewModels;

// Settings → Notifications: what fires (types + thresholds + cooldown + quiet
// hours), how it is shown (app toast with own sound, or native Windows toast),
// and the external actions (TCP/UDP signal, webhook, e-mail, program).
//
// Field edits are saved debounced (typing in a text box must not rewrite the
// settings file per keystroke) through UserSettingsService's functional update,
// so they never clobber fields owned by the main settings page. The SMTP
// password is never part of usersettings.json — "Save login" writes it to the
// credential store (Windows Credential Manager on Windows).
public sealed partial class NotificationSettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private static readonly HashSet<string> Persisted = new(StringComparer.Ordinal)
    {
        nameof(NotificationsEnabled), nameof(NotifyOnMotion), nameof(NotifyOnDetection),
        nameof(NotificationCooldownSeconds), nameof(QuietHoursEnabled), nameof(QuietHoursStartHour),
        nameof(QuietHoursEndHour), nameof(MinConfidencePercent), nameof(MinDuration), nameof(MinObjects),
        nameof(UseSystemStyle), nameof(SoundMode), nameof(Volume),
        nameof(ActionsEnabled), nameof(ActionsIgnoreQuietHours), nameof(ActionsCooldown),
        nameof(ActionsOnMotion), nameof(ActionsOnDetection),
        nameof(SignalEnabled), nameof(SignalProtocol), nameof(SignalHost), nameof(SignalPort), nameof(SignalPayload),
        nameof(HttpEnabled), nameof(HttpMethod), nameof(HttpUrl), nameof(HttpBody),
        nameof(EmailEnabled), nameof(EmailTransport), nameof(EmailTo), nameof(EmailSubject), nameof(EmailBody),
        nameof(SmtpHost), nameof(SmtpPort), nameof(SmtpSecurity), nameof(SmtpFrom),
        nameof(ProgramEnabled), nameof(ProgramPath), nameof(ProgramArguments),
    };

    private readonly UserSettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly NotificationSoundService _sound;
    private readonly ISystemNotificationSink? _system;
    private readonly IEventActionDispatcher _actions;
    private readonly IMailCredentialStore _credentials;
    private readonly IDialogService _dialogs;
    private readonly ILogger<NotificationSettingsViewModel> _log;

    private bool _suppressSave;
    private bool _credentialsDirty;
    private CancellationTokenSource? _pendingSave;
    private string _customSoundFile = "";

    public NotificationSettingsViewModel(
        UserSettingsService settings,
        INotificationService notifications,
        NotificationSoundService sound,
        IEventActionDispatcher actions,
        IMailCredentialStore credentials,
        IDialogService dialogs,
        ILogger<NotificationSettingsViewModel> log,
        ISystemNotificationSink? system = null)
    {
        _settings = settings;
        _notifications = notifications;
        _sound = sound;
        _actions = actions;
        _credentials = credentials;
        _dialogs = dialogs;
        _log = log;
        _system = system;

        var l = Localizer.Instance;
        DurationOptions = new[] { 0, 1, 2, 3, 5, 10, 20, 30, 60 }
            .Select(s => new IntOption(s == 0
                ? l["Settings.Notify.Duration.Any"]
                : string.Format(CultureInfo.CurrentCulture, l["Settings.Notify.Seconds"], s), s))
            .ToList();
        ObjectOptions = new[] { 1, 2, 3, 4, 5, 10 }
            .Select(n => new IntOption(n == 1
                ? l["Settings.Notify.Objects.Any"]
                : string.Format(CultureInfo.CurrentCulture, l["Settings.Notify.Objects.AtLeast"], n), n))
            .ToList();
        ActionCooldownOptions = new[] { 0, 10, 30, 60, 120, 300, 600 }
            .Select(s => new IntOption(s == 0
                ? l["Settings.Notify.Cooldown.None"]
                : string.Format(CultureInfo.CurrentCulture, l["Settings.Notify.Seconds"], s), s))
            .ToList();
        SoundModeOptions = new[]
        {
            new StringOption(l["Settings.Notify.Sound.Chime"], NotificationSoundService.SoundChime),
            new StringOption(l["Settings.Notify.Sound.Custom"], NotificationSoundService.SoundCustom),
            new StringOption(l["Settings.Notify.Sound.Off"], NotificationSoundService.SoundOff),
        };
        SignalProtocolOptions = new[]
        {
            new StringOption("TCP", EventActionSettings.ProtocolTcp),
            new StringOption("UDP", EventActionSettings.ProtocolUdp),
        };
        var transports = new List<StringOption>
        {
            new(l["Settings.Actions.Email.Transport.Smtp"], EventActionSettings.TransportSmtp),
        };
        if (IsWindows)
        {
            transports.Add(new(l["Settings.Actions.Email.Transport.Mapi"], EventActionSettings.TransportMapi));
            transports.Add(new(l["Settings.Actions.Email.Transport.Outlook"], EventActionSettings.TransportOutlook));
        }
        EmailTransportOptions = transports;
        SmtpSecurityOptions = new[]
        {
            new StringOption(l["Settings.Actions.Smtp.Security.Auto"], "auto"),
            new StringOption("SSL/TLS", "ssl"),
            new StringOption("STARTTLS", "starttls"),
            new StringOption(l["Settings.Actions.Smtp.Security.None"], "none"),
        };

        Load();
    }

    // --- platform gates ------------------------------------------------------

    public bool IsWindows { get; } = OperatingSystem.IsWindows();
    public bool IsDesktop { get; } = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    public bool IsSystemStyleSupported => _system is { IsSupported: true };
    public bool IsSoundAvailable => _sound.IsAvailable;

    // --- what fires ------------------------------------------------------------

    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private bool _notifyOnMotion = true;
    [ObservableProperty] private bool _notifyOnDetection = true;
    [ObservableProperty] private int _notificationCooldownSeconds = 30;
    [ObservableProperty] private bool _quietHoursEnabled;
    [ObservableProperty] private int _quietHoursStartHour = 22;
    [ObservableProperty] private int _quietHoursEndHour = 7;
    public int[] HourOptions { get; } = Enumerable.Range(0, 24).ToArray();
    public int[] CooldownOptions { get; } = { 0, 10, 30, 60, 120, 300 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinConfidenceText))]
    private double _minConfidencePercent;
    [ObservableProperty] private IntOption? _minDuration;
    [ObservableProperty] private IntOption? _minObjects;
    public IReadOnlyList<IntOption> DurationOptions { get; }
    public IReadOnlyList<IntOption> ObjectOptions { get; }

    public string MinConfidenceText => (int)MinConfidencePercent <= 0
        ? Localizer.Instance["Settings.Notify.Confidence.Off"]
        : $"≥ {(int)MinConfidencePercent} %";

    // --- how it looks / sounds -----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseAppStyle))]
    [NotifyPropertyChangedFor(nameof(ShowSoundSettings))]
    private bool _useSystemStyle;

    // Second radio button of the pair.
    public bool UseAppStyle
    {
        get => !UseSystemStyle;
        set { if (value) UseSystemStyle = false; }
    }

    public bool ShowSoundSettings => IsSoundAvailable && !UseSystemStyle;

    [ObservableProperty] private string? _systemStatus;
    [ObservableProperty] private string? _testStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSound))]
    private StringOption? _soundMode;
    public IReadOnlyList<StringOption> SoundModeOptions { get; }
    public bool IsCustomSound => SoundMode?.Value == NotificationSoundService.SoundCustom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private double _volume = 70;
    public string VolumeText => $"{(int)Volume} %";

    [ObservableProperty] private string _customSoundName = "";
    [ObservableProperty] private string? _soundStatus;

    // --- external actions ----------------------------------------------------

    [ObservableProperty] private bool _actionsEnabled;
    [ObservableProperty] private bool _actionsIgnoreQuietHours = true;
    [ObservableProperty] private bool _actionsOnMotion = true;
    [ObservableProperty] private bool _actionsOnDetection = true;
    [ObservableProperty] private IntOption? _actionsCooldown;
    public IReadOnlyList<IntOption> ActionCooldownOptions { get; }

    public string PlaceholdersHint { get; } =
        string.Join(" ", EventActionTemplate.Placeholders.Select(p => "{" + p + "}"));

    [ObservableProperty] private bool _signalEnabled;
    [ObservableProperty] private StringOption? _signalProtocol;
    [ObservableProperty] private string _signalHost = "";
    [ObservableProperty] private int _signalPort = 9000;
    [ObservableProperty] private string _signalPayload = "";
    [ObservableProperty] private string? _signalStatus;
    public IReadOnlyList<StringOption> SignalProtocolOptions { get; }

    [ObservableProperty] private bool _httpEnabled;
    [ObservableProperty] private string _httpMethod = "POST";
    [ObservableProperty] private string _httpUrl = "";
    [ObservableProperty] private string _httpBody = "";
    [ObservableProperty] private string? _httpStatus;
    public string[] HttpMethodOptions { get; } = { "POST", "GET", "PUT" };

    [ObservableProperty] private bool _emailEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmtp))]
    [NotifyPropertyChangedFor(nameof(IsMapi))]
    [NotifyPropertyChangedFor(nameof(IsOutlook))]
    private StringOption? _emailTransport;
    public IReadOnlyList<StringOption> EmailTransportOptions { get; }
    public bool IsSmtp => EmailTransport?.Value is null or EventActionSettings.TransportSmtp;
    public bool IsMapi => EmailTransport?.Value == EventActionSettings.TransportMapi;
    public bool IsOutlook => EmailTransport?.Value == EventActionSettings.TransportOutlook;

    [ObservableProperty] private string _emailTo = "";
    [ObservableProperty] private string _emailSubject = "";
    [ObservableProperty] private string _emailBody = "";
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private int _smtpPort = 587;
    [ObservableProperty] private StringOption? _smtpSecurity;
    public IReadOnlyList<StringOption> SmtpSecurityOptions { get; }
    [ObservableProperty] private string _smtpFrom = "";
    [ObservableProperty] private string _smtpUser = "";
    [ObservableProperty] private string _smtpPassword = "";
    [ObservableProperty] private string? _emailStatus;

    public string CredentialStorageHint => Localizer.Instance[IsWindows
        ? "Settings.Actions.Smtp.StoredIn.Windows"
        : "Settings.Actions.Smtp.StoredIn.Secure"];

    [ObservableProperty] private bool _programEnabled;
    [ObservableProperty] private string _programPath = "";
    [ObservableProperty] private string _programArguments = "";
    [ObservableProperty] private string? _programStatus;

    partial void OnSmtpUserChanged(string value) { if (!_suppressSave) _credentialsDirty = true; }
    partial void OnSmtpPasswordChanged(string value) { if (!_suppressSave) _credentialsDirty = true; }

    // --- load / save ---------------------------------------------------------

    private void Load()
    {
        _suppressSave = true;
        try
        {
            var s = _settings.Current;
            NotificationsEnabled = s.NotificationsEnabled;
            NotifyOnMotion = s.NotifyOnMotion;
            NotifyOnDetection = s.NotifyOnDetection;
            NotificationCooldownSeconds = s.NotificationCooldownSeconds;
            QuietHoursEnabled = s.QuietHoursEnabled;
            QuietHoursStartHour = s.QuietHoursStartHour;
            QuietHoursEndHour = s.QuietHoursEndHour;
            MinConfidencePercent = Math.Clamp(s.NotifyMinConfidencePercent, 0, 95);
            MinDuration = Pick(DurationOptions, s.NotifyMinDurationSeconds);
            MinObjects = Pick(ObjectOptions, s.NotifyMinObjects);

            UseSystemStyle = IsSystemStyleSupported &&
                             string.Equals(s.NotificationStyle, DesktopNotificationService.StyleWindows, StringComparison.OrdinalIgnoreCase);
            SoundMode = SoundModeOptions.FirstOrDefault(o => string.Equals(o.Value, s.NotificationSound, StringComparison.OrdinalIgnoreCase))
                        ?? SoundModeOptions[0];
            Volume = Math.Clamp(s.NotificationVolume, 0, 100);
            _customSoundFile = s.NotificationSoundFile ?? "";
            CustomSoundName = s.NotificationSoundName ?? "";

            var a = s.EventActions ?? EventActionSettings.Default;
            ActionsEnabled = a.Enabled;
            ActionsIgnoreQuietHours = a.IgnoreQuietHours;
            ActionsCooldown = Pick(ActionCooldownOptions, a.CooldownSeconds);
            ActionsOnMotion = a.OnMotion;
            ActionsOnDetection = a.OnDetection;
            SignalEnabled = a.SignalEnabled;
            SignalProtocol = SignalProtocolOptions.FirstOrDefault(o => string.Equals(o.Value, a.SignalProtocol, StringComparison.OrdinalIgnoreCase))
                             ?? SignalProtocolOptions[0];
            SignalHost = a.SignalHost ?? "";
            SignalPort = a.SignalPort > 0 ? a.SignalPort : 9000;
            SignalPayload = a.SignalPayload ?? "";
            HttpEnabled = a.HttpEnabled;
            HttpMethod = HttpMethodOptions.FirstOrDefault(m => string.Equals(m, a.HttpMethod, StringComparison.OrdinalIgnoreCase)) ?? "POST";
            HttpUrl = a.HttpUrl ?? "";
            HttpBody = a.HttpBody ?? "";
            EmailEnabled = a.EmailEnabled;
            EmailTransport = EmailTransportOptions.FirstOrDefault(o => string.Equals(o.Value, a.EmailTransport, StringComparison.OrdinalIgnoreCase))
                             ?? EmailTransportOptions[0];
            EmailTo = a.EmailTo ?? "";
            EmailSubject = a.EmailSubject ?? "";
            EmailBody = a.EmailBody ?? "";
            SmtpHost = a.SmtpHost ?? "";
            SmtpPort = a.SmtpPort > 0 ? a.SmtpPort : 587;
            SmtpSecurity = SmtpSecurityOptions.FirstOrDefault(o => string.Equals(o.Value, a.SmtpSecurity, StringComparison.OrdinalIgnoreCase))
                           ?? SmtpSecurityOptions[0];
            SmtpFrom = a.SmtpFrom ?? "";
            ProgramEnabled = a.ProgramEnabled;
            ProgramPath = a.ProgramPath ?? "";
            ProgramArguments = a.ProgramArguments ?? "";
        }
        finally { _suppressSave = false; }
    }

    // Called when the Settings page is shown: the SMTP login (async store) and
    // the Windows notification state (a WinRT call).
    public async Task LoadAsync()
    {
        // A login/password typed but not saved yet (user left the page and came
        // back) is kept, not overwritten from the store.
        if (!_credentialsDirty)
        {
            try
            {
                var cred = await _credentials.GetAsync(CancellationToken.None).ConfigureAwait(true);
                _suppressSave = true;
                try
                {
                    SmtpUser = cred?.UserName ?? "";
                    SmtpPassword = cred?.Password ?? "";
                }
                finally { _suppressSave = false; }
                _credentialsDirty = false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not read the SMTP login from {Store}", _credentials.StorageName);
            }
        }
        await RefreshSystemStatusAsync().ConfigureAwait(true);
    }

    private static IntOption Pick(IReadOnlyList<IntOption> options, int value) =>
        options.FirstOrDefault(o => o.Value == value)
        ?? options.LastOrDefault(o => o.Value <= value)
        ?? options[0];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_suppressSave || e.PropertyName is null || !Persisted.Contains(e.PropertyName)) return;
        if (e.PropertyName == nameof(UseSystemStyle)) _ = RefreshSystemStatusAsync();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        CancelPendingSave();
        var cts = _pendingSave = new CancellationTokenSource();
        _ = SaveLaterAsync(cts.Token);
    }

    private void CancelPendingSave()
    {
        _pendingSave?.Cancel();
        _pendingSave?.Dispose();
        _pendingSave = null;
    }

    private async Task SaveLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDelay, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SaveQuietlyAsync().ConfigureAwait(true);
    }

    // Fire-and-forget saves (debounce, page detach) log instead of throwing:
    // a locked or full disk must not take the app down.
    private async Task SaveQuietlyAsync()
    {
        try
        {
            await SaveNowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving notification settings failed");
        }
    }

    // Page left: write a still-pending debounced edit, and a login/password that
    // was typed but never saved with the button.
    public async Task FlushAsync()
    {
        if (_pendingSave is not null) await SaveQuietlyAsync().ConfigureAwait(true);
        if (_credentialsDirty) await SaveCredentialsAsync().ConfigureAwait(true);
    }

    // Writes immediately (Test buttons need the current values on disk).
    public Task SaveNowAsync()
    {
        CancelPendingSave();

        // Snapshot on the UI thread; the mutation may run on the save thread.
        var enabled = NotificationsEnabled;
        var onMotion = NotifyOnMotion;
        var onDetection = NotifyOnDetection;
        var cooldown = NotificationCooldownSeconds;
        var quiet = QuietHoursEnabled;
        var quietStart = QuietHoursStartHour;
        var quietEnd = QuietHoursEndHour;
        var minConfidence = (int)Math.Round(MinConfidencePercent);
        var minDuration = MinDuration?.Value ?? 0;
        var minObjects = MinObjects?.Value ?? 1;
        var style = UseSystemStyle ? DesktopNotificationService.StyleWindows : DesktopNotificationService.StyleApp;
        var sound = SoundMode?.Value ?? NotificationSoundService.SoundChime;
        var volume = (int)Math.Round(Volume);
        var soundFile = _customSoundFile;
        var soundName = CustomSoundName;
        var actions = BuildActions();

        return _settings.UpdateAsync(s => s with
        {
            NotificationsEnabled = enabled,
            NotifyOnMotion = onMotion,
            NotifyOnDetection = onDetection,
            NotificationCooldownSeconds = cooldown,
            QuietHoursEnabled = quiet,
            QuietHoursStartHour = quietStart,
            QuietHoursEndHour = quietEnd,
            NotifyMinConfidencePercent = minConfidence,
            NotifyMinDurationSeconds = minDuration,
            NotifyMinObjects = minObjects,
            NotificationStyle = style,
            NotificationSound = sound,
            NotificationVolume = volume,
            NotificationSoundFile = soundFile,
            NotificationSoundName = soundName,
            EventActions = actions,
        });
    }

    private EventActionSettings BuildActions() => new(
        Enabled: ActionsEnabled,
        IgnoreQuietHours: ActionsIgnoreQuietHours,
        CooldownSeconds: ActionsCooldown?.Value ?? 60,
        OnMotion: ActionsOnMotion,
        OnDetection: ActionsOnDetection,
        SignalEnabled: SignalEnabled,
        SignalProtocol: SignalProtocol?.Value ?? EventActionSettings.ProtocolTcp,
        SignalHost: (SignalHost ?? "").Trim(),
        SignalPort: SignalPort,
        SignalPayload: SignalPayload ?? "",
        HttpEnabled: HttpEnabled,
        HttpMethod: HttpMethod,
        HttpUrl: (HttpUrl ?? "").Trim(),
        HttpBody: HttpBody ?? "",
        EmailEnabled: EmailEnabled,
        EmailTransport: EmailTransport?.Value ?? EventActionSettings.TransportSmtp,
        EmailTo: (EmailTo ?? "").Trim(),
        EmailSubject: EmailSubject ?? "",
        EmailBody: EmailBody ?? "",
        SmtpHost: (SmtpHost ?? "").Trim(),
        SmtpPort: SmtpPort,
        SmtpSecurity: SmtpSecurity?.Value ?? "auto",
        SmtpFrom: (SmtpFrom ?? "").Trim(),
        ProgramEnabled: ProgramEnabled,
        ProgramPath: (ProgramPath ?? "").Trim(),
        ProgramArguments: ProgramArguments ?? "");

    // --- commands: look & sound ----------------------------------------------

    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        TestStatus = null;
        try
        {
            await SaveNowAsync().ConfigureAwait(true);
            // Straight at the sink — bypasses thresholds/cooldown on purpose, it's a preview.
            _notifications.Show(new NotificationRequest(
                Localizer.Instance["Settings.Notifications.TestTitle"],
                Localizer.Instance["Settings.Notifications.TestBody"],
                EventKind.Motion));
            if (UseSystemStyle) await RefreshSystemStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An AsyncRelayCommand would rethrow on the UI thread and end the process.
            _log.LogWarning(ex, "Test notification failed");
            TestStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Actions.TestFailed"], ex.Message);
        }
    }

    [RelayCommand]
    private void PreviewSound() =>
        _sound.Play(SoundMode?.Value ?? NotificationSoundService.SoundChime, _customSoundFile, (int)Math.Round(Volume));

    [RelayCommand]
    private async Task PickSoundAsync()
    {
        var path = await _dialogs.PickAnyFileAsync(Localizer.Instance["Settings.Notify.Sound.PickTitle"]).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            SoundStatus = Localizer.Instance["Settings.Notify.Sound.Importing"];
            _customSoundFile = await _sound.ImportAsync(path, CancellationToken.None).ConfigureAwait(true);
            CustomSoundName = Path.GetFileName(path);
            SoundMode = SoundModeOptions.First(o => o.Value == NotificationSoundService.SoundCustom);
            SoundStatus = null;
            await SaveNowAsync().ConfigureAwait(true);
            PreviewSound();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Importing notification sound {Path} failed", path);
            SoundStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Notify.Sound.ImportFailed"], ex.Message);
        }
    }

    [RelayCommand]
    private void OpenSystemSettings() => _system?.OpenSettings();

    // Only in the Windows style: querying the state registers our AppUserModelID,
    // which users of the built-in style never need.
    private async Task RefreshSystemStatusAsync()
    {
        if (_system is not { IsSupported: true } || !UseSystemStyle) return;
        var state = await Task.Run(() => _system.State).ConfigureAwait(true);
        SystemStatus = Localizer.Instance[state switch
        {
            SystemNotificationState.Enabled => "Settings.Notify.System.Enabled",
            SystemNotificationState.DisabledForApp => "Settings.Notify.System.DisabledForApp",
            SystemNotificationState.DisabledForUser => "Settings.Notify.System.DisabledForUser",
            SystemNotificationState.DisabledByPolicy => "Settings.Notify.System.DisabledByPolicy",
            _ => "Settings.Notify.System.Unknown",
        }];
    }

    // --- commands: external actions ------------------------------------------

    [RelayCommand]
    private Task TestSignalAsync() => TestChannelAsync(EventActionChannel.Signal, s => SignalStatus = s);

    [RelayCommand]
    private Task TestHttpAsync() => TestChannelAsync(EventActionChannel.Http, s => HttpStatus = s);

    [RelayCommand]
    private async Task TestEmailAsync()
    {
        if (IsSmtp && _credentialsDirty) await SaveCredentialsAsync().ConfigureAwait(true);
        await TestChannelAsync(EventActionChannel.Email, s => EmailStatus = s).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task TestProgramAsync() => TestChannelAsync(EventActionChannel.Program, s => ProgramStatus = s);

    private async Task TestChannelAsync(EventActionChannel channel, Action<string?> report)
    {
        report(Localizer.Instance["Settings.Actions.Testing"]);
        try
        {
            await SaveNowAsync().ConfigureAwait(true);
            var ctx = EventActionContext.Sample(DateTime.UtcNow) with
            {
                Title = Localizer.Instance["Settings.Notifications.TestTitle"],
            };
            await _actions.TestAsync(channel, ctx, BuildActions(), CancellationToken.None).ConfigureAwait(true);
            report(Localizer.Instance["Settings.Actions.TestOk"]);
        }
        catch (Exception ex)
        {
            report(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Actions.TestFailed"], ex.Message));
        }
    }

    [RelayCommand]
    private async Task SaveCredentialsAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SmtpUser) && string.IsNullOrEmpty(SmtpPassword))
                await _credentials.ClearAsync(CancellationToken.None).ConfigureAwait(true);
            else
                await _credentials.SetAsync(new MailCredential(SmtpUser.Trim(), SmtpPassword), CancellationToken.None).ConfigureAwait(true);
            _credentialsDirty = false;
            EmailStatus = CredentialStorageHint;
        }
        catch (Exception ex)
        {
            EmailStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings.Actions.TestFailed"], ex.Message);
        }
    }

    // Control Panel → Credential Manager, where the stored SMTP login can be
    // reviewed, edited or removed with the standard Windows tool.
    [RelayCommand]
    private void OpenCredentialManager()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "/name Microsoft.CredentialManager")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open Credential Manager");
        }
    }

    [RelayCommand]
    private async Task PickProgramAsync()
    {
        var path = await _dialogs.PickAnyFileAsync(Localizer.Instance["Settings.Actions.Program.PickTitle"]).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path)) ProgramPath = path;
    }
}

// Combo items with a localized label and the persisted value.
public sealed record IntOption(string Display, int Value);
public sealed record StringOption(string Display, string Value);
