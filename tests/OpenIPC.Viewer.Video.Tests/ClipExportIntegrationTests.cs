using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIPC.Viewer.Core.Archive;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Core.Video;
using OpenIPC.Viewer.Video.Pipeline;
using OpenIPC.Viewer.Video.Recording;

namespace OpenIPC.Viewer.Video.Tests;

// Phase 16.7 integration: record a few seconds (in-process libav, the mobile
// path) → probe the file → export an in/out range → probe the clip. Verifies
// the probe reports a real duration and the clip length ≈ the selection.
// Gated on the MediaMTX fixture (skips when docker isn't up), like the live
// session test. Tolerances are loose — this is an end-to-end smoke test.
public sealed class ClipExportIntegrationTests
{
    [SkippableFact]
    public async Task Record_Probe_Export_RoundTrip()
    {
        Skip.IfNot(MediaMtxFixture.IsReachable(),
            "MediaMTX not running on localhost:8554 — start tools/mediamtx/docker-compose.yml first.");

        var dir = Path.Combine(Path.GetTempPath(), "openipc-cliptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // --- Record ~5s ---
            var recorder = new LibavformatRecorder(NullLoggerFactory.Instance);
            var options = new RecordingOptions(
                CameraId: CameraId.New(),
                RtspUri: new Uri(MediaMtxFixture.TestStreamUri),
                Credentials: null,
                OutputDirectory: dir,
                FilenamePattern: "rec.mp4",
                SegmentDuration: TimeSpan.FromMinutes(10));

            var session = await recorder.StartAsync(options, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(5));
            var sourcePath = session.CurrentSegmentPath;
            await session.StopAsync(CancellationToken.None);
            await session.DisposeAsync();

            Assert.False(string.IsNullOrEmpty(sourcePath), "Recorder produced no file path");
            Assert.True(File.Exists(sourcePath), $"Recorded file missing: {sourcePath}");

            // --- Probe the recording ---
            var probe = new FfmpegMediaProbe(NullLogger<FfmpegMediaProbe>.Instance);
            var srcInfo = await probe.ProbeAsync(sourcePath!, CancellationToken.None);
            Assert.InRange(srcInfo.Duration.TotalSeconds, 1.5, 15.0);

            // --- Export [1s, 3s] (stream copy) ---
            var clipPath = Path.Combine(dir, "clip.mp4");
            var exporter = new LibavformatClipExporter(NullLoggerFactory.Instance);
            await exporter.ExportAsync(
                new ClipExportRequest(sourcePath!, clipPath, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), Precise: false),
                progress: null, CancellationToken.None);

            Assert.True(File.Exists(clipPath), "Exported clip missing");

            // --- Probe the clip: ~2s ± GOP slack ---
            var clipInfo = await probe.ProbeAsync(clipPath, CancellationToken.None);
            Assert.InRange(clipInfo.Duration.TotalSeconds, 0.3, 5.0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
