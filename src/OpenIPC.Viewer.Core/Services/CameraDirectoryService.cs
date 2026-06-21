using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Core.Services;

public sealed class CameraDirectoryService : ICameraCredentialsProvider
{
    private readonly ICameraRepository _cameras;
    private readonly IGroupRepository _groups;
    private readonly ISecretsStore _secrets;
    private readonly Settings.IUserSettingsAccessor? _settings;
    private readonly ILayoutRepository? _layouts;

    public CameraDirectoryService(
        ICameraRepository cameras,
        IGroupRepository groups,
        ISecretsStore secrets,
        Settings.IUserSettingsAccessor? settings = null,
        ILayoutRepository? layouts = null)
    {
        _cameras = cameras;
        _groups = groups;
        _secrets = secrets;
        _settings = settings;
        _layouts = layouts;
    }

    public Task<IReadOnlyList<Camera>> ListAsync(CancellationToken ct) =>
        _cameras.GetAllAsync(ct);

    public Task<Camera?> GetAsync(CameraId id, CancellationToken ct) =>
        _cameras.GetAsync(id, ct);

    // Group ops are thin pass-throughs — the repo already enforces
    // FK cascade on delete, so removing a group nulls cameras' GroupId
    // automatically (well, doesn't — schema has REFERENCES Groups(Id)
    // without ON DELETE; we let SQLite reject the delete and surface the
    // exception to the UI for confirmation flow).
    public Task<IReadOnlyList<CameraGroup>> ListGroupsAsync(CancellationToken ct) =>
        _groups.GetAllAsync(ct);

    public Task<GroupId> AddGroupAsync(string name, CancellationToken ct) =>
        _groups.AddAsync(name, sortOrder: 0, ct);

    public Task RenameGroupAsync(GroupId id, string name, CancellationToken ct) =>
        _groups.RenameAsync(id, name, ct);

    public Task RemoveGroupAsync(GroupId id, CancellationToken ct) =>
        _groups.RemoveAsync(id, ct);

    public async Task<CameraId> AddAsync(NewCameraRequest req, CancellationToken ct)
    {
        var id = CameraId.New();
        var (usernameRef, passwordRef) = await StoreCredentialsAsync(id, req.Credentials, ct).ConfigureAwait(false);
        await StoreSshCredentialsAsync(id, req.SshCredentials, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var camera = new Camera(
            Id: id,
            GroupId: req.GroupId,
            Name: req.Name,
            Host: req.Host,
            OnvifPort: req.OnvifPort,
            HttpPort: req.HttpPort,
            RtspMainUri: req.RtspMainUri,
            RtspSubUri: req.RtspSubUri,
            UsernameRef: usernameRef,
            PasswordRef: passwordRef,
            OnvifEnabled: false,
            OnvifProfileToken: null,
            ChipModel: null,
            FirmwareVersion: null,
            IncludedInGrid: true,
            HasPtz: false,
            IsMajestic: false,
            SortOrder: 0,
            CreatedAt: now,
            UpdatedAt: now,
            StreamQualityOverride: req.StreamQualityOverride,
            SshPort: req.SshPort,
            Analytics: req.Analytics);

        return await _cameras.AddAsync(camera, ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(CameraId id, UpdateCameraRequest req, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");

        var (usernameRef, passwordRef) = req.Credentials is null
            ? (existing.UsernameRef, existing.PasswordRef)
            : await StoreCredentialsAsync(id, req.Credentials, ct).ConfigureAwait(false);

        // Null SshCredentials = keep what's stored (mirrors main-credential handling).
        if (req.SshCredentials is not null)
            await StoreSshCredentialsAsync(id, req.SshCredentials, ct).ConfigureAwait(false);

        var updated = existing with
        {
            Name = req.Name,
            Host = req.Host,
            HttpPort = req.HttpPort,
            OnvifPort = req.OnvifPort,
            RtspMainUri = req.RtspMainUri,
            RtspSubUri = req.RtspSubUri,
            UsernameRef = usernameRef,
            PasswordRef = passwordRef,
            GroupId = req.GroupId,
            StreamQualityOverride = req.StreamQualityOverride,
            SshPort = req.SshPort,
            // Null keeps the stored analytics config (mirrors credentials).
            Analytics = req.Analytics ?? existing.Analytics,
            UpdatedAt = DateTime.UtcNow,
        };

        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates only a camera's analytics config (Phase 15) — used by the AI
    /// control center / tile toggles without touching credentials or geometry.
    /// </summary>
    public async Task SetAnalyticsAsync(CameraId id, AnalyticsSettings analytics, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");
        var updated = existing with { Analytics = analytics, UpdatedAt = DateTime.UtcNow };
        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public Task UpdateSortOrdersAsync(IReadOnlyDictionary<CameraId, int> orders, CancellationToken ct) =>
        _cameras.UpdateSortOrdersAsync(orders, ct);

    public async Task SetIncludedInGridAsync(CameraId id, bool included, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");
        var updated = existing with { IncludedInGrid = included, UpdatedAt = DateTime.UtcNow };
        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);

        // Tabbed layouts (Phase 19.1): the library checkbox adds/removes the
        // camera from the active layout, so it's the populate path for any tab.
        if (_layouts is not null)
        {
            var layoutId = await ResolveActiveLayoutAsync(ct).ConfigureAwait(false);
            if (layoutId is { } lid)
            {
                if (included) await _layouts.AddTileAsync(lid, id, ct).ConfigureAwait(false);
                else await _layouts.RemoveTileAsync(lid, id, ct).ConfigureAwait(false);
            }
        }
    }

    // CameraIds in the active layout — the library uses this to seed the "in
    // grid" checkbox so it matches the tab the grid is showing (Phase 19.1).
    public async Task<IReadOnlyList<CameraId>> GetActiveLayoutCameraIdsAsync(CancellationToken ct)
    {
        if (_layouts is null) return Array.Empty<CameraId>();
        var layout = await ResolveActiveLayoutAsync(ct).ConfigureAwait(false);
        if (layout is not { } lid) return Array.Empty<CameraId>();
        return await _layouts.GetTilesAsync(lid, ct).ConfigureAwait(false);
    }

    // The active layout (UserSettings.ActiveLayoutId), falling back to the first
    // one. Null only when there are no layouts / no layout repo wired.
    private async Task<LayoutId?> ResolveActiveLayoutAsync(CancellationToken ct)
    {
        if (_layouts is null) return null;
        var all = await _layouts.GetAllAsync(ct).ConfigureAwait(false);
        if (all.Count == 0) return null;
        var activeId = _settings?.ActiveLayoutId ?? 0;
        var match = all.FirstOrDefault(l => l.Id.Value == activeId) ?? all[0];
        return match.Id;
    }

    public async Task SetIsMajesticAsync(CameraId id, bool value, CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");
        if (existing.IsMajestic == value) return;
        var updated = existing with { IsMajestic = value, UpdatedAt = DateTime.UtcNow };
        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task SaveOnvifMetadataAsync(
        CameraId id,
        Onvif.OnvifProbeResult probe,
        CancellationToken ct)
    {
        var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Camera {id} not found");

        var chipModel = probe.Manufacturer is not null && probe.Model is not null
            ? $"{probe.Manufacturer} {probe.Model}".Trim()
            : probe.Model ?? probe.Manufacturer;

        var updated = existing with
        {
            OnvifEnabled = true,
            OnvifProfileToken = probe.ProfileToken,
            HasPtz = probe.HasPtz,
            HasAudioIn = probe.HasAudioIn,
            HasAudioOut = probe.HasAudioOut,
            ChipModel = chipModel ?? existing.ChipModel,
            FirmwareVersion = probe.FirmwareVersion ?? existing.FirmwareVersion,
            UpdatedAt = DateTime.UtcNow,
        };
        await _cameras.UpdateAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(CameraId id, CancellationToken ct)
    {
        await _secrets.RemoveAsync(UsernameKey(id), ct).ConfigureAwait(false);
        await _secrets.RemoveAsync(PasswordKey(id), ct).ConfigureAwait(false);
        await _secrets.RemoveAsync(SshUsernameKey(id), ct).ConfigureAwait(false);
        await _secrets.RemoveAsync(SshPasswordKey(id), ct).ConfigureAwait(false);
        await _cameras.RemoveAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<CameraCredentials?> GetCredentialsAsync(CameraId id, CancellationToken ct)
    {
        var username = await _secrets.GetAsync(UsernameKey(id), ct).ConfigureAwait(false);
        var password = await _secrets.GetAsync(PasswordKey(id), ct).ConfigureAwait(false);
        if (username is null || password is null)
            return null;
        return new CameraCredentials(username, password);
    }

    /// <summary>
    /// SSH-specific credentials, or null if none are stored. Does NOT fall back
    /// to the main login — the editor uses this to decide whether to pre-fill
    /// the SSH fields (blank = "reuse main login").
    /// </summary>
    public async Task<CameraCredentials?> GetSshCredentialsAsync(CameraId id, CancellationToken ct)
    {
        var username = await _secrets.GetAsync(SshUsernameKey(id), ct).ConfigureAwait(false);
        var password = await _secrets.GetAsync(SshPasswordKey(id), ct).ConfigureAwait(false);
        return username is not null && password is not null
            ? new CameraCredentials(username, password)
            : null;
    }

    /// <summary>
    /// Builds the SSH connection target for a camera (Phase 13): SSH-specific
    /// credentials if set, otherwise the main RTSP/ONVIF login (OpenIPC root
    /// often matches). Returns null when no credentials exist at all — the
    /// caller prompts rather than attempting an anonymous login.
    /// </summary>
    public async Task<Ssh.SshEndpoint?> GetSshEndpointAsync(Camera camera, CancellationToken ct)
    {
        var creds = await GetSshCredentialsAsync(camera.Id, ct).ConfigureAwait(false)
                    ?? await GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
        if (creds is null)
            return null;
        // Per-camera port wins; otherwise the global default from settings, then 22.
        var port = camera.SshPort ?? _settings?.SshDefaultPort ?? Ssh.SshEndpoint.DefaultPort;
        return new Ssh.SshEndpoint(
            camera.Host, port, creds.Username, new Ssh.SshAuth.Password(creds.Password));
    }

    private async Task StoreSshCredentialsAsync(CameraId id, CameraCredentials? credentials, CancellationToken ct)
    {
        if (credentials is null)
        {
            await _secrets.RemoveAsync(SshUsernameKey(id), ct).ConfigureAwait(false);
            await _secrets.RemoveAsync(SshPasswordKey(id), ct).ConfigureAwait(false);
            return;
        }
        await _secrets.SetAsync(SshUsernameKey(id), credentials.Username, ct).ConfigureAwait(false);
        await _secrets.SetAsync(SshPasswordKey(id), credentials.Password, ct).ConfigureAwait(false);
    }

    private async Task<(string? UsernameRef, string? PasswordRef)> StoreCredentialsAsync(
        CameraId id, CameraCredentials? credentials, CancellationToken ct)
    {
        if (credentials is null)
        {
            await _secrets.RemoveAsync(UsernameKey(id), ct).ConfigureAwait(false);
            await _secrets.RemoveAsync(PasswordKey(id), ct).ConfigureAwait(false);
            return (null, null);
        }

        var usernameKey = UsernameKey(id);
        var passwordKey = PasswordKey(id);
        await _secrets.SetAsync(usernameKey, credentials.Username, ct).ConfigureAwait(false);
        await _secrets.SetAsync(passwordKey, credentials.Password, ct).ConfigureAwait(false);
        return (usernameKey, passwordKey);
    }

    private static string UsernameKey(CameraId id) => $"cam:{id}:username";
    private static string PasswordKey(CameraId id) => $"cam:{id}:password";
    private static string SshUsernameKey(CameraId id) => $"cam:{id}:ssh:username";
    private static string SshPasswordKey(CameraId id) => $"cam:{id}:ssh:password";
}
