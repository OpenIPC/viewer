using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

public sealed class SqliteSnapshotRepository : ISnapshotRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteSnapshotRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Snapshot>> ListAsync(
        CameraId? cameraId, DateTime? sinceUtc, DateTime? untilUtc, int limit, CancellationToken ct)
    {
        var sql = new StringBuilder("SELECT * FROM Snapshots WHERE 1=1");
        var p = new DynamicParameters();
        if (cameraId is { } cid)
        {
            sql.Append(" AND CameraId = @cid");
            p.Add("cid", cid.ToString());
        }
        if (sinceUtc is { } s)
        {
            sql.Append(" AND TakenAt >= @since");
            p.Add("since", s.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }
        if (untilUtc is { } u)
        {
            sql.Append(" AND TakenAt < @until");
            p.Add("until", u.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }
        sql.Append(" ORDER BY TakenAt DESC LIMIT @limit;");
        p.Add("limit", limit);

        await using var conn = _factory.OpenConnection();
        var rows = await conn.QueryAsync<Row>(sql.ToString(), p).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task AddAsync(Snapshot snapshot, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO Snapshots (Id, CameraId, TakenAt, Path, ThumbPath, Width, Height, Source, Kind)
            VALUES (@Id, @CameraId, @TakenAt, @Path, @ThumbPath, @Width, @Height, @Source, @Kind);
            """,
            ToRow(snapshot)).ConfigureAwait(false);
    }

    public async Task<Snapshot?> GetAsync(SnapshotId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            "SELECT * FROM Snapshots WHERE Id = @id;",
            new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task RemoveAsync(SnapshotId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM Snapshots WHERE Id = @id;",
            new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static Snapshot Map(Row r) => new(
        Id: SnapshotId.Parse(r.Id),
        CameraId: CameraId.Parse(r.CameraId),
        TakenAt: DateTime.Parse(r.TakenAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Path: r.Path,
        ThumbPath: r.ThumbPath,
        Width: r.Width,
        Height: r.Height,
        Source: (SnapshotSource)r.Source,
        Kind: (SnapshotKind)r.Kind);

    private static object ToRow(Snapshot s) => new
    {
        Id = s.Id.ToString(),
        CameraId = s.CameraId.ToString(),
        TakenAt = s.TakenAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        s.Path,
        s.ThumbPath,
        s.Width,
        s.Height,
        Source = (int)s.Source,
        Kind = (int)s.Kind,
    };

    private sealed class Row
    {
        public string Id { get; init; } = default!;
        public string CameraId { get; init; } = default!;
        public string TakenAt { get; init; } = default!;
        public string Path { get; init; } = default!;
        public string? ThumbPath { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Source { get; init; }
        public int Kind { get; init; }
    }
}
