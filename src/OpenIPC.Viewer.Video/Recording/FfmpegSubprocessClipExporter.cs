using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Video.Pipeline;

namespace OpenIPC.Viewer.Video.Recording;

// Exports a clip by shelling out to ffmpeg (Phase 16.5) — same strategy and
// path resolution as FfmpegSubprocessRecorder. Stream-copy by default (fast,
// GOP-accurate); precise mode re-encodes for a frame-accurate cut.
public sealed partial class FfmpegSubprocessClipExporter : IClipExporter
{
    [GeneratedRegex(@"time=(\d+):(\d{2}):(\d{2})\.(\d+)")]
    private static partial Regex TimeRegex();

    private readonly ILogger<FfmpegSubprocessClipExporter> _logger;
    private readonly string _ffmpegPath;

    public FfmpegSubprocessClipExporter(ILoggerFactory loggerFactory, string? ffmpegPathOverride = null)
    {
        _logger = loggerFactory.CreateLogger<FfmpegSubprocessClipExporter>();
        _ffmpegPath = !string.IsNullOrWhiteSpace(ffmpegPathOverride) ? ffmpegPathOverride! : ResolveDefault();
    }

    public async Task ExportAsync(ClipExportRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var dur = ClipBounds.Duration(request.Start, request.End);
        if (dur <= TimeSpan.Zero)
            throw new ArgumentException("Export range is empty", nameof(request));

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);

        var startSec = request.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var durSec = dur.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");

        if (request.Precise)
        {
            // Output seeking (after -i) = frame-accurate; re-encode video.
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(request.SourcePath);
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(startSec);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(durSec);
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("20");
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("copy");
        }
        else
        {
            // Input seeking (before -i) = fast, snaps to the keyframe ≤ start.
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(startSec);
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(request.SourcePath);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(durSec);
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        }
        psi.ArgumentList.Add(request.DestinationPath);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch ffmpeg (check PATH)");

        var totalSec = dur.TotalSeconds;
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var m = TimeRegex().Match(line);
                if (m.Success && totalSec > 0)
                {
                    var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                          + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                          + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
                          + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
                    progress?.Report(Math.Clamp(t / totalSec, 0, 1));
                }
                else
                {
                    _logger.LogDebug("ffmpeg-export: {Line}", line);
                }
            }
        }, ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception ex) { _logger.LogDebug(ex, "Kill on cancel failed"); }
            throw;
        }

        await stderr.ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg export failed (exit {proc.ExitCode})");

        progress?.Report(1.0);
    }

    private static string ResolveDefault()
    {
        var (rid, exe) = RuntimeIds.Current();
        if (rid is not null)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", exe);
            if (File.Exists(bundled)) return bundled;
        }
        return "ffmpeg";
    }
}
