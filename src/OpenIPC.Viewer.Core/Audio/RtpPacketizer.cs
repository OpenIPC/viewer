using System;

namespace OpenIPC.Viewer.Core.Audio;

// Minimal RTP packetizer for the backchannel uplink (Phase 17.5). FFmpeg doesn't
// do ONVIF backchannel, so we build RTP ourselves. Fixed 12-byte header, no CSRCs
// / extensions. For G.711 the RTP timestamp advances by the sample count, which
// for 8-bit companded audio equals the payload byte count.
public sealed class RtpPacketizer
{
    public const byte PayloadTypePcmu = 0; // G.711 µ-law
    public const byte PayloadTypePcma = 8; // G.711 A-law

    private readonly byte _payloadType;
    private readonly uint _ssrc;
    private ushort _sequence;
    private uint _timestamp;

    public RtpPacketizer(byte payloadType, uint ssrc, ushort initialSequence = 0, uint initialTimestamp = 0)
    {
        _payloadType = payloadType;
        _ssrc = ssrc;
        _sequence = initialSequence;
        _timestamp = initialTimestamp;
    }

    public ushort NextSequence => _sequence;
    public uint NextTimestamp => _timestamp;

    // Build one RTP packet wrapping the payload, advancing the sequence number
    // and (by samplesInPayload, or payload length when omitted) the timestamp.
    public byte[] Packetize(ReadOnlySpan<byte> payload, bool marker = false, int? samplesInPayload = null)
    {
        var packet = new byte[12 + payload.Length];

        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)((marker ? 0x80 : 0x00) | (_payloadType & 0x7F));
        WriteUInt16(packet, 2, _sequence);
        WriteUInt32(packet, 4, _timestamp);
        WriteUInt32(packet, 8, _ssrc);
        payload.CopyTo(packet.AsSpan(12));

        unchecked
        {
            _sequence++;
            _timestamp += (uint)(samplesInPayload ?? payload.Length);
        }
        return packet;
    }

    private static void WriteUInt16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}
