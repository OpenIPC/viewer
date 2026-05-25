namespace OpenIPC.Viewer.Core.Video;

public sealed record HwProbeResult(bool Available, string? Reason)
{
    public static HwProbeResult Ok() => new(true, null);
    public static HwProbeResult Unavailable(string reason) => new(false, reason);
}
