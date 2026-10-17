using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Notifications;

public class EventActionTemplateTests
{
    private static EventActionContext Ctx(string? summary = "person ×2", float? confidence = 0.874f, int objects = 2) => new(
        new CameraEvent(new EventId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new CameraId(Guid.Parse("22222222-2222-2222-2222-222222222222")), EventKind.Detection, EventSeverity.Info,
            new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc), null, "analytics", summary, confidence, objects),
        "Gate \"A\"", "Object detected", new DateTime(2026, 10, 17, 9, 30, 12, DateTimeKind.Utc));

    [Fact]
    public void Render_ExpandsKnownPlaceholders()
    {
        var text = EventActionTemplate.Render("{title}: {camera} {kind} {summary} {confidence}% x{objects} {duration}s {timeUtc}", Ctx());
        Assert.Equal("Object detected: Gate \"A\" detection person ×2 87% x2 12s 2026-10-17T09:30:00Z", text);
    }

    [Fact]
    public void Render_LeavesUnknownBracesAlone()
    {
        Assert.Equal("{nope} {} {", EventActionTemplate.Render("{nope} {} {", Ctx()));
    }

    [Fact]
    public void Render_JsonBody_StaysValidJson()
    {
        var body = EventActionTemplate.Render(EventActionSettings.DefaultHttpBody, Ctx(), EventActionTemplate.JsonEscape);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("detection", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("Gate \"A\"", doc.RootElement.GetProperty("camera").GetString());
        Assert.Equal("87", doc.RootElement.GetProperty("confidence").GetString());
    }

    [Fact]
    public void Render_MissingConfidence_IsEmpty()
    {
        Assert.Equal("[]", EventActionTemplate.Render("[{confidence}]", Ctx(confidence: null)));
    }

    [Fact]
    public void SplitAddresses_HandlesBothSeparators()
    {
        Assert.Equal(new[] { "a@x.ru", "b@y.com", "c@z.org" },
            EventActionTemplate.SplitAddresses(" a@x.ru; b@y.com ,c@z.org;; "));
    }

    [Fact]
    public void AnyChannelEnabled_NeedsConfiguration()
    {
        Assert.False(new EventActionSettings(SignalEnabled: true).AnyChannelEnabled); // no host/port
        Assert.True(new EventActionSettings(SignalEnabled: true, SignalHost: "10.0.0.5", SignalPort: 4000).AnyChannelEnabled);
        Assert.False(new EventActionSettings(HttpUrl: "http://x").AnyChannelEnabled);     // not enabled
    }
}

public class SignalPayloadTests
{
    [Fact]
    public void Text_WithEscapes()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("ALARM 1\r\n"), SignalPayload.Encode("ALARM 1\\r\\n"));
    }

    [Fact]
    public void Text_RawByteEscape_IsNotUtf8Encoded()
    {
        Assert.Equal(new byte[] { (byte)'A', 0xFF, (byte)'B' }, SignalPayload.Encode("A\\xFFB"));
    }

    [Fact]
    public void Text_Cyrillic_IsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("Тревога"), SignalPayload.Encode("Тревога"));
    }

    [Fact]
    public void Hex_IgnoresSeparators()
    {
        Assert.Equal(new byte[] { 0xA0, 0x01, 0x01, 0xA2 }, SignalPayload.Encode("hex: A0 01-01:a2"));
    }

    [Fact]
    public void Hex_OddDigits_Throws()
    {
        Assert.Throws<FormatException>(() => SignalPayload.Encode("hex:ABC"));
    }
}

public class PcmClipTests
{
    [Fact]
    public void Wav_RoundTrip()
    {
        var pcm = new byte[] { 0x00, 0x10, 0xFF, 0x7F, 0x00, 0x80, 0x34, 0x12 };
        var clip = new PcmClip(pcm, 22050, 2);
        using var ms = new MemoryStream();
        WavCodec.Write(ms, clip);
        ms.Position = 0;

        var back = WavCodec.Read(ms);
        Assert.Equal(22050, back.SampleRate);
        Assert.Equal(2, back.Channels);
        Assert.Equal(pcm, back.Pcm16);
    }

    [Fact]
    public void Wav_8BitMono_IsConvertedTo16Bit()
    {
        var bytes = BuildWav(format: 1, channels: 1, rate: 8000, bits: 8, data: new byte[] { 128, 255, 0 });
        var clip = WavCodec.Read(new MemoryStream(bytes));
        Assert.Equal(1, clip.Channels);
        var samples = Samples(clip);
        Assert.Equal(0, samples[0]);
        Assert.Equal(32512, samples[1]); // 127/128 of full scale
        Assert.Equal(-32768, samples[2]);
    }

    [Fact]
    public void Wav_FloatSurround_DownmixesToStereo()
    {
        var data = new byte[4 * 4]; // one frame, 4 float channels
        BitConverter.GetBytes(0.5f).CopyTo(data, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(data, 4);
        var clip = WavCodec.Read(new MemoryStream(BuildWav(3, 4, 48000, 32, data)));
        Assert.Equal(2, clip.Channels);
        var s = Samples(clip);
        Assert.True(s[0] > 0 && s[1] < 0);
    }

    [Fact]
    public void Wav_NotRiff_Throws()
    {
        Assert.Throws<InvalidDataException>(() => WavCodec.Read(new MemoryStream(Encoding.ASCII.GetBytes("ID3 not a wav file at all"))));
    }

    [Fact]
    public void Volume_ScalesAndClamps()
    {
        var clip = new PcmClip(new byte[] { 0x00, 0x40, 0x00, 0xC0 }, 48000, 1); // +16384, -16384
        var half = Samples(new PcmClip(clip.WithVolume(0.5), 48000, 1));
        Assert.Equal(new short[] { 8192, -8192 }, half);
        Assert.Equal(clip.Pcm16, clip.WithVolume(3));
        Assert.All(clip.WithVolume(0), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Truncate_KeepsWholeFrames()
    {
        var clip = new PcmClip(new byte[48000 * 4 * 3], 48000, 2); // 3 s stereo
        var cut = clip.Truncate(TimeSpan.FromSeconds(1));
        Assert.Equal(48000 * 4, cut.Pcm16.Length);
        Assert.Same(clip, clip.Truncate(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Chime_IsShortStereoAndNotSilent()
    {
        var chime = NotificationChime.Generate();
        Assert.Equal(2, chime.Channels);
        Assert.InRange(chime.Duration.TotalSeconds, 0.5, 1.5);
        Assert.Contains(Samples(chime), s => Math.Abs((int)s) > 3000);
    }

    private static short[] Samples(PcmClip c) =>
        Enumerable.Range(0, c.Pcm16.Length / 2).Select(i => (short)(c.Pcm16[2 * i] | (c.Pcm16[2 * i + 1] << 8))).ToArray();

    private static byte[] BuildWav(ushort format, ushort channels, int rate, ushort bits, byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var block = (ushort)(channels * bits / 8);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length + 8); // + a LIST chunk before data, to test chunk skipping
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write(format);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * block);
        w.Write(block);
        w.Write(bits);
        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write(0);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}
