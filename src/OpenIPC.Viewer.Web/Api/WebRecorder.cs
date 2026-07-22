using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Web.Api;

// Recording started from the browser.
//
// The desktop head records through the Video project's libavformat session; the
// server head doesn't reference Video (no FFmpeg.AutoGen natives to ship), so it
// records the way it does everything else with video: one ffmpeg process writing
// the camera's own H.264 straight to disk. Same folder, same database table, so
// a clip recorded from the browser shows up in the desktop's Recordings page and
// vice versa.
//
// Stopping sends "q" on stdin rather than killing the process: an MP4 needs its
// trailer written, and a killed ffmpeg leaves a file no player will open.
//
// Long recordings are cut into segments (like the desktop's 10-minute chunks):
// a night-long single file is painful to download, to seek, and to lose — one
// corrupt tail would take the whole night with it. ffmpeg's segment muxer closes
// each part properly on its own, so every finished segment is playable even if
// the server dies mid-recording.
public sealed class WebRecorder : IAsyncDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    // Matches the desktop's RecordingOptions.SegmentDuration. Overridable so an
    // operator can trade file count against file size (and so this is testable
    // without waiting ten minutes).
    private static readonly TimeSpan SegmentDuration = ResolveSegmentDuration();

    private static TimeSpan ResolveSegmentDuration()
    {
        var raw = Environment.GetEnvironmentVariable("OPENIPC_WEB_SEGMENT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds is > 0 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);
    }

    private readonly IRecordingRepository _repo;
    private readonly IFileSystem _fs;
    private readonly ILogger<WebRecorder> _logger;
    private readonly ConcurrentDictionary<string, ActiveRecording> _active = new(StringComparer.OrdinalIgnoreCase);

    public WebRecorder(IRecordingRepository repo, IFileSystem fs, ILogger<WebRecorder> logger)
    {
        _repo = repo;
        _fs = fs;
        _logger = logger;
    }

    public bool IsRecording(string cameraId) => _active.ContainsKey(cameraId);

    public IReadOnlyList<string> ActiveCameraIds => _active.Keys.ToList();

    public async Task<Recording?> StartAsync(Camera camera, string rtspUrl, CancellationToken ct)
    {
        var cameraId = camera.Id.ToString();
        if (_active.ContainsKey(cameraId))
            return null;

        // Same layout as the desktop head — <recordings>/<camera-slug>/<date>/ —
        // so both write into one archive a person can still navigate by hand.
        var dir = Path.Combine(
            _fs.RecordingsDir.FullName,
            Slug(camera.Name),
            DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        // ffmpeg fills in the %03d; the first segment's name is predictable, which
        // is what gets indexed up front.
        var baseName = $"cam_{DateTime.Now:yyyyMMdd_HHmmss}";
        var pattern = Path.Combine(dir, baseName + "_%03d.mp4");
        var path = Path.Combine(dir, baseName + "_000.mp4");

        var psi = new ProcessStartInfo(LiveFfmpeg.ResolveExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-rtsp_transport", "tcp", "-i", rtspUrl,
                     // Copy, never re-encode: recording must not cost CPU per camera,
                     // and the archive should hold what the camera actually sent.
                     "-c", "copy",
                     "-f", "segment",
                     "-segment_time", ((int)SegmentDuration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                     // Each part starts at zero, so every segment plays as its own
                     // file instead of pretending to begin hours in.
                     "-reset_timestamps", "1",
                     "-segment_format", "mp4",
                     pattern,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi);
        if (process is null)
            return null;
        _ = LiveFfmpeg.DrainStderrAsync(process, _logger);

        var recording = new Recording(
            Id: RecordingId.New(),
            CameraId: camera.Id,
            FilePath: path,
            StartedAt: DateTime.UtcNow,
            EndedAt: null,
            SizeBytes: 0,
            Codec: null,
            HasMotion: false);

        // Indexed while running, so a server that dies mid-recording still leaves
        // a row pointing at the partial file instead of an orphan on disk.
        await _repo.AddAsync(recording, ct);
        _active[cameraId] = new ActiveRecording(process, recording, baseName, dir);
        _logger.LogInformation("Recording {Camera} to {Path}", camera.Name, path);
        return recording;
    }

    /// Returns the finished recording, or null when there was nothing to stop
    /// OR the camera produced an empty file (see below) — the caller tells the
    /// two apart with <see cref="IsRecording"/>.
    public async Task<Recording?> StopAsync(string cameraId, CancellationToken ct)
    {
        if (!_active.TryRemove(cameraId, out var active))
            return null;

        try
        {
            // "q" is ffmpeg's clean shutdown: finish the current packet, write the
            // trailer, exit. Only if it ignores us does the process get killed.
            await active.Process.StandardInput.WriteAsync("q");
            await active.Process.StandardInput.FlushAsync(ct);
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
            grace.CancelAfter(StopGrace);
            await active.Process.WaitForExitAsync(grace.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg did not stop cleanly; killing it (the file may be unplayable)");
            try { active.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        finally
        {
            active.Process.Dispose();
        }

        return await ReconcileSegmentsAsync(active, DateTime.UtcNow, ct);
    }

    // Turns whatever ffmpeg actually wrote into archive rows.
    //
    // Only the first segment is indexed at start (its name is predictable), so
    // the rest are added here, and the first row is updated with its real size.
    // Timestamps come from the segment index rather than the file's mtime: mtime
    // is when writing FINISHED, and the archive is browsed by when footage began.
    private async Task<Recording?> ReconcileSegmentsAsync(
        ActiveRecording active, DateTime stoppedAt, CancellationToken ct)
    {
        var segments = Directory
            .GetFiles(active.Directory, active.BaseName + "_*.mp4")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Recording? first = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var info = new FileInfo(segments[i]);
            var isFirst = string.Equals(info.FullName, active.Recording.FilePath, StringComparison.OrdinalIgnoreCase);

            if (!info.Exists || info.Length == 0)
            {
                // A trailing empty part is normal when the stop lands right on a
                // segment boundary; an empty first part means the camera gave
                // nothing at all. Neither belongs in the archive.
                if (isFirst) await _repo.RemoveAsync(active.Recording.Id, ct);
                try { if (info.Exists) info.Delete(); } catch (IOException) { /* leave it */ }
                continue;
            }

            var startedAt = active.Recording.StartedAt + i * SegmentDuration;
            var endedAt = i + 1 < segments.Count
                ? active.Recording.StartedAt + (i + 1) * SegmentDuration
                : stoppedAt;

            var row = isFirst
                ? active.Recording with { EndedAt = endedAt, SizeBytes = info.Length }
                : new Recording(
                    Id: RecordingId.New(),
                    CameraId: active.Recording.CameraId,
                    FilePath: info.FullName,
                    StartedAt: startedAt,
                    EndedAt: endedAt,
                    SizeBytes: info.Length,
                    Codec: null,
                    HasMotion: false);

            if (isFirst) await _repo.UpdateAsync(row, ct);
            else await _repo.AddAsync(row, ct);

            first ??= row;
        }

        if (first is null)
            _logger.LogWarning("Recording under {Base} produced no data; discarding it", active.BaseName);
        return first;
    }

    // A stopping server should not leave half-written files behind.
    public async ValueTask DisposeAsync()
    {
        foreach (var cameraId in _active.Keys.ToList())
        {
            try { await StopAsync(cameraId, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop recording for {Camera} on shutdown", cameraId); }
        }
    }

    // Mirrors RecordingService.Slug so a camera lands in the same folder no
    // matter which head recorded it.
    private static string Slug(string name)
    {
        var chars = new List<char>(name.Length);
        foreach (var raw in name.ToLowerInvariant())
        {
            var c = raw == ' ' ? '-' : raw;
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_') chars.Add(c);
        }
        return chars.Count == 0 ? "camera" : new string(chars.ToArray());
    }

    private sealed record ActiveRecording(Process Process, Recording Recording, string BaseName, string Directory);
}
