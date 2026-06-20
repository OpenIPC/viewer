using System;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Video.Pipeline;

// Wraps an inner-session factory and transparently re-creates the session on
// Failed, with exponential backoff 1→2→4→8→16→30s (then 30s capped). A
// successful frame resets the backoff to zero. After ColdFailures consecutive
// attempts with no frame the wrapper surfaces Offline (the interactive error
// cell) while still probing at the 30s cadence. A watchdog forces a reconnect
// when a "Playing" stream stops delivering frames — FFmpeg can sit on a dead
// RTSP socket without erroring. Auth errors (401, Unauthorized, EACCES) abort
// permanently — we never retry against a wrong password (would lock the camera
// out / DDoS it). Phase 12.3.
internal sealed class AutoReconnectingVideoSession : IVideoSession
{
    // No decoded frame for this long while Playing → treat the stream as hung.
    private const long FrameTimeoutTicks = 5 * TimeSpan.TicksPerSecond;
    // Consecutive failed attempts (no successful frame) before going Offline.
    private const int ColdFailures = 5;

    private readonly Func<IVideoSession> _innerFactory;
    private readonly ILogger _logger;

    private readonly Subject<VideoFrame> _frames = new();
    private readonly Subject<AudioFrame> _audioFrames = new();
    private readonly Subject<SessionState> _stateChanged = new();
    private readonly Subject<SessionTelemetry> _telemetry = new();

    private readonly object _stateLock = new();
    private SessionState _state = SessionState.Idle;
    private string? _lastError;

    // Watchdog shared state. _lastActivityTicks is the UTC tick of the last
    // frame (or the moment Playing was reached); _watching gates the watchdog
    // so it only fires while the inner session believes it is Playing.
    private long _lastActivityTicks;
    private volatile bool _watching;
    // Transition-only logging — avoids a log line per retry attempt.
    private SessionState _lastLoggedState = SessionState.Idle;

    private IVideoSession? _activeInner;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    // Sticky across reconnects: a session created mid-pause starts paused too.
    private volatile bool _pauseRequested;
    // Sticky audio toggle (Phase 17). null = untouched (inner uses its option
    // default); once set, re-applied to every reconnected inner session.
    private bool? _audioRequested;

    public AutoReconnectingVideoSession(Func<IVideoSession> innerFactory, ILogger logger)
    {
        _innerFactory = innerFactory;
        _logger = logger;
    }

    public SessionState State { get { lock (_stateLock) return _state; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }
    public IObservable<VideoFrame> Frames => _frames;
    public IObservable<AudioFrame> AudioFrames => _audioFrames;
    public IObservable<SessionState> StateChanged => _stateChanged;
    public IObservable<SessionTelemetry> Telemetry => _telemetry;

    public Task StartAsync(CancellationToken ct)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Already started");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct)
    {
        var inner = _activeInner ?? throw new InvalidOperationException("No active session");
        return inner.SnapshotAsync(format, ct);
    }

    public void SetAudioEnabled(bool enabled)
    {
        _audioRequested = enabled;
        _activeInner?.SetAudioEnabled(enabled);
    }

    public void PauseDecode()
    {
        _pauseRequested = true;
        _activeInner?.PauseDecode();
    }

    public void Resume()
    {
        _pauseRequested = false;
        _activeInner?.Resume();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* expected when cancelled */ }
        }
        if (_activeInner is not null)
            await _activeInner.DisposeAsync().ConfigureAwait(false);
        _frames.OnCompleted();
        _audioFrames.OnCompleted();
        _stateChanged.OnCompleted();
        _telemetry.OnCompleted();
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var inner = _innerFactory();
            _activeInner = inner;
            var sawFrame = false;

            var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            // failed.Task only completes when the inner session reports Failed/Idle
            // (or the watchdog forces it). On Dispose the token is cancelled while
            // the inner is still Playing, so without this the await below would hang
            // forever and Dispose would never return (freezing the layout-switch
            // command). Cancelling the TCS lets the OperationCanceledException catch
            // unwind the loop promptly.
            using var ctReg = ct.Register(() => failed.TrySetCanceled());

            using var framesSub = inner.Frames.Subscribe(f =>
            {
                Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                // A real decoded frame means the connection is healthy — reset the
                // backoff so a later blip starts again from 1s, not the capped 30s.
                if (!sawFrame) { sawFrame = true; attempt = 0; }
                _frames.OnNext(f);
            });
            using var audioSub = inner.AudioFrames.Subscribe(_audioFrames.OnNext);
            using var telemetrySub = inner.Telemetry.Subscribe(_telemetry.OnNext);
            using var stateSub = inner.StateChanged.Subscribe(s =>
            {
                if (s == SessionState.Playing)
                {
                    Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                    _watching = true;
                    LogTransition(SessionState.Playing, null);
                }
                else
                {
                    _watching = false;
                }
                SetState(s);
                if (s is SessionState.Failed or SessionState.Idle)
                    failed.TrySetResult(inner.LastError);
            });

            try
            {
                await inner.StartAsync(ct).ConfigureAwait(false);
                if (_pauseRequested) inner.PauseDecode();
                if (_audioRequested is { } audio) inner.SetAudioEnabled(audio);

                using (StartWatchdog(failed, ct))
                {
                    var error = await failed.Task.ConfigureAwait(false);
                    _watching = false;

                    if (IsAuthFailure(error))
                    {
                        SetState(SessionState.Failed, error);
                        LogTransition(SessionState.Failed, error);
                        return;
                    }

                    attempt++;
                    var cold = attempt >= ColdFailures;
                    // Cold mode probes at the capped cadence; otherwise climb the
                    // exponential ramp 1→2→4→8→16→30s.
                    var delay = cold
                        ? TimeSpan.FromSeconds(ReconnectBackoff.MaxSeconds)
                        : ReconnectBackoff.Delay(attempt);

                    // Offline (Failed) surfaces the interactive error cell after
                    // ColdFailures dead attempts; below that it's a transient
                    // Reconnecting badge.
                    var next = cold ? SessionState.Failed : SessionState.Reconnecting;
                    SetState(next, error);
                    LogTransition(next, error);

                    // Stop listening before disposing: the inner decode thread
                    // emits a terminal Idle as it unwinds, which would otherwise
                    // clobber the Reconnecting/Offline badge we just set.
                    stateSub.Dispose();
                    await inner.DisposeAsync().ConfigureAwait(false);
                    _activeInner = null;

                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect loop crashed");
                SetState(SessionState.Failed, ex.Message);
                return;
            }
        }
    }

    // Background frame watchdog. While the inner session reports Playing, a gap
    // of FrameTimeoutTicks with no decoded frame means the stream is hung — push
    // the same failure path as an explicit disconnect. Disposing the returned
    // handle cancels the loop.
    private IDisposable StartWatchdog(TaskCompletionSource<string?> failed, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                    if (!_watching) continue;
                    var idle = DateTime.UtcNow.Ticks - Volatile.Read(ref _lastActivityTicks);
                    if (idle > FrameTimeoutTicks)
                    {
                        failed.TrySetResult("Stream stalled (no frames for 5s)");
                        return;
                    }
                }
            }
            catch (Exception) { /* cancelled on dispose */ }
        }, cts.Token);

        return Disposable.Create(() =>
        {
            cts.Cancel();
            cts.Dispose();
        });
    }

    // One log line per state transition, not per retry attempt (Phase 12.3).
    private void LogTransition(SessionState state, string? error)
    {
        if (_lastLoggedState == state) return;
        _lastLoggedState = state;
        switch (state)
        {
            case SessionState.Playing:
                _logger.LogInformation("Video session live");
                break;
            case SessionState.Reconnecting:
                _logger.LogInformation("Video session reconnecting: {Error}", error);
                break;
            case SessionState.Failed:
                _logger.LogWarning("Video session offline: {Error}", error);
                break;
        }
    }

    private static bool IsAuthFailure(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("Unauthor", StringComparison.OrdinalIgnoreCase) >= 0
            || error.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetState(SessionState newState, string? error = null)
    {
        lock (_stateLock)
        {
            _state = newState;
            if (error is not null) _lastError = error;
        }
        _stateChanged.OnNext(newState);
    }
}
