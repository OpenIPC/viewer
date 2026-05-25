using System;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Video.Pipeline;

// Wraps an inner-session factory and transparently re-creates the session
// on Failed, with backoff 1→2→5→10→30s (then 30s capped). Auth errors
// (401, Unauthorized, EACCES) abort permanently — we never retry against
// a wrong password (would lock the camera out / DDoS it).
internal sealed class AutoReconnectingVideoSession : IVideoSession
{
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    };

    private readonly Func<IVideoSession> _innerFactory;
    private readonly ILogger _logger;

    private readonly Subject<VideoFrame> _frames = new();
    private readonly Subject<SessionState> _stateChanged = new();
    private readonly Subject<SessionTelemetry> _telemetry = new();

    private readonly object _stateLock = new();
    private SessionState _state = SessionState.Idle;
    private string? _lastError;

    private IVideoSession? _activeInner;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AutoReconnectingVideoSession(Func<IVideoSession> innerFactory, ILogger logger)
    {
        _innerFactory = innerFactory;
        _logger = logger;
    }

    public SessionState State { get { lock (_stateLock) return _state; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }
    public IObservable<VideoFrame> Frames => _frames;
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

            using var framesSub = inner.Frames.Subscribe(_frames.OnNext);
            using var telemetrySub = inner.Telemetry.Subscribe(_telemetry.OnNext);

            var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stateSub = inner.StateChanged.Subscribe(s =>
            {
                SetState(s);
                if (s is SessionState.Failed or SessionState.Idle && attempt > 0)
                    failed.TrySetResult(inner.LastError);
                else if (s == SessionState.Failed)
                    failed.TrySetResult(inner.LastError);
            });

            try
            {
                await inner.StartAsync(ct).ConfigureAwait(false);
                var error = await failed.Task.ConfigureAwait(false);

                if (IsAuthFailure(error))
                {
                    _logger.LogWarning("Auth failure for video session ({Error}); will not retry.", error);
                    SetState(SessionState.Failed, error);
                    return;
                }

                attempt++;
                var delay = Backoff[Math.Min(attempt - 1, Backoff.Length - 1)];
                SetState(SessionState.Reconnecting, error);
                _logger.LogInformation("Reconnect attempt {Attempt} in {Delay}s after {Error}", attempt, delay.TotalSeconds, error);

                await inner.DisposeAsync().ConfigureAwait(false);
                _activeInner = null;

                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
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
