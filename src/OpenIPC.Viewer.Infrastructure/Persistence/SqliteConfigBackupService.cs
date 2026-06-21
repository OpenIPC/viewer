using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

// JSON config export/import (Phase 19.2). Cameras round-trip by GUID (upsert);
// layouts are appended with their tile membership. No secrets are ever written —
// the Camera entity holds none (passwords live in ISecretsStore).
public sealed class SqliteConfigBackupService : IConfigBackupService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICameraRepository _cameras;
    private readonly ILayoutRepository _layouts;

    public SqliteConfigBackupService(ICameraRepository cameras, ILayoutRepository layouts)
    {
        _cameras = cameras;
        _layouts = layouts;
    }

    public async Task<string> ExportAsync(CancellationToken ct)
    {
        var cameras = await _cameras.GetAllAsync(ct).ConfigureAwait(false);
        var layouts = await _layouts.GetAllAsync(ct).ConfigureAwait(false);

        var layoutDtos = new List<LayoutDto>(layouts.Count);
        foreach (var l in layouts)
        {
            var tiles = await _layouts.GetTilesAsync(l.Id, ct).ConfigureAwait(false);
            layoutDtos.Add(new LayoutDto(l.Name, l.GridSize, l.SortOrder, tiles.Select(t => t.Value.ToString()).ToList()));
        }

        var root = new BackupRoot(
            CurrentSchemaVersion,
            cameras.Select(ToDto).ToList(),
            layoutDtos);

        return JsonSerializer.Serialize(root, JsonOpts);
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

    private sealed record BackupRoot(int SchemaVersion, List<CameraDto> Cameras, List<LayoutDto> Layouts);

    private sealed record CameraDto(
        string Id, string Name, string Host, int? OnvifPort, int HttpPort,
        string RtspMainUri, string? RtspSubUri, string? UsernameRef, string? PasswordRef,
        bool OnvifEnabled, string? OnvifProfileToken, string? ChipModel, string? FirmwareVersion,
        bool IncludedInGrid, bool HasPtz, bool IsMajestic, int SortOrder,
        int StreamQualityOverride, int? SshPort, bool HasAudioIn, bool HasAudioOut);

    private sealed record LayoutDto(string Name, int GridSize, int SortOrder, List<string> Tiles);
}
