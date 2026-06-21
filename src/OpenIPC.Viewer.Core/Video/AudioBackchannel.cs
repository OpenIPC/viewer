using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Video;

// Push-to-talk backchannel (Phase 17.5). FFmpeg can't do ONVIF Profile T
// backchannel, so we open our own RTSP session that negotiates a sendonly audio
// track and push RTP into it. The contract lives in Core; the RTSP/RTP wiring is
// in the Devices layer like ONVIF/Majestic.
public sealed record BackchannelEndpoint(Uri RtspUri, CameraCredentials? Credentials);

public interface IAudioBackchannelClient
{
    // Performs the RTSP DESCRIBE/SETUP/PLAY handshake with the backchannel
    // Require header and returns a live session ready to accept RTP. Returns
    // null when the camera advertises no backchannel audio track (the common
    // "this camera has no speaker" case — not an error). Throws only on a real
    // connection / auth / protocol failure.
    Task<IAudioBackchannelSession?> OpenAsync(BackchannelEndpoint endpoint, CancellationToken ct);
}

// Outcome of PushToTalkController.StartAsync — lets the UI tell "camera has no
// two-way audio" (Unsupported) apart from a genuine failure (Failed, which also
// raises the Error event with the reason).
public enum TalkStartResult
{
    Started,
    Unsupported,
    Failed,
}

public interface IAudioBackchannelSession : IAsyncDisposable
{
    // Negotiated codec: A-law (PCMA, RTP PT 8) vs µ-law (PCMU, PT 0), and the
    // RTP clock rate (8000 Hz for G.711). The caller encodes/packetizes to match.
    bool ALaw { get; }
    int SampleRate { get; }

    // Send one already-built RTP packet to the camera. Thread-safe; serialized
    // internally so capture-thread sends don't interleave on the wire.
    Task SendRtpAsync(byte[] rtpPacket, CancellationToken ct);
}
