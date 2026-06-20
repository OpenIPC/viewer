using OpenIPC.Viewer.Core.Audio;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Audio;

public class RtpPacketizerTests
{
    [Fact]
    public void Header_HasCorrectVersionPayloadTypeAndFields()
    {
        var rtp = new RtpPacketizer(RtpPacketizer.PayloadTypePcmu, ssrc: 0x11223344, initialSequence: 7, initialTimestamp: 1000);
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        var pkt = rtp.Packetize(payload, marker: true);

        Assert.Equal(12 + 3, pkt.Length);
        Assert.Equal(0x80, pkt[0]);                 // V=2, no padding/ext/CSRC
        Assert.Equal(0x80, pkt[1]);                 // marker set, PT=0 (PCMU)
        Assert.Equal(0, pkt[2]); Assert.Equal(7, pkt[3]); // sequence 7 big-endian
        // timestamp 1000 = 0x000003E8 big-endian
        Assert.Equal(0x00, pkt[4]); Assert.Equal(0x00, pkt[5]); Assert.Equal(0x03, pkt[6]); Assert.Equal(0xE8, pkt[7]);
        // ssrc big-endian
        Assert.Equal(0x11, pkt[8]); Assert.Equal(0x22, pkt[9]); Assert.Equal(0x33, pkt[10]); Assert.Equal(0x44, pkt[11]);
        Assert.Equal(payload, pkt[12..]);
    }

    [Fact]
    public void Sequence_AndTimestamp_AdvancePerPacket()
    {
        var rtp = new RtpPacketizer(RtpPacketizer.PayloadTypePcma, ssrc: 1);
        var payload = new byte[160]; // 20 ms of 8 kHz G.711

        rtp.Packetize(payload);
        Assert.Equal(1, rtp.NextSequence);
        Assert.Equal((uint)160, rtp.NextTimestamp);

        var second = rtp.Packetize(payload);
        Assert.Equal(0x08, second[1]);              // PT=8 (PCMA), no marker
        Assert.Equal(1, second[3]);                 // second packet uses sequence 1
        Assert.Equal(2, rtp.NextSequence);
        Assert.Equal((uint)320, rtp.NextTimestamp);
    }

    [Fact]
    public void Sequence_WrapsAtUInt16()
    {
        var rtp = new RtpPacketizer(RtpPacketizer.PayloadTypePcmu, ssrc: 1, initialSequence: 65535);
        rtp.Packetize(new byte[1]);
        Assert.Equal(0, rtp.NextSequence);
    }
}
