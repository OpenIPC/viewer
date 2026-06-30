using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.App.Services;

// Network config auto-sync (Phase 20). On startup — and on demand from Settings
// — mirrors the cameras + layouts from the JSON at ConfigSyncPath (a local or
// UNC file, e.g. a shared folder). The file is the source of truth, so an admin
// can add/remove a camera once centrally instead of visiting every machine.
//
// Resilience by design: if the path is unreachable the last-applied local DB
// keeps working (we just skip). An unchanged file (same SHA-256 as last applied)
// is not re-imported. Mirror is destructive (removes local extras), so it only
// runs when the user explicitly enables it.
public sealed class ConfigSyncService
{
    // A dead UNC share can wedge the native open well past a CancellationToken
    // check, so reads are bounded by this wall-clock timeout.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);

    // Secrets-store key holding the fleet credential passphrase (DPAPI-encrypted
    // at rest, per-machine). Never written to usersettings.json.
    private const string PassphraseKey = "configsync:passphrase";

    private readonly IConfigBackupService _backup;
    private readonly UserSettingsService _settings;
    private readonly ISecretsStore _secrets;
    private readonly ILogger<ConfigSyncService> _logger;

    public ConfigSyncService(
        IConfigBackupService backup,
        UserSettingsService settings,
        ISecretsStore secrets,
        ILogger<ConfigSyncService> logger)
    {
        _backup = backup;
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
    }

    // Fleet passphrase used to encrypt/decrypt synced credentials. Stored in the
    // OS secrets store; the UI reads/writes it through these.
    public Task<string?> GetPassphraseAsync(CancellationToken ct) => _secrets.GetAsync(PassphraseKey, ct);

    public Task SetPassphraseAsync(string value, CancellationToken ct) =>
        string.IsNullOrEmpty(value)
            ? _secrets.RemoveAsync(PassphraseKey, ct)
            : _secrets.SetAsync(PassphraseKey, value, ct);

    // The passphrase to use for THIS sync, or null when credential sync is off.
    private async Task<string?> ResolveCredentialPassphraseAsync(CancellationToken ct) =>
        _settings.Current.ConfigSyncIncludeCredentials
            ? await GetPassphraseAsync(ct).ConfigureAwait(false)
            : null;

    public bool IsConfigured =>
        _settings.Current.ConfigSyncEnabled
        && !string.IsNullOrWhiteSpace(_settings.Current.ConfigSyncPath);

    // Startup hook: best-effort, never throws — a sync failure must not block
    // the app from opening on the last-applied local config.
    public async Task RunStartupSyncAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;
        try { await SyncAsync(force: false, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup config sync failed; using last-applied local config");
        }
    }

    // Read → change-check → mirror. force re-applies even if the signature
    // matches (the "Sync now" button). Returns a structured outcome for the UI.
    public async Task<ConfigSyncOutcome> SyncAsync(bool force, CancellationToken ct)
    {
        var s = _settings.Current;
        if (!s.ConfigSyncEnabled || string.IsNullOrWhiteSpace(s.ConfigSyncPath))
            return ConfigSyncOutcome.Disabled;

        string json;
        try
        {
            json = await ReadWithTimeoutAsync(s.ConfigSyncPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Offline / missing / locked — keep the last-applied local config.
            _logger.LogWarning(ex, "Config source unreachable at {Path}", s.ConfigSyncPath);
            return ConfigSyncOutcome.Unreachable(ex.Message);
        }

        var signature = Sha256(json);
        if (!force && string.Equals(signature, s.ConfigSyncSignature, StringComparison.Ordinal))
            return ConfigSyncOutcome.Unchanged;

        var passphrase = await ResolveCredentialPassphraseAsync(ct).ConfigureAwait(false);
        var result = await _backup.MirrorAsync(json, passphrase, ct).ConfigureAwait(false);
        await _settings.UpdateAsync(_settings.Current with { ConfigSyncSignature = signature }, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Config mirrored from {Path}: +{Added} ~{Updated} -{Removed} cameras, {Layouts} layouts",
            s.ConfigSyncPath, result.CamerasAdded, result.CamerasUpdated, result.CamerasRemoved, result.LayoutsReplaced);
        return ConfigSyncOutcome.Applied(result);
    }

    private static async Task<string> ReadWithTimeoutAsync(string path, CancellationToken ct)
    {
        // WaitAsync bounds the wall-clock; a leaked background read on a wedged
        // share is acceptable (background thread, app proceeds).
        return await Task.Run(() => File.ReadAllText(path), ct)
            .WaitAsync(ReadTimeout, ct)
            .ConfigureAwait(false);
    }

    private static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}

// Result of a sync attempt, for surfacing status in Settings.
public sealed record ConfigSyncOutcome(ConfigSyncStatus Status, ConfigSyncResult? Result = null, string? Error = null)
{
    public static readonly ConfigSyncOutcome Disabled = new(ConfigSyncStatus.Disabled);
    public static readonly ConfigSyncOutcome Unchanged = new(ConfigSyncStatus.Unchanged);
    public static ConfigSyncOutcome Unreachable(string error) => new(ConfigSyncStatus.Unreachable, Error: error);
    public static ConfigSyncOutcome Applied(ConfigSyncResult result) => new(ConfigSyncStatus.Applied, result);
}

public enum ConfigSyncStatus
{
    Disabled,
    Unreachable,
    Unchanged,
    Applied,
}
