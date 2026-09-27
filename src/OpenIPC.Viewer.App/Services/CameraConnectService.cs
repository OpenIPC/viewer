using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Services;

public enum CameraConnectFailure { None, Unreachable, Unauthorized, NoVideo, Timeout }

// What the camera editor sends to "Connect". RtspMain/RtspSub are only the
// user's own URIs — null means "work it out".
public sealed record CameraConnectRequest(
    string Host,
    int HttpPort,
    int? OnvifPort,
    Uri? RtspMain,
    Uri? RtspSub,
    CameraCredentials? Credentials);

public sealed record CameraConnectResult(
    CameraConnectFailure Failure,
    TimeSpan Elapsed,
    bool IsMajestic,
    MajesticInfo? Majestic,
    OnvifProbeResult? Onvif,
    int? OnvifPort,
    Uri? RtspMain,
    Uri? RtspSub,
    int MainWidth,
    int MainHeight,
    string? MainCodec,
    int SubWidth,
    int SubHeight,
    bool HasAudio,
    byte[]? SnapshotJpeg,
    string? ErrorDetail)
{
    public bool Ok => Failure == CameraConnectFailure.None;
    public bool HasPtz => Onvif?.HasPtz == true;
}

// "Connect" in the camera editor: works out what sits at an address and how to
// stream it. Identifies the device (OpenIPC/Majestic web API, ONVIF), picks the
// RTSP URIs, then proves them by decoding a frame — the same pipeline the live
// view uses — and grabs a still for the result card.
public sealed class CameraConnectService
{
    private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MainStreamTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SubStreamTimeout = TimeSpan.FromSeconds(6);

    // Where ONVIF device services commonly live when the user didn't say.
    private static readonly int[] OnvifPortGuesses = { 80, 8899, 8000, 2020, 8080 };

    private readonly IVideoEngine _engine;
    private readonly OnvifProbeService _onvif;
    private readonly IOnvifFingerprint _onvifFingerprint;
    private readonly IMajesticClient _majestic;
    private readonly IReachabilityProbe _reach;
    private readonly UserSettingsService _settings;
    private readonly ILogger<CameraConnectService> _logger;

    public CameraConnectService(
        IVideoEngine engine,
        OnvifProbeService onvif,
        IOnvifFingerprint onvifFingerprint,
        IMajesticClient majestic,
        IReachabilityProbe reach,
        UserSettingsService settings,
        ILogger<CameraConnectService> logger)
    {
        _engine = engine;
        _onvif = onvif;
        _onvifFingerprint = onvifFingerprint;
        _majestic = majestic;
        _reach = reach;
        _settings = settings;
        _logger = logger;
    }

    // OpenIPC firmware serves these two paths on the default RTSP port.
    public static Uri MajesticMain(string host) => new($"rtsp://{host}:554/stream0");
    public static Uri MajesticSub(string host) => new($"rtsp://{host}:554/stream1");

    public async Task<CameraConnectResult> ConnectAsync(CameraConnectRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var host = req.Host;
        var rtspPort = req.RtspMain is { } um && um.Port > 0 ? um.Port : 554;

        // 1. Anyone home? Fails fast instead of waiting out every timeout.
        var reachRtsp = _reach.IsReachableAsync(host, rtspPort, ReachTimeout, ct);
        var reachHttp = _reach.IsReachableAsync(host, req.HttpPort, ReachTimeout, ct);
        var reachable = (await Task.WhenAll(reachRtsp, reachHttp).ConfigureAwait(true)).Any(r => r);
        if (!reachable)
            return Fail(CameraConnectFailure.Unreachable, sw, null);

        // 2. Identify, both ways at once.
        var majTask = IdentifyMajesticAsync(host, req.HttpPort, req.Credentials, ct);
        var onvifTask = IdentifyOnvifAsync(host, req.HttpPort, req.OnvifPort, req.Credentials, ct);
        await Task.WhenAll(majTask, onvifTask).ConfigureAwait(true);
        var maj = majTask.Result;
        var onv = onvifTask.Result;

        // 3. Streams: the user's own URI wins; then OpenIPC's fixed paths, then
        // what ONVIF reported, then the bare host.
        var mainGuessed = req.RtspMain is null;
        var main = req.RtspMain
            ?? (maj.IsMajestic ? MajesticMain(host) : onv.Probe?.RtspMainUri ?? new Uri($"rtsp://{host}/"));
        var subGuessed = req.RtspSub is null;
        var sub = req.RtspSub ?? (maj.IsMajestic ? MajesticSub(host) : null);

        var mainTask = TryStreamAsync(main, req.Credentials, MainStreamTimeout, snapshot: true, ct);
        var subTask = sub is null
            ? Task.FromResult(StreamProbe.None)
            : TryStreamAsync(sub, req.Credentials, SubStreamTimeout, snapshot: false, ct);
        await Task.WhenAll(mainTask, subTask).ConfigureAwait(true);
        var m = mainTask.Result;
        var s = subTask.Result;

        // A stock ONVIF URI can fail where the bare host works (and vice versa
        // for a guess) — one fallback before giving up.
        if (!m.Ok && mainGuessed && onv.Probe is null && !maj.IsMajestic && !m.Unauthorized)
        {
            var alt = new Uri($"rtsp://{host}:554/stream0");
            var retry = await TryStreamAsync(alt, req.Credentials, MainStreamTimeout, snapshot: true, ct).ConfigureAwait(true);
            if (retry.Ok) { m = retry; main = alt; }
        }

        if (!s.Ok && subGuessed) sub = null; // don't keep a sub path we invented and couldn't open

        var hasAudio = onv.Probe?.HasAudioIn == true || maj.AudioEnabled == true;
        if (!m.Ok)
        {
            var failure = m.Unauthorized || maj.Unauthorized || onv.Unauthorized
                ? CameraConnectFailure.Unauthorized
                : m.TimedOut ? CameraConnectFailure.Timeout : CameraConnectFailure.NoVideo;
            return new CameraConnectResult(failure, sw.Elapsed, maj.IsMajestic, maj.Info, onv.Probe, onv.Port,
                main, sub, 0, 0, null, 0, 0, hasAudio, null, m.Error);
        }

        return new CameraConnectResult(CameraConnectFailure.None, sw.Elapsed, maj.IsMajestic, maj.Info, onv.Probe, onv.Port,
            main, sub, m.Width, m.Height, m.Codec, s.Width, s.Height, hasAudio, m.Jpeg, null);
    }

    private static CameraConnectResult Fail(CameraConnectFailure f, Stopwatch sw, string? detail) =>
        new(f, sw.Elapsed, false, null, null, null, null, null, 0, 0, null, 0, 0, false, null, detail);

    private sealed record MajesticId(bool IsMajestic, MajesticInfo? Info, bool? AudioEnabled, bool Unauthorized);

    private async Task<MajesticId> IdentifyMajesticAsync(string host, int port, CameraCredentials? creds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IdentifyTimeout);
        try
        {
            var anon = new MajesticEndpoint(host, port, null);
            if (!await _majestic.PingAsync(anon, cts.Token).ConfigureAwait(false))
                return new MajesticId(false, null, null, false);

            var endpoint = new MajesticEndpoint(host, port, creds);
            MajesticInfo? info = null;
            bool? audio = null;
            var unauthorized = false;
            try { info = await _majestic.GetInfoAsync(endpoint, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { unauthorized |= IsAuthError(ex); }
            try { audio = (await _majestic.GetConfigAsync(endpoint, cts.Token).ConfigureAwait(false)).AudioEnabled; }
            catch (Exception ex) when (ex is not OperationCanceledException) { unauthorized |= IsAuthError(ex); }
            return new MajesticId(true, info, audio, unauthorized);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Majestic identify failed for {Host}", host);
            return new MajesticId(false, null, null, false);
        }
    }

    private sealed record OnvifId(OnvifProbeResult? Probe, int? Port, bool Unauthorized);

    private async Task<OnvifId> IdentifyOnvifAsync(string host, int httpPort, int? onvifPort, CameraCredentials? creds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IdentifyTimeout);
        try
        {
            var ports = (onvifPort is { } p ? (IEnumerable<int>)new[] { p } : new[] { httpPort }.Concat(OnvifPortGuesses)).Distinct().ToArray();
            // Cheap anonymous SOAP ping on every candidate, then the real
            // (authenticated) probe on the first port that answers.
            var hits = await Task.WhenAll(ports.Select(async port =>
                (Port: port, Uri: await _onvifFingerprint.ProbeAsync(host, port, cts.Token).ConfigureAwait(false))))
                .ConfigureAwait(false);
            var hit = hits.FirstOrDefault(h => h.Uri is not null);
            if (hit.Uri is null)
                return new OnvifId(null, null, false);

            try
            {
                var probe = await _onvif.ProbeAsync(new OnvifEndpoint(hit.Uri, creds), cts.Token).ConfigureAwait(false);
                return new OnvifId(probe, hit.Port, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The WCF probe doesn't run on every platform (Android) — the
                // port is still worth keeping.
                _logger.LogDebug(ex, "ONVIF probe failed for {Host}:{Port}", host, hit.Port);
                return new OnvifId(null, hit.Port, IsAuthError(ex));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ONVIF identify failed for {Host}", host);
            return new OnvifId(null, onvifPort, false);
        }
    }

    private sealed record StreamProbe(bool Ok, int Width, int Height, string? Codec, byte[]? Jpeg, bool Unauthorized, bool TimedOut, string? Error)
    {
        public static readonly StreamProbe None = new(false, 0, 0, null, null, false, false, null);
    }

    private async Task<StreamProbe> TryStreamAsync(Uri uri, CameraCredentials? creds, TimeSpan timeout, bool snapshot, CancellationToken ct)
    {
        // Same transport the live view will use — a UDP-only setup used to pass
        // playback but fail the test (which was hardwired to TCP).
        var transport = string.Equals(_settings.Current.RtspTransport, "udp", StringComparison.OrdinalIgnoreCase)
            ? RtspTransport.Udp
            : RtspTransport.Tcp;
        var options = VideoSessionOptions.Default(uri, creds) with { Transport = transport, AutoReconnect = false };
        var session = _engine.CreateSession(options);
        string? codec = null;
        using var telemetry = session.Telemetry.Subscribe(t => codec ??= t.Codec);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            // A failed open surfaces as a Failed state, not an exception — race it.
            var failed = session.StateChanged.Where(s => s == SessionState.Failed).Take(1).DefaultIfEmpty(SessionState.Idle).ToTask(cts.Token);
            await session.StartAsync(cts.Token).ConfigureAwait(false);
            var frameTask = session.Frames.Take(1).ToTask(cts.Token);
            var first = await Task.WhenAny(frameTask, failed).ConfigureAwait(false);
            if (first == failed)
            {
                await failed.ConfigureAwait(false);
                var err = session.LastError;
                return new StreamProbe(false, 0, 0, null, null, IsAuthMessage(err), false, err);
            }
            var frame = await frameTask.ConfigureAwait(false);
            byte[]? jpeg = null;
            if (snapshot)
            {
                // Telemetry (with the codec name) is published about once a
                // second, so it lags the first frame; give it a moment.
                for (var i = 0; i < 15 && Volatile.Read(ref codec) is null; i++)
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);

                try { jpeg = await session.SnapshotAsync(SnapshotFormat.Jpeg, cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Connect snapshot failed"); }
            }
            return new StreamProbe(true, frame.Width, frame.Height, codec, jpeg is { Length: > 0 } ? jpeg : null, false, false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StreamProbe(false, 0, 0, null, null, IsAuthMessage(session.LastError), true, session.LastError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Stream test failed for {Uri}", uri.GetLeftPart(UriPartial.Authority));
            return new StreamProbe(false, 0, 0, null, null, IsAuthMessage(ex.Message), false, ex.Message);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsAuthError(Exception ex) => IsAuthMessage(ex.Message) || (ex.InnerException is { } inner && IsAuthError(inner));

    private static bool IsAuthMessage(string? message) =>
        message is not null
        && (message.Contains("401", StringComparison.Ordinal)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not authorized", StringComparison.OrdinalIgnoreCase));
}
