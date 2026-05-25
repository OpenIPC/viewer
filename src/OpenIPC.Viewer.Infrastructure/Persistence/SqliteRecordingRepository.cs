using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

public sealed class SqliteRecordingRepository : IRecordingRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteRecordingRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Recording>> ListAsync(CameraId? cameraId, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        IEnumerable<Row> rows;
        if (cameraId is { } cid)
        {
            rows = await conn.QueryAsync<Row>(
                "SELECT * FROM Recordings WHERE CameraId = @cid ORDER BY StartedAt DESC;",
                new { cid = cid.ToString() }).ConfigureAwait(false);
        }
        else
        {
            rows = await conn.QueryAsync<Row>(
                "SELECT * FROM Recordings ORDER BY StartedAt DESC;").ConfigureAwait(false);
        }
        return rows.Select(Map).ToList();
    }

    public async Task AddAsync(Recording recording, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO Recordings (Id, CameraId, FilePath, StartedAt, EndedAt, SizeBytes, Codec, HasMotion)
            VALUES (@Id, @CameraId, @FilePath, @StartedAt, @EndedAt, @SizeBytes, @Codec, @HasMotion);
            """,
            ToRow(recording)).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Recording recording, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await conn.ExecuteAsync(
            """
            UPDATE Recordings SET
                EndedAt   = @EndedAt,
                SizeBytes = @SizeBytes,
                Codec     = @Codec,
                HasMotion = @HasMotion
            WHERE Id = @Id;
            """,
            ToRow(recording)).ConfigureAwait(false);
    }

    public async Task<Recording?> GetByPathAsync(string filePath, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            "SELECT * FROM Recordings WHERE FilePath = @p;",
            new { p = filePath }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task RemoveAsync(RecordingId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM Recordings WHERE Id = @id;",
            new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static Recording Map(Row r) => new(
        Id: RecordingId.Parse(r.Id),
        CameraId: CameraId.Parse(r.CameraId),
        FilePath: r.FilePath,
        StartedAt: DateTime.Parse(r.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt: r.EndedAt is null ? null : DateTime.Parse(r.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SizeBytes: r.SizeBytes,
        Codec: r.Codec,
        HasMotion: r.HasMotion != 0);

    private static object ToRow(Recording r) => new
    {
        Id = r.Id.ToString(),
        CameraId = r.CameraId.ToString(),
        r.FilePath,
        StartedAt = r.StartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        EndedAt = r.EndedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        r.SizeBytes,
        r.Codec,
        HasMotion = r.HasMotion ? 1 : 0,
    };

    private sealed class Row
    {
        public string Id { get; init; } = default!;
        public string CameraId { get; init; } = default!;
        public string FilePath { get; init; } = default!;
        public string StartedAt { get; init; } = default!;
        public string? EndedAt { get; init; }
        public long SizeBytes { get; init; }
        public string? Codec { get; init; }
        public int HasMotion { get; init; }
    }
}
