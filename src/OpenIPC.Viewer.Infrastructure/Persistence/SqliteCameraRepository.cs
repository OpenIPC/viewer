using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OpenIPC.Viewer.Core.Analytics;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

public sealed class SqliteCameraRepository : ICameraRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteCameraRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var rows = await conn.QueryAsync<CameraRow>(
            "SELECT * FROM Cameras ORDER BY SortOrder, Name COLLATE NOCASE;").ConfigureAwait(false);
        return rows.Select(MapRow).ToList();
    }

    public async Task<Camera?> GetAsync(CameraId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<CameraRow>(
            "SELECT * FROM Cameras WHERE Id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : MapRow(row);
    }

    public async Task<CameraId> AddAsync(Camera camera, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            """
            INSERT INTO Cameras (
                Id, GroupId, Name, Host, OnvifPort, HttpPort,
                RtspMainUri, RtspSubUri, UsernameRef, PasswordRef,
                OnvifEnabled, OnvifProfileToken, ChipModel, FirmwareVersion,
                IncludedInGrid, HasPtz, IsMajestic, SortOrder, CreatedAt, UpdatedAt,
                StreamQualityOverride, SshPort,
                AiEnabled, AiClasses, AiThreshold, AiFps, AiAutoRecord, AiPostEventSeconds,
                HasAudioIn, HasAudioOut)
            VALUES (
                @Id, @GroupId, @Name, @Host, @OnvifPort, @HttpPort,
                @RtspMainUri, @RtspSubUri, @UsernameRef, @PasswordRef,
                @OnvifEnabled, @OnvifProfileToken, @ChipModel, @FirmwareVersion,
                @IncludedInGrid, @HasPtz, @IsMajestic, @SortOrder, @CreatedAt, @UpdatedAt,
                @StreamQualityOverride, @SshPort,
                @AiEnabled, @AiClasses, @AiThreshold, @AiFps, @AiAutoRecord, @AiPostEventSeconds,
                @HasAudioIn, @HasAudioOut);
            """,
            ToRow(camera), transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return camera.Id;
    }

    public async Task UpdateAsync(Camera camera, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE Cameras SET
                GroupId            = @GroupId,
                Name               = @Name,
                Host               = @Host,
                OnvifPort          = @OnvifPort,
                HttpPort           = @HttpPort,
                RtspMainUri        = @RtspMainUri,
                RtspSubUri         = @RtspSubUri,
                UsernameRef        = @UsernameRef,
                PasswordRef        = @PasswordRef,
                OnvifEnabled       = @OnvifEnabled,
                OnvifProfileToken  = @OnvifProfileToken,
                ChipModel          = @ChipModel,
                FirmwareVersion    = @FirmwareVersion,
                IncludedInGrid     = @IncludedInGrid,
                HasPtz             = @HasPtz,
                IsMajestic         = @IsMajestic,
                SortOrder          = @SortOrder,
                UpdatedAt          = @UpdatedAt,
                StreamQualityOverride = @StreamQualityOverride,
                SshPort            = @SshPort,
                AiEnabled          = @AiEnabled,
                AiClasses          = @AiClasses,
                AiThreshold        = @AiThreshold,
                AiFps              = @AiFps,
                AiAutoRecord       = @AiAutoRecord,
                AiPostEventSeconds = @AiPostEventSeconds,
                HasAudioIn         = @HasAudioIn,
                HasAudioOut        = @HasAudioOut
            WHERE Id = @Id;
            """,
            ToRow(camera), transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (affected == 0)
            throw new InvalidOperationException($"Camera {camera.Id} not found");
    }

    public async Task UpdateSortOrdersAsync(IReadOnlyDictionary<CameraId, int> orders, CancellationToken ct)
    {
        if (orders.Count == 0) return;
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        foreach (var kv in orders)
        {
            await conn.ExecuteAsync(
                "UPDATE Cameras SET SortOrder = @order, UpdatedAt = @ts WHERE Id = @id;",
                new { order = kv.Value, ts, id = kv.Key.ToString() },
                transaction: tx).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(CameraId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "DELETE FROM Cameras WHERE Id = @id;",
            new { id = id.ToString() }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static Camera MapRow(CameraRow row) => new(
        Id: CameraId.Parse(row.Id),
        GroupId: row.GroupId is null ? null : new GroupId((int)row.GroupId.Value),
        Name: row.Name,
        Host: row.Host,
        OnvifPort: row.OnvifPort,
        HttpPort: row.HttpPort,
        RtspMainUri: new Uri(row.RtspMainUri, UriKind.Absolute),
        RtspSubUri: row.RtspSubUri is null ? null : new Uri(row.RtspSubUri, UriKind.Absolute),
        UsernameRef: row.UsernameRef,
        PasswordRef: row.PasswordRef,
        OnvifEnabled: row.OnvifEnabled != 0,
        OnvifProfileToken: row.OnvifProfileToken,
        ChipModel: row.ChipModel,
        FirmwareVersion: row.FirmwareVersion,
        IncludedInGrid: row.IncludedInGrid != 0,
        HasPtz: row.HasPtz != 0,
        IsMajestic: row.IsMajestic != 0,
        SortOrder: row.SortOrder,
        CreatedAt: DateTime.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt: DateTime.Parse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        StreamQualityOverride: (StreamQualityOverride)row.StreamQualityOverride,
        SshPort: row.SshPort is null ? null : (int)row.SshPort.Value,
        Analytics: new AnalyticsSettings(
            Enabled: row.AiEnabled != 0,
            ClassIds: ParseClassIds(row.AiClasses),
            ConfidenceThreshold: (float)row.AiThreshold,
            AnalyticsFps: row.AiFps,
            AutoRecord: row.AiAutoRecord != 0,
            PostEventSeconds: row.AiPostEventSeconds),
        HasAudioIn: row.HasAudioIn != 0,
        HasAudioOut: row.HasAudioOut != 0);

    private static object ToRow(Camera c) => new
    {
        Id = c.Id.ToString(),
        GroupId = c.GroupId?.Value,
        c.Name,
        c.Host,
        c.OnvifPort,
        c.HttpPort,
        RtspMainUri = c.RtspMainUri.ToString(),
        RtspSubUri = c.RtspSubUri?.ToString(),
        c.UsernameRef,
        c.PasswordRef,
        OnvifEnabled = c.OnvifEnabled ? 1 : 0,
        c.OnvifProfileToken,
        c.ChipModel,
        c.FirmwareVersion,
        IncludedInGrid = c.IncludedInGrid ? 1 : 0,
        HasPtz = c.HasPtz ? 1 : 0,
        IsMajestic = c.IsMajestic ? 1 : 0,
        c.SortOrder,
        CreatedAt = c.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        UpdatedAt = c.UpdatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        StreamQualityOverride = (int)c.StreamQualityOverride,
        c.SshPort,
        AiEnabled = c.AnalyticsOrDefault.Enabled ? 1 : 0,
        AiClasses = FormatClassIds(c.AnalyticsOrDefault.ClassIds),
        AiThreshold = c.AnalyticsOrDefault.ConfidenceThreshold,
        AiFps = c.AnalyticsOrDefault.AnalyticsFps,
        AiAutoRecord = c.AnalyticsOrDefault.AutoRecord ? 1 : 0,
        AiPostEventSeconds = c.AnalyticsOrDefault.PostEventSeconds,
        HasAudioIn = c.HasAudioIn ? 1 : 0,
        HasAudioOut = c.HasAudioOut ? 1 : 0,
    };

    // AiClasses is a CSV of COCO class ids; null/empty means "all classes".
    private static IReadOnlyCollection<int>? ParseClassIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var ids = new List<int>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    private static string? FormatClassIds(IReadOnlyCollection<int>? ids) =>
        ids is { Count: > 0 } ? string.Join(',', ids) : null;

    private sealed class CameraRow
    {
        public string Id { get; init; } = default!;
        public long? GroupId { get; init; }
        public string Name { get; init; } = default!;
        public string Host { get; init; } = default!;
        public int? OnvifPort { get; init; }
        public int HttpPort { get; init; }
        public string RtspMainUri { get; init; } = default!;
        public string? RtspSubUri { get; init; }
        public string? UsernameRef { get; init; }
        public string? PasswordRef { get; init; }
        public int OnvifEnabled { get; init; }
        public string? OnvifProfileToken { get; init; }
        public string? ChipModel { get; init; }
        public string? FirmwareVersion { get; init; }
        public int IncludedInGrid { get; init; }
        public int HasPtz { get; init; }
        public int IsMajestic { get; init; }
        public int SortOrder { get; init; }
        public string CreatedAt { get; init; } = default!;
        public string UpdatedAt { get; init; } = default!;
        public int StreamQualityOverride { get; init; }
        public long? SshPort { get; init; }
        public int AiEnabled { get; init; }
        public string? AiClasses { get; init; }
        public double AiThreshold { get; init; }
        public int AiFps { get; init; }
        public int AiAutoRecord { get; init; }
        public int AiPostEventSeconds { get; init; }
        public int HasAudioIn { get; init; }
        public int HasAudioOut { get; init; }
    }
}
