using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.Infrastructure.Persistence;

public sealed class SqliteLayoutRepository : ILayoutRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteLayoutRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<GridLayout>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var rows = await conn.QueryAsync<LayoutRow>(
            "SELECT Id, Name, GridSize, SortOrder FROM Layouts ORDER BY SortOrder, Id;")
            .ConfigureAwait(false);
        return rows.Select(r => new GridLayout(new LayoutId((int)r.Id), r.Name, r.GridSize, r.SortOrder)).ToList();
    }

    public async Task<LayoutId> AddAsync(string name, int gridSize, int sortOrder, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO Layouts (Name, GridSize, SortOrder) VALUES (@name, @gridSize, @sortOrder);
            SELECT last_insert_rowid();
            """,
            new { name, gridSize, sortOrder }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new LayoutId((int)id);
    }

    public async Task RenameAsync(LayoutId id, string name, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var affected = await conn.ExecuteAsync(
            "UPDATE Layouts SET Name = @name WHERE Id = @id;",
            new { id = id.Value, name }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (affected == 0) throw new InvalidOperationException($"Layout {id} not found");
    }

    public async Task SetGridSizeAsync(LayoutId id, int gridSize, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "UPDATE Layouts SET GridSize = @gridSize WHERE Id = @id;",
            new { id = id.Value, gridSize }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(LayoutId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM LayoutTiles WHERE LayoutId = @id;",
            new { id = id.Value }, transaction: tx).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM Layouts WHERE Id = @id;",
            new { id = id.Value }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ReorderAsync(IReadOnlyList<LayoutId> orderedIds, CancellationToken ct)
    {
        if (orderedIds.Count == 0) return;
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            await conn.ExecuteAsync(
                "UPDATE Layouts SET SortOrder = @order WHERE Id = @id;",
                new { order = i, id = orderedIds[i].Value }, transaction: tx).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CameraId>> GetTilesAsync(LayoutId id, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        var ids = await conn.QueryAsync<string>(
            """
            SELECT lt.CameraId FROM LayoutTiles lt
            JOIN Cameras c ON c.Id = lt.CameraId
            WHERE lt.LayoutId = @id
            ORDER BY lt.Position;
            """,
            new { id = id.Value }).ConfigureAwait(false);
        return ids.Select(CameraId.Parse).ToList();
    }

    public async Task SetTilesAsync(LayoutId id, IReadOnlyList<CameraId> cameraIds, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM LayoutTiles WHERE LayoutId = @id;",
            new { id = id.Value }, transaction: tx).ConfigureAwait(false);
        for (var i = 0; i < cameraIds.Count; i++)
        {
            await conn.ExecuteAsync(
                "INSERT INTO LayoutTiles (LayoutId, CameraId, Position) VALUES (@id, @cam, @pos);",
                new { id = id.Value, cam = cameraIds[i].ToString(), pos = i }, transaction: tx).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task AddTileAsync(LayoutId id, CameraId cameraId, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var nextPos = await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(Position), -1) + 1 FROM LayoutTiles WHERE LayoutId = @id;",
            new { id = id.Value }, transaction: tx).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO LayoutTiles (LayoutId, CameraId, Position) VALUES (@id, @cam, @pos);",
            new { id = id.Value, cam = cameraId.ToString(), pos = nextPos }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveTileAsync(LayoutId id, CameraId cameraId, CancellationToken ct)
    {
        await using var conn = _factory.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "DELETE FROM LayoutTiles WHERE LayoutId = @id AND CameraId = @cam;",
            new { id = id.Value, cam = cameraId.ToString() }, transaction: tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private sealed class LayoutRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = default!;
        public int GridSize { get; init; }
        public int SortOrder { get; init; }
    }
}
