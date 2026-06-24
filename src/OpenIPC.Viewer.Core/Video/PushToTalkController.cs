using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Audio;
using OpenIPC.Viewer.Core.Platform;

namespace OpenIPC.Viewer.Core.Video;

// Push-to-talk orchestrator (Phase 17.6): opens the backchannel, captures the
// mic, encodes each chunk to G.711, packetizes to RTP and pushes it to the
// camera. Lives in Core (no package deps) so it's testable with fake input /
// backchannel; the native capture and RTSP wiring are injected.
public sealed class PushToTalkController : IAsyncDisposable
{
    private readonly IAudioInput _input;
    private readonly IAudioBackchannelClient _client;
    private readonly object _gate = new();

    private IAudioBackchannelSession? _session;
    private RtpPacketizer? _rtp;
    private bool _aLaw;
    private volatile bool _talking;
    private int _teardownPending;

    public PushToTalkController(IAudioInput input, IAudioBackchannelClient client)
    {
        _input = input;
        _client = client;
    }

    // No mic / no native capture → the talk button hides.
    public bool IsAvailable => _input.IsAvailable;
    public bool IsTalking => _talking;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Error;

    // Capability probe (OPTIONS + DESCRIBE only) so the UI can show whether the
    // camera offers two-way audio before the user presses talk. Throws on a real
    // connection/auth failure — the caller treats that as "unknown".
    public Task<bool> ProbeAsync(BackchannelEndpoint endpoint, CancellationToken ct) =>
        _client.ProbeAsync(endpoint, ct);

    public async Task<TalkStartResult> StartAsync(BackchannelEndpoint endpoint, CancellationToken ct)
    {
        if (!_input.IsAvailable) return TalkStartResult.Failed;
        await StopAsync().ConfigureAwait(false);

        IAudioBackchannelSession? session;
        try
        {
            session = await _client.OpenAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            return TalkStartResult.Failed;
        }

        // Camera has no backchannel track — expected, not an error. The caller
        // shows a friendly "no two-way audio" hint; we don't raise Error.
        if (session is null) return TalkStartResult.Unsupported;

        lock (_gate)
        {
            _session = session;
            _aLaw = session.ALaw;
            _rtp = new RtpPacketizer(
                session.ALaw ? RtpPacketizer.PayloadTypePcma : RtpPacketizer.PayloadTypePcmu,
                ssrc: (uint)new Random().Next());
            _talking = true;
            _teardownPending = 0;
        }

        _input.FrameCaptured += OnFrame;
        _input.Start(session.SampleRate, channels: 1);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return TalkStartResult.Started;
    }

    private void OnFrame(byte[] pcm16)
    {
        IAudioBackchannelSession? session;
        RtpPacketizer? rtp;
        bool aLaw;
        lock (_gate)
        {
            session = _session;
            rtp = _rtp;
            aLaw = _aLaw;
        }
        if (session is null || rtp is null) return;

        try
        {
            var g711 = G711.Encode(pcm16, aLaw);
            if (g711.Length == 0) return;
            var packet = rtp.Packetize(g711, samplesInPayload: g711.Length);
            // Sync wait: G.711 chunks are tiny (~160 B / 20 ms) so the TCP write
            // returns fast; serialized inside the session.
            session.SendRtpAsync(packet, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Connection dropped mid-talk. Don't tear down on the capture thread
            // (Stop joins it → deadlock); hand off once.
            if (Interlocked.Exchange(ref _teardownPending, 1) == 0)
            {
                Error?.Invoke(this, ex.Message);
                _ = Task.Run(StopAsync);
            }
        }
    }

    public async Task StopAsync()
    {
        _input.FrameCaptured -= OnFrame;
        _input.Stop();

        IAudioBackchannelSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _rtp = null;
            _talking = false;
        }
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
