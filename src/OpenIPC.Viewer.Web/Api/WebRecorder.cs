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
public sealed class WebRecorder : IAsyncDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

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
        var path = Path.Combine(dir, $"cam_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

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
                     "-movflags", "+faststart",
                     path,
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
        _active[cameraId] = new ActiveRecording(process, recording);
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

        var file = new FileInfo(active.Recording.FilePath);
        var size = file.Exists ? file.Length : 0;
        if (size == 0)
        {
            // The camera never delivered anything (offline, or ffmpeg died before
            // the first packet). An empty file is not an archive entry — drop both
            // rather than leave a row that plays nothing.
            _logger.LogWarning("Recording of {Path} produced no data; discarding it", active.Recording.FilePath);
            await _repo.RemoveAsync(active.Recording.Id, ct);
            try { if (file.Exists) file.Delete(); } catch (IOException) { /* leave the stub */ }
            return null;
        }

        var finished = active.Recording with { EndedAt = DateTime.UtcNow, SizeBytes = size };
        await _repo.UpdateAsync(finished, ct);
        return finished;
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

    private sealed record ActiveRecording(Process Process, Recording Recording);
}
