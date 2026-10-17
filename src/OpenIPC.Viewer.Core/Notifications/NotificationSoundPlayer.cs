using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Core.Notifications;

// Plays a PcmClip through a PRIVATE IAudioOutput instance (the factory makes a
// fresh one), so a notification sound never fights the live camera audio for
// the shared sink — WASAPI shared mode / ALSA / CoreAudio all mix streams.
//
// IAudioOutput is a live-monitor sink with a ~0.5 s drop-oldest ring, so the
// clip is fed in real time with a small lead instead of in one write. A new
// Play cancels the one in progress; the device is released shortly after the
// clip ends.
public sealed class NotificationSoundPlayer : IDisposable
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Chunk = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(350);

    private readonly Func<IAudioOutput?> _factory;
    private readonly object _gate = new();
    private IAudioOutput? _output;
    private CancellationTokenSource? _current;
    private bool _disposed;

    public NotificationSoundPlayer(Func<IAudioOutput?> factory) => _factory = factory;

    public void Play(PcmClip clip, double volume)
    {
        if (clip.Pcm16.Length == 0 || volume <= 0) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            // Cancel, don't dispose: the superseded run may still be waiting on
            // its token's handle.
            _current?.Cancel();
            _current = cts = new CancellationTokenSource();
        }
        var pcm = clip.WithVolume(volume);
        _ = Task.Run(() => Run(clip, pcm, cts.Token));
    }

    private void Run(PcmClip clip, byte[] pcm, CancellationToken ct)
    {
        IAudioOutput? output;
        try
        {
            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                output = _output ??= _factory();
            }
            if (output is null || !output.IsAvailable) return;

            output.Start(clip.SampleRate, clip.Channels);

            var block = clip.Channels * 2;
            var bytesPerSecond = clip.BytesPerSecond;
            var chunkBytes = Align((int)(bytesPerSecond * Chunk.TotalSeconds), block);
            var leadBytes = Align((int)(bytesPerSecond * Lead.TotalSeconds), block);

            var sw = Stopwatch.StartNew();
            var written = 0;
            while (written < pcm.Length && !ct.IsCancellationRequested)
            {
                // Stay at most `Lead` ahead of the playback clock.
                var played = (long)(sw.Elapsed.TotalSeconds * bytesPerSecond);
                if (written - played < leadBytes)
                {
                    var n = Math.Min(chunkBytes, pcm.Length - written);
                    output.Write(new ReadOnlySpan<byte>(pcm, written, n));
                    written += n;
                    continue;
                }
                Thread.Sleep(Chunk.Milliseconds / 2);
            }

            // Let the buffered tail drain, then free the device unless a newer
            // Play has taken over in the meantime.
            var remaining = TimeSpan.FromSeconds(pcm.Length / (double)bytesPerSecond) - sw.Elapsed + Tail;
            if (remaining > TimeSpan.Zero && ct.WaitHandle.WaitOne(remaining)) return;
            lock (_gate)
            {
                if (!ct.IsCancellationRequested) output.Stop();
            }
        }
        catch (Exception)
        {
            // A sound is a nicety — never let it take anything down.
        }
    }

    private static int Align(int bytes, int block) => Math.Max(block, bytes - bytes % block);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
            try { _output?.Dispose(); } catch (Exception) { /* tearing down */ }
            _output = null;
        }
    }
}
