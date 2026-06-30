using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

// JSON config export/import (Phase 19.2). Cameras round-trip by GUID (upsert);
// layouts are appended with their tile membership. Passwords are excluded by
// default — they live in ISecretsStore — but the network-sync flow (Phase 20)
// can opt in to carry them ENCRYPTED with a fleet passphrase (see CredentialCipher).
public sealed class SqliteConfigBackupService : IConfigBackupService
{
    // v2 adds the optional encrypted-credentials fields (CredsSalt + per-camera
    // EncCreds). A v1 file still imports — those fields are simply absent.
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICameraRepository _cameras;
    private readonly ILayoutRepository _layouts;
    private readonly ISecretsStore _secrets;

    public SqliteConfigBackupService(ICameraRepository cameras, ILayoutRepository layouts, ISecretsStore secrets)
    {
        _cameras = cameras;
        _layouts = layouts;
        _secrets = secrets;
    }

    public async Task<string> ExportAsync(string? credentialPassphrase, CancellationToken ct)
    {
        var cameras = await _cameras.GetAllAsync(ct).ConfigureAwait(false);
        var layouts = await _layouts.GetAllAsync(ct).ConfigureAwait(false);

        var layoutDtos = new List<LayoutDto>(layouts.Count);
        foreach (var l in layouts)
        {
            var tiles = await _layouts.GetTilesAsync(l.Id, ct).ConfigureAwait(false);
            layoutDtos.Add(new LayoutDto(l.Name, l.GridSize, l.SortOrder, tiles.Select(t => t.Value.ToString()).ToList()));
        }

        // Optional encrypted credentials. One salt per file; each camera's
        // {username,password} is encrypted into its EncCreds.
        var includeCreds = !string.IsNullOrEmpty(credentialPassphrase);
        byte[]? salt = includeCreds ? CredentialCipher.NewSalt() : null;

        var cameraDtos = new List<CameraDto>(cameras.Count);
        foreach (var c in cameras)
        {
            string? encCreds = null;
            if (includeCreds && salt is not null)
                encCreds = await EncryptCredsAsync(c, credentialPassphrase!, salt, ct).ConfigureAwait(false);
            cameraDtos.Add(ToDto(c) with { EncCreds = encCreds });
        }

        var root = new BackupRoot(
            CurrentSchemaVersion,
            cameraDtos,
            layoutDtos,
            salt is null ? null : Convert.ToBase64String(salt));

        return JsonSerializer.Serialize(root, JsonOpts);
    }

    private async Task<string?> EncryptCredsAsync(Camera c, string passphrase, byte[] salt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.UsernameRef) || string.IsNullOrEmpty(c.PasswordRef))
            return null;
        var user = await _secrets.GetAsync(c.UsernameRef, ct).ConfigureAwait(false);
        var pass = await _secrets.GetAsync(c.PasswordRef, ct).ConfigureAwait(false);
        if (user is null && pass is null) return null;
        var payload = JsonSerializer.Serialize(new CredsPayload(user ?? "", pass ?? ""), JsonOpts);
        return CredentialCipher.Encrypt(payload, passphrase, salt);
    }

    // Decrypt a camera's credentials from the file and store them under the
    // camera's secret refs (cam:{id}:username/password) so the local secrets
    // store now resolves them. A wrong passphrase / tampered blob decrypts to
    // null and is skipped — the camera just stays credential-less locally.
    private async Task RestoreCredsAsync(Camera camera, string encCreds, string passphrase, byte[] salt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(camera.UsernameRef) || string.IsNullOrEmpty(camera.PasswordRef))
            return;
        var plain = CredentialCipher.TryDecrypt(encCreds, passphrase, salt);
        if (plain is null) return;
        CredsPayload? creds;
        try { creds = JsonSerializer.Deserialize<CredsPayload>(plain, JsonOpts); }
        catch (JsonException) { return; }
        if (creds is null) return;
        await _secrets.SetAsync(camera.UsernameRef, creds.U, ct).ConfigureAwait(false);
        await _secrets.SetAsync(camera.PasswordRef, creds.P, ct).ConfigureAwait(false);
    }

    public async Task<ConfigImportPreview> PreviewAsync(string json, CancellationToken ct)
    {
        var root = Parse(json);
        var existing = (await _cameras.GetAllAsync(ct).ConfigureAwait(false))
            .Select(c => c.Id).ToHashSet();

        var added = 0;
        var updated = 0;
        foreach (var dto in root.Cameras)
        {
            if (!Guid.TryParse(dto.Id, out var g)) continue;
            if (existing.Contains(new CameraId(g))) updated++;
            else added++;
        }
        return new ConfigImportPreview(added, updated, root.Layouts.Count);
    }

    public async Task<ConfigImportPreview> ImportAsync(string json, CancellationToken ct)
    {
        var root = Parse(json);
        var added = 0;
        var updated = 0;

        foreach (var dto in root.Cameras)
        {
            if (!Guid.TryParse(dto.Id, out var g)) continue;
            var id = new CameraId(g);
            var camera = FromDto(dto, id);
            var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await _cameras.AddAsync(camera, ct).ConfigureAwait(false);
                added++;
            }
            else
            {
                await _cameras.UpdateAsync(camera with { CreatedAt = existing.CreatedAt }, ct).ConfigureAwait(false);
                updated++;
            }
        }

        // Only keep tiles for cameras that now exist (avoid orphan rows).
        var present = (await _cameras.GetAllAsync(ct).ConfigureAwait(false)).Select(c => c.Id).ToHashSet();
        foreach (var l in root.Layouts)
        {
            var newId = await _layouts.AddAsync(l.Name, l.GridSize, l.SortOrder, ct).ConfigureAwait(false);
            var tileIds = l.Tiles
                .Select(s => Guid.TryParse(s, out var g) ? new CameraId(g) : (CameraId?)null)
                .Where(c => c is { } cid && present.Contains(cid))
                .Select(c => c!.Value)
                .ToList();
            if (tileIds.Count > 0)
                await _layouts.SetTilesAsync(newId, tileIds, ct).ConfigureAwait(false);
        }

        return new ConfigImportPreview(added, updated, root.Layouts.Count);
    }

    public async Task<ConfigSyncResult> MirrorAsync(string json, string? credentialPassphrase, CancellationToken ct)
    {
        var root = Parse(json);
        var added = 0;
        var updated = 0;
        var removed = 0;

        // Decrypt credentials only if the caller supplied a passphrase AND the
        // file carries a salt (i.e. it was exported with credentials).
        byte[]? salt = null;
        if (!string.IsNullOrEmpty(credentialPassphrase) && !string.IsNullOrEmpty(root.CredsSalt))
        {
            try { salt = Convert.FromBase64String(root.CredsSalt); }
            catch (FormatException) { salt = null; }
        }

        // Upsert every camera the file declares; track which ids it covers.
        var fileIds = new HashSet<CameraId>();
        foreach (var dto in root.Cameras)
        {
            if (!Guid.TryParse(dto.Id, out var g)) continue;
            var id = new CameraId(g);
            fileIds.Add(id);
            var camera = FromDto(dto, id);
            var existing = await _cameras.GetAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await _cameras.AddAsync(camera, ct).ConfigureAwait(false);
                added++;
            }
            else
            {
                await _cameras.UpdateAsync(camera with { CreatedAt = existing.CreatedAt }, ct).ConfigureAwait(false);
                updated++;
            }

            if (salt is not null && !string.IsNullOrEmpty(dto.EncCreds))
                await RestoreCredsAsync(camera, dto.EncCreds!, credentialPassphrase!, salt, ct).ConfigureAwait(false);
        }

        // Authoritative: drop local cameras the file omits. Wipe-guard — a file
        // with no cameras at all is almost certainly truncated/corrupt, so we
        // skip deletion rather than empty the user's library.
        if (fileIds.Count > 0)
        {
            foreach (var existing in await _cameras.GetAllAsync(ct).ConfigureAwait(false))
            {
                if (!fileIds.Contains(existing.Id))
                {
                    await _cameras.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
                    removed++;
                }
            }
        }

        // Replace layouts wholesale — but only when the file actually carries
        // some, so an empty layout set doesn't leave the grid with no tabs.
        if (root.Layouts.Count > 0)
        {
            foreach (var l in await _layouts.GetAllAsync(ct).ConfigureAwait(false))
                await _layouts.RemoveAsync(l.Id, ct).ConfigureAwait(false);

            var present = (await _cameras.GetAllAsync(ct).ConfigureAwait(false)).Select(c => c.Id).ToHashSet();
            foreach (var l in root.Layouts)
            {
                var newId = await _layouts.AddAsync(l.Name, l.GridSize, l.SortOrder, ct).ConfigureAwait(false);
                var tileIds = l.Tiles
                    .Select(s => Guid.TryParse(s, out var g) ? new CameraId(g) : (CameraId?)null)
                    .Where(c => c is { } cid && present.Contains(cid))
                    .Select(c => c!.Value)
                    .ToList();
                if (tileIds.Count > 0)
                    await _layouts.SetTilesAsync(newId, tileIds, ct).ConfigureAwait(false);
            }
        }

        return new ConfigSyncResult(added, updated, removed, root.Layouts.Count);
    }

    private static BackupRoot Parse(string json)
    {
        BackupRoot? root;
        try { root = JsonSerializer.Deserialize<BackupRoot>(json, JsonOpts); }
        catch (JsonException ex) { throw new ArgumentException("Invalid backup JSON: " + ex.Message, nameof(json), ex); }
        if (root is null) throw new ArgumentException("Empty backup", nameof(json));
        if (root.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported backup schema version {root.SchemaVersion}");
        return root with { Cameras = root.Cameras ?? new(), Layouts = root.Layouts ?? new() };
    }

    private static CameraDto ToDto(Camera c) => new(
        c.Id.Value.ToString(), c.Name, c.Host, c.OnvifPort, c.HttpPort,
        c.RtspMainUri.ToString(), c.RtspSubUri?.ToString(),
        c.UsernameRef, c.PasswordRef, c.OnvifEnabled, c.OnvifProfileToken,
        c.ChipModel, c.FirmwareVersion, c.IncludedInGrid, c.HasPtz, c.IsMajestic,
        c.SortOrder, (int)c.StreamQualityOverride, c.SshPort, c.HasAudioIn, c.HasAudioOut);

    private static Camera FromDto(CameraDto d, CameraId id) => new(
        Id: id,
        GroupId: null, // group int-ids aren't portable; dropped on import (19.2 v1)
        Name: d.Name,
        Host: d.Host,
        OnvifPort: d.OnvifPort,
        HttpPort: d.HttpPort,
        RtspMainUri: new Uri(d.RtspMainUri, UriKind.Absolute),
        RtspSubUri: string.IsNullOrEmpty(d.RtspSubUri) ? null : new Uri(d.RtspSubUri, UriKind.Absolute),
        UsernameRef: d.UsernameRef,
        PasswordRef: d.PasswordRef,
        OnvifEnabled: d.OnvifEnabled,
        OnvifProfileToken: d.OnvifProfileToken,
        ChipModel: d.ChipModel,
        FirmwareVersion: d.FirmwareVersion,
        IncludedInGrid: d.IncludedInGrid,
        HasPtz: d.HasPtz,
        IsMajestic: d.IsMajestic,
        SortOrder: d.SortOrder,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        StreamQualityOverride: (StreamQualityOverride)d.StreamQualityOverride,
        SshPort: d.SshPort,
        Analytics: null,
        HasAudioIn: d.HasAudioIn,
        HasAudioOut: d.HasAudioOut);

    private sealed record BackupRoot(
        int SchemaVersion, List<CameraDto> Cameras, List<LayoutDto> Layouts, string? CredsSalt = null);

    private sealed record CameraDto(
        string Id, string Name, string Host, int? OnvifPort, int HttpPort,
        string RtspMainUri, string? RtspSubUri, string? UsernameRef, string? PasswordRef,
        bool OnvifEnabled, string? OnvifProfileToken, string? ChipModel, string? FirmwareVersion,
        bool IncludedInGrid, bool HasPtz, bool IsMajestic, int SortOrder,
        int StreamQualityOverride, int? SshPort, bool HasAudioIn, bool HasAudioOut,
        string? EncCreds = null);

    private sealed record LayoutDto(string Name, int GridSize, int SortOrder, List<string> Tiles);

    // The encrypted credential payload (AES-GCM plaintext). Short field names keep
    // the (already encrypted) blob compact.
    private sealed record CredsPayload(string U, string P);
}
