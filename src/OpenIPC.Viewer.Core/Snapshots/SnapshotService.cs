using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Phase 14.1 — the one place that captures a still. Centralises the HD-source
/// priority so the grid tile, the single-camera page and any future caller all
/// behave identically and never persist an SD frame when an HD source exists.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    // Gallery thumbnails: longest side. Big enough to look crisp on a HiDPI
    // tile, small enough that thousands decode cheaply.
    private const int ThumbMaxDim = 320;

    private static readonly TimeSpan MajesticTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshOpenFrameTimeout = TimeSpan.FromSeconds(8);

    private readonly IMajesticClient _majestic;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly ICameraCredentialsProvider _credentials;
    private readonly ISnapshotRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IThumbnailGenerator _thumbs;
    private readonly IImageEditor _editor;

    public SnapshotService(
        IMajesticClient majestic,
        LiveStreamCoordinator coordinator,
        ICameraCredentialsProvider credentials,
        ISnapshotRepository repo,
        IFileSystem fs,
        IThumbnailGenerator thumbs,
        IImageEditor editor)
    {
        _majestic = majestic;
        _coordinator = coordinator;
        _credentials = credentials;
        _repo = repo;
        _fs = fs;
        _thumbs = thumbs;
        _editor = editor;
    }

    public async Task<Snapshot> SaveEditAsync(Snapshot source, SnapshotEdit edit, CancellationToken ct)
    {
        var id = SnapshotId.New();
        var dir = Path.GetDirectoryName(source.Path) ?? _fs.SnapshotsDir.FullName;
        var stem = Path.GetFileNameWithoutExtension(source.Path);
        var outPath = EnsureUnique(Path.Combine(dir, stem + "_edited.jpg"));

        var size = await _editor.RenderAsync(source.Path, edit, outPath, ct).ConfigureAwait(false);

        var thumbDir = Path.Combine(_fs.SnapshotsDir.FullName, ".thumbs");
        Directory.CreateDirectory(thumbDir);
        var thumbPath = Path.Combine(thumbDir, id.ToString() + ".jpg");
        string? savedThumb = thumbPath;
        try
        {
            var bytes = await ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            await _thumbs.GenerateAsync(bytes, thumbPath, ThumbMaxDim, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            savedThumb = null;
        }

        var snapshot = new Snapshot(
            id, source.CameraId, DateTime.UtcNow, outPath, savedThumb,
            size.Width, size.Height, SnapshotSource.Edited, SnapshotKind.Manual);
        await _repo.AddAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<Snapshot> CaptureAsync(
        Camera camera, IVideoSession? liveSession, StreamQuality? liveQuality, CancellationToken ct)
    {
        var (jpeg, source) = await GrabAsync(camera, liveSession, liveQuality, ct).ConfigureAwait(false);
        return await SaveAsync(camera, jpeg, source, SnapshotKind.Manual, ct).ConfigureAwait(false);
    }

    private async Task<(byte[] Jpeg, SnapshotSource Source)> GrabAsync(
        Camera camera, IVideoSession? liveSession, StreamQuality? liveQuality, CancellationToken ct)
    {
        // 1. Already-running mainstream — grab the last decoded frame for free.
        if (liveSession is not null && liveQuality == StreamQuality.Main)
        {
            var b = await TryGrabAsync(liveSession, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.LiveMain);
        }

        // 2. Majestic HTTP /image.jpg — always full-resolution, ~50–100 ms, and
        //    far cheaper than opening a fresh RTSP session.
        if (camera.IsMajestic)
        {
            try
            {
                var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
                var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(MajesticTimeout);
                var b = await _majestic.SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token)
                    .ConfigureAwait(false);
                if (b.Length > 0) return (b, SnapshotSource.HttpSnapshot);
            }
            catch (Exception)
            {
                // Majestic unreachable / returned a non-image — fall through.
            }
        }

        // 3. Briefly open the mainstream, grab a keyframe, release.
        try
        {
            var b = await GrabFromFreshMainAsync(camera, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.OpenedStream);
        }
        catch (Exception)
        {
            // Couldn't open the mainstream — fall through to the SD fallback.
        }

        // 4. Last resort: a running substream (SD) is better than no snapshot.
        if (liveSession is not null && liveQuality == StreamQuality.Sub)
        {
            var b = await TryGrabAsync(liveSession, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.LiveSub);
        }

        throw new InvalidOperationException($"No snapshot source available for camera {camera.Id}");
    }

    private async Task<byte[]?> TryGrabAsync(IVideoSession session, CancellationToken ct)
    {
        try
        {
            var b = await session.SnapshotAsync(SnapshotFormat.Jpeg, ct).ConfigureAwait(false);
            return b.Length > 0 ? b : null;
        }
        catch (Exception)
        {
            // Most commonly "No frame available yet" — let the caller fall through.
            return null;
        }
    }

    private async Task<byte[]?> GrabFromFreshMainAsync(Camera camera, CancellationToken ct)
    {
        var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
        // No auto-reconnect: this is a one-shot grab, we don't want a lingering
        // reconnect loop after we release.
        var options = VideoSessionOptions.Default(camera.RtspMainUri, creds) with { AutoReconnect = false };

        // Acquire is ref-counted: if a mainstream is already up (another viewer),
        // we share it and grab immediately; otherwise we own a fresh one.
        var session = _coordinator.Acquire(camera.Id, StreamQuality.Main, options);
        try
        {
            if (session.State == SessionState.Idle)
                await session.StartAsync(ct).ConfigureAwait(false);

            // Poll until the first keyframe decodes (SnapshotAsync returns an
            // empty array until then). Avoids an Rx dependency in Core just to
            // await one frame.
            var deadline = DateTime.UtcNow + FreshOpenFrameTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var b = await TryGrabAsync(session, ct).ConfigureAwait(false);
                if (b is not null) return b;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            await _coordinator.ReleaseAsync(camera.Id, StreamQuality.Main).ConfigureAwait(false);
        }
    }

    private async Task<Snapshot> SaveAsync(
        Camera camera, byte[] jpeg, SnapshotSource source, SnapshotKind kind, CancellationToken ct)
    {
        var id = SnapshotId.New();
        var takenAt = DateTime.UtcNow;

        var dir = Path.Combine(_fs.SnapshotsDir.FullName, camera.Id.ToString());
        Directory.CreateDirectory(dir);
        var fileName = takenAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".jpg";
        var path = EnsureUnique(Path.Combine(dir, fileName));
        await WriteAllBytesAsync(path, jpeg, ct).ConfigureAwait(false);

        var thumbDir = Path.Combine(_fs.SnapshotsDir.FullName, ".thumbs");
        Directory.CreateDirectory(thumbDir);
        var thumbPath = Path.Combine(thumbDir, id.ToString() + ".jpg");

        ImageSize size = default;
        string? savedThumb = thumbPath;
        try
        {
            size = await _thumbs.GenerateAsync(jpeg, thumbPath, ThumbMaxDim, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A missing thumbnail just means the gallery decodes the full image;
            // never let it sink the capture.
            savedThumb = null;
        }

        var snapshot = new Snapshot(id, camera.Id, takenAt, path, savedThumb, size.Width, size.Height, source, kind);
        await _repo.AddAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        // File.WriteAllBytesAsync isn't on netstandard2.1; stream it instead.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await fs.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[fs.Length];
        var offset = 0;
        int read;
        while (offset < buffer.Length &&
               (read = await fs.ReadAsync(buffer, offset, buffer.Length - offset, ct).ConfigureAwait(false)) > 0)
            offset += read;
        return buffer;
    }
}
