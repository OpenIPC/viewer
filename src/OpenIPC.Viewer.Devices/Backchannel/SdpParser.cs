using System;

namespace OpenIPC.Viewer.Devices.Backchannel;

// Just enough SDP to locate the ONVIF backchannel audio track (Phase 17.5). The
// backchannel media is the audio m= section marked `a=sendonly` (the camera's
// view: it sends nothing, we push to it — same heuristic go2rtc uses). We pull
// its payload type / codec / clock rate and control URL.
internal static class SdpParser
{
    public readonly record struct BackchannelTrack(bool ALaw, int SampleRate, string Control);

    public static BackchannelTrack? FindBackchannelAudio(string sdp)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n');

        var inAudio = false;
        var sendonly = false;
        var payloadType = -1;
        var control = "";
        bool? aLaw = null;
        var sampleRate = 8000;

        BackchannelTrack? Finish()
        {
            if (inAudio && sendonly && payloadType >= 0)
            {
                // Default by static payload type when no rtpmap was given:
                // 0 = PCMU, 8 = PCMA.
                var isA = aLaw ?? payloadType == 8;
                return new BackchannelTrack(isA, sampleRate, control);
            }
            return null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                // Close out a previous audio section if it qualified.
                var done = Finish();
                if (done is not null) return done;

                inAudio = line.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase);
                sendonly = false;
                payloadType = -1;
                control = "";
                aLaw = null;
                sampleRate = 8000;

                if (inAudio)
                {
                    // "m=audio 0 RTP/AVP 8" → first fmt is the payload type.
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4) int.TryParse(parts[3], out payloadType);
                }
                continue;
            }

            if (!inAudio) continue;

            if (line.Equals("a=sendonly", StringComparison.OrdinalIgnoreCase))
                sendonly = true;
            else if (line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
                control = line["a=control:".Length..].Trim();
            else if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                // "a=rtpmap:8 PCMA/8000" / "a=rtpmap:0 PCMU/8000"
                var val = line["a=rtpmap:".Length..];
                var space = val.IndexOf(' ');
                if (space > 0)
                {
                    var codec = val[(space + 1)..];
                    if (codec.StartsWith("PCMA", StringComparison.OrdinalIgnoreCase)) aLaw = true;
                    else if (codec.StartsWith("PCMU", StringComparison.OrdinalIgnoreCase)) aLaw = false;
                    var slash = codec.IndexOf('/');
                    if (slash >= 0)
                    {
                        var rest = codec[(slash + 1)..];
                        var end = 0;
                        while (end < rest.Length && char.IsDigit(rest[end])) end++;
                        if (end > 0 && int.TryParse(rest[..end], out var sr)) sampleRate = sr;
                    }
                }
            }
        }

        return Finish();
    }
}
