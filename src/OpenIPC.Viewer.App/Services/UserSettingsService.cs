using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.App.Services;

// Persisted user preferences. UI ViewModels read Current + react to Changed;
// runtime side-effects (e.g. Serilog level switch) live in the platform host
// composition where the relevant library refs are available — keeps App
// project free of Serilog or platform-specific deps.
//
// Load is best-effort: a corrupt or missing file leaves defaults in place
// and logs a warning. Save is atomic (temp + move) to avoid half-written
// files on crash.
public sealed class UserSettingsService : IUserSettingsAccessor
{
    public string? RecordingsDirectoryOverride =>
        string.IsNullOrWhiteSpace(Current.RecordingsDirOverride) ? null : Current.RecordingsDirOverride;
    public int MaxConcurrentGridSessions => Current.MaxConcurrentGridSessions;
    public string? PreferredNetworkInterface =>
        string.IsNullOrWhiteSpace(Current.PreferredNetworkInterface) ? null : Current.PreferredNetworkInterface;
    public bool SshStrictHostKey => Current.SshStrictHostKey;
    public int SshDefaultPort => Current.SshDefaultPort < 1 ? 22 : Current.SshDefaultPort;
    public string MajesticConfigPath =>
        string.IsNullOrWhiteSpace(Current.MajesticConfigPath) ? "/etc/majestic.yaml" : Current.MajesticConfigPath;
    public int ActiveLayoutId => Current.ActiveLayoutId;
    public bool NotificationsEnabled => Current.NotificationsEnabled;
    public bool NotifyOnMotion => Current.NotifyOnMotion;
    public bool NotifyOnDetection => Current.NotifyOnDetection;
    public int NotificationCooldownSeconds => Current.NotificationCooldownSeconds;
    public bool QuietHoursEnabled => Current.QuietHoursEnabled;
    public int QuietHoursStartHour => Current.QuietHoursStartHour;
    public int QuietHoursEndHour => Current.QuietHoursEndHour;
    public OpenIPC.Viewer.Core.Analytics.AiAcceleration AiAcceleration =>
        string.Equals(Current.AiAcceleration, "force-cpu", StringComparison.OrdinalIgnoreCase)
            ? OpenIPC.Viewer.Core.Analytics.AiAcceleration.ForceCpu
            : OpenIPC.Viewer.Core.Analytics.AiAcceleration.Auto;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly ILogger<UserSettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserSettings Current { get; private set; } = UserSettings.Default;

    public event EventHandler? Changed;

    public UserSettingsService(IFileSystem fs, ILogger<UserSettingsService> logger)
    {
        _path = Path.Combine(fs.AppDataDir.FullName, "usersettings.json");
        _logger = logger;
        TryLoad();
    }

    public async Task UpdateAsync(UserSettings next, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Current = next;
            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryLoad()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var stream = File.OpenRead(_path);
            var loaded = JsonSerializer.Deserialize<UserSettings>(stream, JsonOpts);
            if (loaded is not null) Current = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load usersettings.json — using defaults");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var tmp = _path + ".tmp";
        var stream = File.Create(tmp);
        // A bare `await using` has no ConfigureAwait — when this method is
        // entered on the UI thread (free-gate UpdateAsync runs synchronously up
        // to here), the DisposeAsync continuation would be posted back to the
        // blocked dispatcher and deadlock a caller that synchronously waits on
        // the task (MainWindow.OnClosing did exactly that).
        await using (stream.ConfigureAwait(false))
            await JsonSerializer.SerializeAsync(stream, Current, JsonOpts, ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }
}
