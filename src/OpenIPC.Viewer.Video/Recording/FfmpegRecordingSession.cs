using System;
using System.Diagnostics;
using System.IO;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;

namespace OpenIPC.Viewer.Video.Recording;

internal sealed partial class FfmpegRecordingSession : IRecordingSession
{
    // ffmpeg prints "[segment @ 0x...] Opening 'path' for writing" at the start
    // of every segment. We rely on that instead of -segment_list because the
    // list output only appears after a segment is CLOSED, so the first segment
    // wouldn't fire a Started event until the first rotation.
    [GeneratedRegex(@"Opening '([^']+)' for writing")]
    private static partial Regex OpeningRegex();

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly RecordingOptions _options;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private readonly Subject<RecordingEvent> _events = new();

    private Process? _proc;
    private string? _currentSegment;
    private string? _lastSegment;
    private bool _stopRequested;

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public string? CurrentSegmentPath => _currentSegment;
    public IObservable<RecordingEvent> Events => _events;

    public FfmpegRecordingSession(RecordingOptions options, string ffmpegPath, ILogger logger)
    {
        _options = options;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    public void Start()
    {
        var outputPattern = Path.Combine(_options.OutputDirectory, _options.FilenamePattern);

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(BuildRtspUri(_options.RtspUri, _options.Credentials).ToString());
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("segment");
        psi.ArgumentList.Add("-segment_time"); psi.ArgumentList.Add(((int)_options.SegmentDuration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-reset_timestamps"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-strftime"); psi.ArgumentList.Add("1");
        // Fragmented MP4 → kill-survivable segments (phase-16.1). No +faststart:
        // it relocates the moov in a second pass over a *finished* file, which a
        // fragmented (empty_moov) stream doesn't have and which defeats the
        // play-while-recording / crash-survivable guarantee. default_base_moof
        // matches the in-process libavformat path and improves player compat.
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof");
        psi.ArgumentList.Add(outputPattern);

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch ffmpeg (check PATH)");

        _ = Task.Run(ReadStderrAsync);
        _ = Task.Run(WatchExitAsync);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopRequested = true;
        if (_proc is null || _proc.HasExited)
        {
            EmitStopped(RecordingStopReason.User);
            return;
        }

        try
        {
            // 'q' on stdin = graceful shutdown; finalizes the moov box.
            await _proc.StandardInput.WriteAsync("q").ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write 'q' to ffmpeg stdin");
        }

        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        killCts.CancelAfter(StopGrace);
        try
        {
            await _proc.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ffmpeg did not exit within {Sec}s; killing", StopGrace.TotalSeconds);
            try { _proc.Kill(entireProcessTree: true); } catch (Exception ex) { _logger.LogWarning(ex, "Kill failed"); }
        }

        EmitStopped(RecordingStopReason.User);
    }

    public ValueTask DisposeAsync()
    {
        try { _events.OnCompleted(); } catch { /* already completed */ }
        try { _proc?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    private async Task ReadStderrAsync()
    {
        if (_proc is null) return;
        try
        {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var m = OpeningRegex().Match(line);
                if (m.Success)
                    HandleSegmentOpened(m.Groups[1].Value);
                else
                    _logger.LogDebug("ffmpeg[{Cam}]: {Line}", _options.CameraId, line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg stderr reader failed");
        }
    }

    private async Task WatchExitAsync()
    {
        if (_proc is null) return;
        try { await _proc.WaitForExitAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
        if (!_stopRequested)
            EmitStopped(RecordingStopReason.ProcessExited);
    }

    private void HandleSegmentOpened(string path)
    {
        var prev = _currentSegment;
        _currentSegment = path;
        _lastSegment = path;
        if (prev is null)
        {
            _events.OnNext(new RecordingEvent.Started(path, DateTime.UtcNow));
        }
        else
        {
            // prev segment is now closed; size should be stable.
            var size = TryFileSize(prev);
            _events.OnNext(new RecordingEvent.SegmentRotated(prev, path, DateTime.UtcNow, size));
        }
    }

    private void EmitStopped(RecordingStopReason reason)
    {
        try { _events.OnNext(new RecordingEvent.Stopped(DateTime.UtcNow, reason)); }
        catch { /* observers may already be unsubscribed */ }
    }

    private static long? TryFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static Uri BuildRtspUri(Uri rtsp, CameraCredentials? creds)
    {
        if (creds is null) return rtsp;
        var b = new UriBuilder(rtsp) { UserName = creds.Username, Password = creds.Password };
        return b.Uri;
    }
}
