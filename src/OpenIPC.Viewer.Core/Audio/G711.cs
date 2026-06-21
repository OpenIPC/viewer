using System;

namespace OpenIPC.Viewer.Core.Audio;

// G.711 companding (Phase 17.6). Pure C# — backchannel mic audio is almost always
// G.711 µ-law (PCMU, RTP payload type 0) or A-law (PCMA, type 8), and encoding it
// needs no FFmpeg. Ported from the canonical CCITT/Sun reference (g711.c): µ-law
// works on 14-bit, A-law on 13-bit, both 8000 Hz mono in practice.
public static class G711
{
    private const int MuBias = 0x84;
    private const int MuClip = 8159;

    private static readonly short[] SegMuEnd = { 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF };
    private static readonly short[] SegAEnd = { 0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF };

    private static int Search(int val, short[] table)
    {
        for (var i = 0; i < table.Length; i++)
            if (val <= table[i]) return i;
        return table.Length;
    }

    public static byte EncodeMuLaw(short pcm)
    {
        int sample = pcm >> 2; // to 14-bit
        int mask;
        if (sample < 0) { sample = -sample; mask = 0x7F; }
        else mask = 0xFF;
        if (sample > MuClip) sample = MuClip;
        sample += MuBias >> 2;

        var seg = Search(sample, SegMuEnd);
        if (seg >= 8) return (byte)(0x7F ^ mask);
        var uval = (seg << 4) | ((sample >> (seg + 1)) & 0xF);
        return (byte)(uval ^ mask);
    }

    public static short DecodeMuLaw(byte mu)
    {
        int u = ~mu;
        int t = ((u & 0xF) << 3) + MuBias;
        t <<= (u & 0x70) >> 4;
        return (short)((u & 0x80) != 0 ? MuBias - t : t - MuBias);
    }

    public static byte EncodeALaw(short pcm)
    {
        int sample = pcm >> 3; // to 13-bit
        int mask;
        if (sample >= 0) { mask = 0xD5; }
        else { mask = 0x55; sample = -sample - 1; }

        var seg = Search(sample, SegAEnd);
        if (seg >= 8) return (byte)(0x7F ^ mask);
        var aval = seg << 4;
        aval |= seg < 2 ? (sample >> 1) & 0xF : (sample >> seg) & 0xF;
        return (byte)(aval ^ mask);
    }

    public static short DecodeALaw(byte a)
    {
        int val = a ^ 0x55;
        int t = (val & 0xF) << 4;
        var seg = (val & 0x70) >> 4;
        switch (seg)
        {
            case 0: t += 8; break;
            case 1: t += 0x108; break;
            default: t += 0x108; t <<= seg - 1; break;
        }
        return (short)((val & 0x80) != 0 ? t : -t);
    }

    // Bulk encode interleaved S16 PCM (mono expected) into G.711 bytes.
    public static byte[] Encode(ReadOnlySpan<byte> pcm16, bool aLaw)
    {
        var samples = pcm16.Length / 2;
        var outBuf = new byte[samples];
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            outBuf[i] = aLaw ? EncodeALaw(s) : EncodeMuLaw(s);
        }
        return outBuf;
    }
}
