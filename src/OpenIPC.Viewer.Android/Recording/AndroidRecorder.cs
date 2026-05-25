using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Core.Content;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Recording;
using OpenIPC.Viewer.Video.Recording;

namespace OpenIPC.Viewer.Android.Recording;

// IRecorder wrapper for Android. Delegates the actual ffmpeg work to
// LibavformatRecorder (cross-platform, in-process via libavformat) and
// pairs each session with a foreground service so the OS keeps the
// process alive in the background.
//
// The service is fire-and-forget: we start it on session start, stop it
// on session stop. Crashes in the service don't interrupt the recording
// loop (the session owns its own thread + ffmpeg handles); they just lift
// the OS's "don't kill me" guarantee — typically harmless on a foreground
// app, surfaces during backgrounding.
internal sealed class AndroidRecorder : IRecorder
{
    private readonly Context _appContext;
    private readonly LibavformatRecorder _inner;
    private readonly ILogger<AndroidRecorder> _logger;

    public AndroidRecorder(Context appContext, ILoggerFactory loggerFactory)
    {
        _appContext = appContext;
        _inner = new LibavformatRecorder(loggerFactory);
        _logger = loggerFactory.CreateLogger<AndroidRecorder>();
    }

    public async Task<IRecordingSession> StartAsync(RecordingOptions options, CancellationToken ct)
    {
        var session = await _inner.StartAsync(options, ct).ConfigureAwait(false);
        StartService(options.CameraId.ToString());
        return new ServiceBoundSession(session, _appContext, _logger);
    }

    private void StartService(string cameraName)
    {
        var intent = new Intent(_appContext, typeof(RecordingForegroundService));
        intent.SetAction(RecordingForegroundService.ActionStart);
        intent.PutExtra(RecordingForegroundService.ExtraCameraName, cameraName);
        try
        {
            ContextCompat.StartForegroundService(_appContext, intent);
        }
        catch (Exception ex)
        {
            // Falls back to a regular (non-foreground) startup attempt. On
            // Android 12+ this is restricted further but the OS will at
            // least surface the failure clearly — recording itself still
            // runs since the session is in-process.
            _logger.LogWarning(ex, "StartForegroundService failed; recording continues without keep-alive guarantee");
        }
    }

    private sealed class ServiceBoundSession : IRecordingSession
    {
        private readonly IRecordingSession _inner;
        private readonly Context _appContext;
        private readonly ILogger _logger;
        private int _stopped;

        public ServiceBoundSession(IRecordingSession inner, Context appContext, ILogger logger)
        {
            _inner = inner;
            _appContext = appContext;
            _logger = logger;
        }

        public DateTime StartedAt => _inner.StartedAt;
        public string? CurrentSegmentPath => _inner.CurrentSegmentPath;
        public IObservable<RecordingEvent> Events => _inner.Events;

        public async Task StopAsync(CancellationToken ct)
        {
            try { await _inner.StopAsync(ct).ConfigureAwait(false); }
            finally { StopService(); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { StopService(); }
        }

        private void StopService()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            var intent = new Intent(_appContext, typeof(RecordingForegroundService));
            intent.SetAction(RecordingForegroundService.ActionStop);
            try { _appContext.StartService(intent); }
            catch (Exception ex) { _logger.LogDebug(ex, "Stop-service intent failed"); }
        }
    }
}
