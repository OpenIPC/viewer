using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Video;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Video;

public class AudioMonitorTests
{
    [Fact]
    public void DefaultsToMuted()
    {
        using var m = new AudioMonitor(new FakeOutput());
        Assert.True(m.Muted);
        Assert.Equal(1.0f, m.Volume);
    }

    [Fact]
    public void MutedDropsAudio_NothingWritten()
    {
        var output = new FakeOutput();
        using var m = new AudioMonitor(output) { Muted = true };
        var session = new FakeSession();
        m.Attach(session, Cam());

        session.Push(Frame(new byte[] { 1, 0, 2, 0 }));

        Assert.Empty(output.Writes);
    }

    [Fact]
    public void UnmutedFullVolume_PassesThroughAndStartsDevice()
    {
        var output = new FakeOutput();
        using var m = new AudioMonitor(output) { Muted = false };
        var session = new FakeSession();
        m.Attach(session, Cam());

        var pcm = new byte[] { 10, 0, 20, 0 };
        session.Push(Frame(pcm, sampleRate: 48000, channels: 2));

        Assert.Equal((48000, 2), output.Started);
        var written = Assert.Single(output.Writes);
        Assert.Equal(pcm, written);
    }

    [Fact]
    public void HalfVolume_ScalesSamples()
    {
        // Two S16 samples: 100 and -100 → halved to 50 and -50.
        var pcm = BitConverter.GetBytes((short)100);
        var neg = BitConverter.GetBytes((short)-100);
        var input = new byte[] { pcm[0], pcm[1], neg[0], neg[1] };

        var scaled = AudioMonitor.ApplyGain(input, 0.5f);

        Assert.Equal((short)50, BitConverter.ToInt16(scaled, 0));
        Assert.Equal((short)-50, BitConverter.ToInt16(scaled, 2));
    }

    [Fact]
    public void OneSourcePolicy_AttachingSecondSilencesFirst()
    {
        var output = new FakeOutput();
        using var m = new AudioMonitor(output) { Muted = false };
        var first = new FakeSession();
        var second = new FakeSession();

        m.Attach(first, Cam());
        Assert.Equal(0, output.StopCount);

        m.Attach(second, Cam());
        Assert.Equal(1, output.StopCount); // first was torn down
        Assert.Equal(second.Id, m.AttachedCamera);

        // Frames from the now-detached first source are ignored.
        output.Writes.Clear();
        first.Push(Frame(new byte[] { 1, 0 }));
        Assert.Empty(output.Writes);

        second.Push(Frame(new byte[] { 2, 0 }));
        Assert.Single(output.Writes);
    }

    [Fact]
    public void UnavailableOutput_AttachIsNoOp()
    {
        var output = new FakeOutput { Available = false };
        using var m = new AudioMonitor(output);
        var session = new FakeSession();

        m.Attach(session, Cam());

        Assert.Null(m.AttachedCamera);
        session.Push(Frame(new byte[] { 1, 0 }));
        Assert.Empty(output.Writes);
    }

    [Fact]
    public void Detach_StopsOutputAndIgnoresLaterFrames()
    {
        var output = new FakeOutput();
        using var m = new AudioMonitor(output) { Muted = false };
        var session = new FakeSession();
        m.Attach(session, Cam());

        m.Detach();
        Assert.Equal(1, output.StopCount);
        Assert.Null(m.AttachedCamera);

        session.Push(Frame(new byte[] { 1, 0 }));
        Assert.Empty(output.Writes);
    }

    private static CameraId Cam() => new(Guid.NewGuid());

    private static AudioFrame Frame(byte[] pcm, int sampleRate = 48000, int channels = 2) =>
        new(pcm, sampleRate, channels, 0);

    private sealed class FakeOutput : IAudioOutput
    {
        public bool Available = true;
        public bool IsAvailable => Available;
        public (int, int)? Started { get; private set; }
        public int StopCount { get; private set; }
        public List<byte[]> Writes { get; } = new();

        public void Start(int sampleRate, int channels) => Started = (sampleRate, channels);
        public void Write(ReadOnlySpan<byte> pcm16) => Writes.Add(pcm16.ToArray());
        public void Stop() => StopCount++;
        public void Dispose() { }
    }

    private sealed class FakeSession : IVideoSession
    {
        public CameraId Id { get; } = new(Guid.NewGuid());
        private readonly PushObservable _audio = new();

        public void Push(AudioFrame f) => _audio.Push(f);

        public SessionState State => SessionState.Playing;
        public string? LastError => null;
        public IObservable<SessionState> StateChanged { get; } = new PushObservable<SessionState>();
        public IObservable<VideoFrame> Frames { get; } = new PushObservable<VideoFrame>();
        public IObservable<AudioFrame> AudioFrames => _audio;
        public IObservable<SessionTelemetry> Telemetry { get; } = new PushObservable<SessionTelemetry>();
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
        public void PauseDecode() { }
        public void Resume() { }
        public void SetAudioEnabled(bool enabled) { }
        public ValueTask DisposeAsync() => default;
    }

    private sealed class PushObservable : PushObservable<AudioFrame> { }

    private class PushObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = new();

        public void Push(T value)
        {
            foreach (var o in _observers.ToArray()) o.OnNext(value);
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            _observers.Add(observer);
            return new Unsub(_observers, observer);
        }

        private sealed class Unsub : IDisposable
        {
            private readonly List<IObserver<T>> _list;
            private readonly IObserver<T> _o;
            public Unsub(List<IObserver<T>> list, IObserver<T> o) { _list = list; _o = o; }
            public void Dispose() => _list.Remove(_o);
        }
    }
}
