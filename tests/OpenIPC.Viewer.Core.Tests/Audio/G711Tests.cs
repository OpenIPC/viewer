using OpenIPC.Viewer.Core.Audio;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Audio;

public class G711Tests
{
    // G.711 is lossy, but decoding is the exact inverse of encoding on the
    // already-quantized set: decode(encode(decode(b))) == decode(b) for every
    // possible code byte. This proves the encode/decode tables are consistent.
    [Fact]
    public void MuLaw_IsIdempotentOnQuantizedValues()
    {
        for (var b = 0; b < 256; b++)
        {
            var sample = G711.DecodeMuLaw((byte)b);
            var reencoded = G711.EncodeMuLaw(sample);
            Assert.Equal(G711.DecodeMuLaw((byte)b), G711.DecodeMuLaw(reencoded));
        }
    }

    [Fact]
    public void ALaw_IsIdempotentOnQuantizedValues()
    {
        for (var b = 0; b < 256; b++)
        {
            var sample = G711.DecodeALaw((byte)b);
            var reencoded = G711.EncodeALaw(sample);
            Assert.Equal(G711.DecodeALaw((byte)b), G711.DecodeALaw(reencoded));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1000)]
    [InlineData(16000)]
    [InlineData(-16000)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void RoundTrip_StaysWithinQuantizationBand(short sample)
    {
        var mu = G711.DecodeMuLaw(G711.EncodeMuLaw(sample));
        var a = G711.DecodeALaw(G711.EncodeALaw(sample));
        // Companding error grows with magnitude; ~5% of full scale is a safe ceiling.
        const int tolerance = 2048;
        Assert.True(System.Math.Abs(mu - sample) <= tolerance, $"µ-law off by {mu - sample}");
        Assert.True(System.Math.Abs(a - sample) <= tolerance, $"A-law off by {a - sample}");
    }

    [Fact]
    public void Encode_ProducesOneBytePerSample()
    {
        var pcm = new byte[] { 0, 0, 0, 1, 0, 2 }; // 3 S16 samples
        Assert.Equal(3, G711.Encode(pcm, aLaw: false).Length);
        Assert.Equal(3, G711.Encode(pcm, aLaw: true).Length);
    }
}
