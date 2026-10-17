using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Notifications;

// A short, fully decoded sound: interleaved signed-16-bit PCM — the format every
// IAudioOutput consumes. Used for the notification chime and the user's own
// sound file.
public sealed class PcmClip
{
    public PcmClip(byte[] pcm16, int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        Pcm16 = pcm16 ?? throw new ArgumentNullException(nameof(pcm16));
        SampleRate = sampleRate;
        Channels = channels;
    }

    public byte[] Pcm16 { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public int BytesPerSecond => SampleRate * Channels * 2;
    public TimeSpan Duration => TimeSpan.FromSeconds(Pcm16.Length / (double)BytesPerSecond);

    // A copy with every sample multiplied by volume (0..1). The gain is applied
    // on top of the system volume, never above unity, so it can't clip.
    public byte[] WithVolume(double volume)
    {
        var gain = volume < 0 ? 0 : volume > 1 ? 1 : volume;
        var output = new byte[Pcm16.Length];
        if (gain <= 0) return output;
        if (gain >= 1)
        {
            Buffer.BlockCopy(Pcm16, 0, output, 0, Pcm16.Length);
            return output;
        }
        for (var i = 0; i + 1 < Pcm16.Length; i += 2)
        {
            var s = (short)(Pcm16[i] | (Pcm16[i + 1] << 8));
            var scaled = (short)Math.Round(s * gain);
            output[i] = (byte)scaled;
            output[i + 1] = (byte)(scaled >> 8);
        }
        return output;
    }

    // Keeps at most maxLength of audio (a 10-minute song is not a notification).
    public PcmClip Truncate(TimeSpan maxLength)
    {
        var block = Channels * 2;
        var maxBytes = (long)(maxLength.TotalSeconds * SampleRate) * block;
        if (Pcm16.Length <= maxBytes) return this;
        var cut = new byte[maxBytes];
        Buffer.BlockCopy(Pcm16, 0, cut, 0, (int)maxBytes);
        return new PcmClip(cut, SampleRate, Channels);
    }
}

// Decodes any audio file (mp3, ogg, m4a, flac, wav, …) to a PcmClip. The
// FFmpeg-backed implementation lives in the Video project.
public interface IAudioClipDecoder
{
    Task<PcmClip> DecodeAsync(string path, TimeSpan maxLength, CancellationToken ct);
}

// Minimal RIFF/WAVE reader and writer. Reads PCM 8/16/24/32-bit and IEEE float
// 32-bit, mono or multichannel (downmixed to stereo); writes PCM 16-bit. Enough
// for notification sounds without dragging in a media framework.
public static class WavCodec
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static PcmClip Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadTag(r) != "RIFF") throw new InvalidDataException("Not a RIFF file");
        r.ReadUInt32();
        if (ReadTag(r) != "WAVE") throw new InvalidDataException("Not a WAVE file");

        ushort format = 0, channels = 0, bits = 0;
        uint rate = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var tag = ReadTag(r);
            var size = r.ReadUInt32();
            var next = stream.Position + size + (size & 1);
            if (tag == "fmt ")
            {
                format = r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = r.ReadUInt32();
                r.ReadUInt32(); // byte rate
                r.ReadUInt16(); // block align
                bits = r.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    r.ReadUInt16(); // cbSize
                    r.ReadUInt16(); // valid bits
                    r.ReadUInt32(); // channel mask
                    format = r.ReadUInt16(); // first two bytes of the sub-format GUID
                }
            }
            else if (tag == "data")
            {
                var available = (int)Math.Min(size, stream.Length - stream.Position);
                data = r.ReadBytes(available);
            }
            if (next > stream.Length) break;
            stream.Position = next;
        }

        if (data is null || channels == 0 || rate == 0)
            throw new InvalidDataException("WAV file has no audio data");
        if (format != FormatPcm && format != FormatFloat)
            throw new InvalidDataException($"Unsupported WAV encoding ({format}); use PCM or float");

        return ToClip(data, format, channels, bits, (int)rate);
    }

    public static void Write(Stream stream, PcmClip clip)
    {
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var blockAlign = (ushort)(clip.Channels * 2);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + clip.Pcm16.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write(FormatPcm);
        w.Write((ushort)clip.Channels);
        w.Write(clip.SampleRate);
        w.Write(clip.SampleRate * blockAlign);
        w.Write(blockAlign);
        w.Write((ushort)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(clip.Pcm16.Length);
        w.Write(clip.Pcm16);
    }

    private static string ReadTag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));

    private static PcmClip ToClip(byte[] data, ushort format, int channels, int bits, int rate)
    {
        var bytesPerSample = bits / 8;
        if (bytesPerSample is < 1 or > 4 || (format == FormatFloat && bits != 32))
            throw new InvalidDataException($"Unsupported WAV sample size ({bits} bit)");

        var frames = data.Length / (bytesPerSample * channels);
        var outChannels = channels == 1 ? 1 : 2;

        // Already the target format — copy verbatim (bit-exact, and fast).
        if (format == FormatPcm && bits == 16 && channels <= 2)
        {
            var exact = new byte[frames * channels * 2];
            Buffer.BlockCopy(data, 0, exact, 0, exact.Length);
            return new PcmClip(exact, rate, channels);
        }
        var pcm = new byte[frames * outChannels * 2];
        var o = 0;
        for (var f = 0; f < frames; f++)
        {
            var frameBase = f * bytesPerSample * channels;
            if (outChannels == 1)
            {
                o = Put(pcm, o, Sample(data, frameBase, format, bytesPerSample));
                continue;
            }
            // Stereo out: channel 0 → L, channel 1 → R; extra channels are
            // averaged into both so a 5.1 file still sounds complete.
            double left = Sample(data, frameBase, format, bytesPerSample);
            double right = Sample(data, frameBase + bytesPerSample, format, bytesPerSample);
            if (channels > 2)
            {
                double extra = 0;
                for (var c = 2; c < channels; c++)
                    extra += Sample(data, frameBase + c * bytesPerSample, format, bytesPerSample);
                extra /= channels - 2;
                left = (left + extra) / 2;
                right = (right + extra) / 2;
            }
            o = Put(pcm, o, left);
            o = Put(pcm, o, right);
        }
        return new PcmClip(pcm, rate, outChannels);
    }

    // One sample normalized to -1..1.
    private static double Sample(byte[] d, int i, ushort format, int bytes)
    {
        if (format == FormatFloat)
            return BitConverter.ToSingle(d, i);
        return bytes switch
        {
            1 => (d[i] - 128) / 128.0,
            2 => (short)(d[i] | (d[i + 1] << 8)) / 32768.0,
            3 => ((d[i] << 8 | d[i + 1] << 16 | d[i + 2] << 24) >> 8) / 8388608.0,
            _ => (d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24) / 2147483648.0,
        };
    }

    // Same 32768 scale as Sample(), so full-scale values map back exactly.
    private static int Put(byte[] pcm, int o, double sample)
    {
        var scaled = Math.Round(sample * 32768);
        var s = (short)(scaled < short.MinValue ? short.MinValue : scaled > short.MaxValue ? short.MaxValue : scaled);
        pcm[o] = (byte)s;
        pcm[o + 1] = (byte)(s >> 8);
        return o + 2;
    }
}

// The built-in notification sound: a short two-note chime (E6 → A6) with a soft
// attack and exponential decay, synthesized at runtime so no audio asset has to
// ship with every head.
public static class NotificationChime
{
    public const int SampleRate = 48000;

    public static PcmClip Generate()
    {
        var notes = new (double freq, double start, double length)[]
        {
            (1318.51, 0.00, 0.45),
            (1760.00, 0.16, 0.60),
        };
        const double total = 0.80;
        var frames = (int)(total * SampleRate);
        var mix = new double[frames];

        foreach (var (freq, start, length) in notes)
        {
            var from = (int)(start * SampleRate);
            var count = (int)(length * SampleRate);
            for (var n = 0; n < count && from + n < frames; n++)
            {
                var t = n / (double)SampleRate;
                var attack = Math.Min(1.0, t / 0.008);
                var decay = Math.Exp(-t * 6.5);
                // Fundamental plus a quiet octave for a bell-like timbre.
                var wave = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(4 * Math.PI * freq * t);
                mix[from + n] += 0.32 * attack * decay * wave;
            }
        }

        var pcm = new byte[frames * 4]; // stereo S16
        for (var i = 0; i < frames; i++)
        {
            var v = mix[i] < -1 ? -1 : mix[i] > 1 ? 1 : mix[i];
            var s = (short)Math.Round(v * 32767);
            var o = i * 4;
            pcm[o] = (byte)s;
            pcm[o + 1] = (byte)(s >> 8);
            pcm[o + 2] = (byte)s;
            pcm[o + 3] = (byte)(s >> 8);
        }
        return new PcmClip(pcm, SampleRate, 2);
    }
}
