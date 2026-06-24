using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Audio;

public class PushToTalkControllerTests
{
    private static readonly BackchannelEndpoint Endpoint = new(new Uri("rtsp://cam/stream"), null);

    [Fact]
    public async Task Start_OpensSessionAndCapturesAtNegotiatedFormat()
    {
        var input = new FakeInput();
        var client = new FakeClient(new FakeSession(aLaw: false, sampleRate: 8000));
        await using var ptt = new PushToTalkController(input, client);

        var result = await ptt.StartAsync(Endpoint, CancellationToken.None);

        Assert.Equal(TalkStartResult.Started, result);
        Assert.True(ptt.IsTalking);
        Assert.Equal((8000, 1), input.Started);
    }

    [Fact]
    public async Task NoBackchannelTrack_ReturnsUnsupportedAndDoesNotThrow()
    {
        var input = new FakeInput();
        await using var ptt = new PushToTalkController(input, new FakeClient(null));

        var result = await ptt.StartAsync(Endpoint, CancellationToken.None);

        Assert.Equal(TalkStartResult.Unsupported, result);
        Assert.False(ptt.IsTalking);
        Assert.Null(input.Started);
    }

    [Fact]
    public async Task CapturedFrame_IsEncodedAndSentAsRtp()
    {
        var session = new FakeSession(aLaw: false, sampleRate: 8000);
        var input = new FakeInput();
        await using var ptt = new PushToTalkController(input, new FakeClient(session));
        await ptt.StartAsync(Endpoint, CancellationToken.None);

        // 4 S16 samples → 4 G.711 bytes → RTP = 12-byte header + 4.
        input.Emit(new byte[] { 0, 0, 0, 1, 0, 2, 0, 3 });

        var packet = Assert.Single(session.Sent);
        Assert.Equal(12 + 4, packet.Length);
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0x00, packet[1]); // PT 0 (PCMU) since aLaw=false
    }

    [Fact]
    public async Task Stop_DisposesSessionAndStopsCapture()
    {
        var session = new FakeSession(aLaw: true, sampleRate: 8000);
        var input = new FakeInput();
        await using var ptt = new PushToTalkController(input, new FakeClient(session));
        await ptt.StartAsync(Endpoint, CancellationToken.None);

        await ptt.StopAsync();

        Assert.False(ptt.IsTalking);
        Assert.True(session.Disposed);
        Assert.True(input.Stopped);
        // Frames after stop are ignored.
        input.Emit(new byte[] { 0, 0 });
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task UnavailableMic_StartReturnsFailed()
    {
        var ptt = new PushToTalkController(new FakeInput { Available = false }, new FakeClient(new FakeSession(false, 8000)));
        Assert.Equal(TalkStartResult.Failed, await ptt.StartAsync(Endpoint, CancellationToken.None));
        Assert.False(ptt.IsTalking);
    }

    private sealed class FakeInput : IAudioInput
    {
        public bool Available = true;
        public bool IsAvailable => Available;
        public (int, int)? Started { get; private set; }
        public bool Stopped { get; private set; }
        public event Action<byte[]>? FrameCaptured;

        public void Start(int sampleRate, int channels) => Started = (sampleRate, channels);
        public void Stop() => Stopped = true;
        public void Emit(byte[] pcm) => FrameCaptured?.Invoke(pcm);
        public void Dispose() { }
    }

    private sealed class FakeClient : IAudioBackchannelClient
    {
        private readonly FakeSession? _session;
        public FakeClient(FakeSession? session) => _session = session;
        public Task<IAudioBackchannelSession?> OpenAsync(BackchannelEndpoint endpoint, CancellationToken ct)
            => Task.FromResult<IAudioBackchannelSession?>(_session);
        public Task<bool> ProbeAsync(BackchannelEndpoint endpoint, CancellationToken ct)
            => Task.FromResult(_session is not null);
    }

    private sealed class FakeSession : IAudioBackchannelSession
    {
        public FakeSession(bool aLaw, int sampleRate) { ALaw = aLaw; SampleRate = sampleRate; }
        public bool ALaw { get; }
        public int SampleRate { get; }
        public bool Disposed { get; private set; }
        public List<byte[]> Sent { get; } = new();

        public Task SendRtpAsync(byte[] rtpPacket, CancellationToken ct)
        {
            Sent.Add(rtpPacket);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
