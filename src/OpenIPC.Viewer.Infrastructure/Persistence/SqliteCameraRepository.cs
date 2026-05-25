using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;

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
                IncludedInGrid, HasPtz, SortOrder, CreatedAt, UpdatedAt)
            VALUES (
                @Id, @GroupId, @Name, @Host, @OnvifPort, @HttpPort,
                @RtspMainUri, @RtspSubUri, @UsernameRef, @PasswordRef,
                @OnvifEnabled, @OnvifProfileToken, @ChipModel, @FirmwareVersion,
                @IncludedInGrid, @HasPtz, @SortOrder, @CreatedAt, @UpdatedAt);
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
                SortOrder          = @SortOrder,
                UpdatedAt          = @UpdatedAt
            WHERE Id = @Id;
            """,
            ToRow(camera), transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (affected == 0)
            throw new InvalidOperationException($"Camera {camera.Id} not found");
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
        SortOrder: row.SortOrder,
        CreatedAt: DateTime.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt: DateTime.Parse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

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
        c.SortOrder,
        CreatedAt = c.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        UpdatedAt = c.UpdatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
    };

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
        public int SortOrder { get; init; }
        public string CreatedAt { get; init; } = default!;
        public string UpdatedAt { get; init; } = default!;
    }
}
